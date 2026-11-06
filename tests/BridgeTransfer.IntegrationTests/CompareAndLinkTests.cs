using BridgeTransfer.Core.Comparison;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.IntegrationTests;

public class CompareAndLinkTests
{
    [FixtureFact]
    public async Task Compare_then_apply_selected_one_way_plan_and_compare_again()
    {
        await using var ws = new Workspace();
        var src = Path.Combine(ws.LocalDir, "src");
        var dst = Path.Combine(ws.RemoteDir, "dst");
        Directory.CreateDirectory(Path.Combine(src, "sub", "newfolder"));
        Directory.CreateDirectory(Path.Combine(dst, "sub"));
        var old = DateTime.UtcNow.AddDays(-10);
        var newer = DateTime.UtcNow.AddDays(-1);

        void L(string rel, string text, DateTime t)
        {
            var p = Path.Combine(src, rel);
            File.WriteAllText(p, text);
            File.SetLastWriteTimeUtc(p, t);
        }

        void R(string rel, string text, DateTime t)
        {
            var p = Path.Combine(dst, rel);
            File.WriteAllText(p, text);
            File.SetLastWriteTimeUtc(p, t);
        }

        L("only-src.txt", "new file", old);
        L("sub/src-newer.txt", "updated content", newer);
        R("sub/src-newer.txt", "stale", old);
        L("dst-newer.txt", "older local", old);
        R("dst-newer.txt", "newer remote edit", newer);
        L("same.txt", "same", old);
        R("same.txt", "same", old);
        R("only-dst.txt", "keep me", old);
        Workspace.Sh($"chown -R {Env.User} '{ws.RemoteDir}'");

        var browse = await ws.ConnectAsync();
        var srcTree = await TreeScanner.ScanAsync(ws.Local, src, null, default);
        var dstTree = await TreeScanner.ScanAsync(browse, dst, null, default);
        var result = FolderComparer.Compare(srcTree, dstTree, TransferDirection.Upload);

        ComparisonRow Row(string rel) => result.Rows.Single(r => r.RelativePath == rel);
        Assert.Equal(ComparisonCategory.OnlyOnSource, Row("only-src.txt").Category);
        Assert.Equal(ComparisonCategory.SourceNewer, Row("sub/src-newer.txt").Category);
        Assert.Equal(ComparisonCategory.DestinationNewer, Row("dst-newer.txt").Category);
        Assert.Equal(ComparisonCategory.SameMetadata, Row("same.txt").Category);
        Assert.Equal(ComparisonCategory.OnlyOnDestination, Row("only-dst.txt").Category);
        Assert.Equal(ComparisonCategory.OnlyOnSource, Row("sub/newfolder").Category);
        Assert.Equal(new[] { "only-src.txt", "sub/newfolder", "sub/src-newer.txt" }, result.Selected.Select(r => r.RelativePath).OrderBy(x => x));

        // The comparison scan did not modify anything.
        Assert.Equal("stale", File.ReadAllText(Path.Combine(dst, "sub/src-newer.txt")));

        Assert.Equal(0, await SyncPlanBuilder.RevalidateAsync(result, ws.Local, browse, default));
        var items = SyncPlanBuilder.Build(result, ws.Local, browse);
        await using var q = ws.NewQueue();
        var job = q.EnqueueSync(ws.Key, TransferDirection.Upload, src, dst, items, browse.CombineRelative);
        Assert.Equal(TransferState.Completed, await Workspace.WaitFinalAsync(job));

        Assert.Equal("new file", File.ReadAllText(Path.Combine(dst, "only-src.txt")));
        Assert.Equal("updated content", File.ReadAllText(Path.Combine(dst, "sub/src-newer.txt")));
        Assert.Equal("newer remote edit", File.ReadAllText(Path.Combine(dst, "dst-newer.txt")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(dst, "only-dst.txt")));
        Assert.True(Directory.Exists(Path.Combine(dst, "sub/newfolder")));

        // Second comparison: everything copied now reads as Same metadata (timestamps were preserved).
        var again = FolderComparer.Compare(await TreeScanner.ScanAsync(ws.Local, src, null, default), await TreeScanner.ScanAsync(browse, dst, null, default), TransferDirection.Upload);
        Assert.Empty(again.Selected);
        Assert.Equal(ComparisonCategory.SameMetadata, again.Rows.Single(r => r.RelativePath == "only-src.txt").Category);
        Assert.Equal(ComparisonCategory.SameMetadata, again.Rows.Single(r => r.RelativePath == "sub/src-newer.txt").Category);
    }

    [FixtureFact]
    public async Task Changes_after_comparison_are_put_back_into_review_and_not_applied()
    {
        await using var ws = new Workspace();
        var src = Path.Combine(ws.RemoteDir, "src");
        var dst = Path.Combine(ws.LocalDir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "a.txt"), "A");
        File.WriteAllText(Path.Combine(src, "b.txt"), "B");
        File.WriteAllText(Path.Combine(src, "c.txt"), "C");
        Workspace.Sh($"chown -R {Env.User} '{ws.RemoteDir}'");
        var browse = await ws.ConnectAsync();
        var result = FolderComparer.Compare(await TreeScanner.ScanAsync(browse, src, null, default), await TreeScanner.ScanAsync(ws.Local, dst, null, default), TransferDirection.Download);
        Assert.Equal(3, result.Selected.Count());

        // a: source changes; b: destination appears.
        File.AppendAllText(Path.Combine(src, "a.txt"), "more");
        File.WriteAllText(Path.Combine(dst, "b.txt"), "local edit");
        Assert.Equal(2, await SyncPlanBuilder.RevalidateAsync(result, browse, ws.Local, default));
        Assert.Equal(new[] { "c.txt" }, result.Selected.Select(r => r.RelativePath));

        // Change after revalidation but before execution: c.txt appears locally → execution skips it.
        var items = SyncPlanBuilder.Build(result, browse, ws.Local);
        File.WriteAllText(Path.Combine(dst, "c.txt"), "created meanwhile");
        await using var q = ws.NewQueue();
        var job = q.EnqueueSync(ws.Key, TransferDirection.Download, src, dst, items, ws.Local.CombineRelative);
        await Workspace.WaitFinalAsync(job);
        Assert.Equal("created meanwhile", File.ReadAllText(Path.Combine(dst, "c.txt")));
        Assert.Equal(FailureKind.DestinationChanged, job.Files.Single().Failure);
        Assert.Equal("local edit", File.ReadAllText(Path.Combine(dst, "b.txt")));
    }

    [FixtureFact]
    public async Task Remote_links_are_shown_as_links_skipped_in_copies_and_never_written_through()
    {
        await using var ws = new Workspace();
        var tree = Path.Combine(ws.RemoteDir, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "real"));
        File.WriteAllText(Path.Combine(tree, "real", "f.txt"), "f");
        var outside = Path.Combine(ws.RemoteDir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside the tree");
        File.CreateSymbolicLink(Path.Combine(tree, "link-to-outside"), outside);
        File.CreateSymbolicLink(Path.Combine(tree, "file-link.txt"), Path.Combine(outside, "secret.txt"));
        File.CreateSymbolicLink(Path.Combine(tree, "loop"), tree);
        Workspace.Sh($"chown -hR {Env.User} '{ws.RemoteDir}'");
        var browse = await ws.ConnectAsync();

        var entries = await browse.ListAsync(tree, default);
        Assert.Equal(EntryKind.SymbolicLink, entries.Single(e => e.Name == "link-to-outside").Kind);
        Assert.Equal(EntryKind.SymbolicLink, entries.Single(e => e.Name == "file-link.txt").Kind);
        Assert.Equal(EntryKind.SymbolicLink, (await browse.GetEntryAsync(tree + "/file-link.txt", default))!.Kind);
        Assert.Equal(EntryKind.SymbolicLink, (await browse.GetEntryAsync(tree + "/loop", default))!.Kind);
        Assert.Equal(EntryKind.Directory, (await browse.GetEntryAsync(tree + "/real", default))!.Kind);

        var dl = Path.Combine(ws.LocalDir, "dl");
        Directory.CreateDirectory(dl);
        await using var q = ws.NewQueue();
        var job = q.EnqueueItems(ws.Key, TransferDirection.Download, new[] { (await browse.GetEntryAsync(tree, default))! }, ws.RemoteDir, dl);
        Assert.Equal(TransferState.CompletedWithWarning, await Workspace.WaitFinalAsync(job));
        Assert.Equal(3, job.ScanSkipped.Count(s => s.Reason == SkipReason.SymbolicLink));
        Assert.False(File.Exists(Path.Combine(dl, "tree", "link-to-outside", "secret.txt")));
        Assert.True(File.Exists(Path.Combine(dl, "tree", "real", "f.txt")));

        // Uploading a folder whose destination name is a link is refused (no writing through links).
        var up = Path.Combine(ws.LocalDir, "link-to-outside");
        Directory.CreateDirectory(up);
        File.WriteAllText(Path.Combine(up, "x.txt"), "x");
        var job2 = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(up, default))! }, ws.LocalDir, tree);
        Assert.Equal(TransferState.Failed, await Workspace.WaitFinalAsync(job2));
        Assert.False(File.Exists(Path.Combine(outside, "x.txt")));

        // A file whose destination is a link cannot be replaced through it.
        File.WriteAllText(Path.Combine(ws.LocalDir, "file-link.txt"), "attempt");
        ConflictRequest? asked = null;
        q.ConflictRaised += r => asked = r;
        var job3 = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(Path.Combine(ws.LocalDir, "file-link.txt"), default))! }, ws.LocalDir, tree);
        await Workspace.WaitUntilAsync(() => asked is not null);
        Assert.False(asked!.ReplaceAllowed);
        q.ResolveConflict(asked, ConflictChoice.Skip, false);
        await Workspace.WaitFinalAsync(job3);
        Assert.Equal("outside the tree", File.ReadAllText(Path.Combine(outside, "secret.txt")));
    }

    [FixtureFact]
    public async Task Linux_names_invalid_on_windows_and_case_collisions_are_reported_not_renamed()
    {
        await using var ws = new Workspace();
        var tree = Path.Combine(ws.RemoteDir, "names");
        Directory.CreateDirectory(tree);
        foreach (var n in new[] { "ok.txt", "a:b.txt", "CON", "aux.log", "trailing.", "trailing ", "q?.txt", "back\\slash.txt", "Data.csv", "data.csv" })
        {
            File.WriteAllText(Path.Combine(tree, n), n);
        }

        Workspace.Sh($"chown -R {Env.User} '{ws.RemoteDir}'");
        var browse = await ws.ConnectAsync();
        var dl = Path.Combine(ws.LocalDir, "dl");
        Directory.CreateDirectory(dl);
        await using var q = ws.NewQueue();
        var job = q.EnqueueItems(ws.Key, TransferDirection.Download, new[] { (await browse.GetEntryAsync(tree, default))! }, ws.RemoteDir, dl);
        Assert.Equal(TransferState.CompletedWithWarning, await Workspace.WaitFinalAsync(job));
        Assert.Equal(new[] { "ok.txt" }, Directory.GetFiles(Path.Combine(dl, "names")).Select(Path.GetFileName));
        Assert.Equal(7, job.ScanSkipped.Count(s => s.Reason == SkipReason.UnsupportedName));
        Assert.Equal(2, job.ScanSkipped.Count(s => s.Reason == SkipReason.CaseCollision));

        var cmp = FolderComparer.Compare(await TreeScanner.ScanAsync(browse, tree, null, default), await TreeScanner.ScanAsync(ws.Local, Path.Combine(dl, "names"), null, default), TransferDirection.Download);
        Assert.Equal(9, cmp.Rows.Count(r => r.Category == ComparisonCategory.UnsupportedName));
    }

    [FixtureFact]
    public async Task Permission_denied_listing_makes_comparison_incomplete()
    {
        await using var ws = new Workspace();
        var src = Path.Combine(ws.RemoteDir, "src");
        Directory.CreateDirectory(Path.Combine(src, "locked"));
        File.WriteAllText(Path.Combine(src, "locked", "x.txt"), "x");
        File.WriteAllText(Path.Combine(src, "ok.txt"), "ok");
        Workspace.Sh($"chown -R {Env.User} '{src}' && chown root:root '{src}/locked' && chmod 700 '{src}/locked'");
        var dst = Path.Combine(ws.LocalDir, "dst");
        Directory.CreateDirectory(Path.Combine(dst, "locked"));
        File.WriteAllText(Path.Combine(dst, "locked", "x.txt"), "local");
        var browse = await ws.ConnectAsync();
        var result = FolderComparer.Compare(await TreeScanner.ScanAsync(browse, src, null, default), await TreeScanner.ScanAsync(ws.Local, dst, null, default), TransferDirection.Download);
        Assert.False(result.SourceComplete);
        Assert.Equal(ComparisonCategory.Incomplete, result.Rows.Single(r => r.RelativePath == "locked").Category);
        Assert.DoesNotContain(result.Rows, r => r.Category == ComparisonCategory.OnlyOnDestination);
        Assert.Equal(new[] { "ok.txt" }, result.Selected.Select(r => r.RelativePath));
    }

    [LargeFixtureFact]
    public async Task File_larger_than_4_GB_round_trips_with_identical_sha256()
    {
        await using var ws = new Workspace();
        var big = Path.Combine(ws.LocalDir, "huge.bin");
        const long size = 4L * 1024 * 1024 * 1024 + 123_457;
        Workspace.WriteRandom(big, size, 77);
        var browse = await ws.ConnectAsync();
        await using var q = ws.NewQueue();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var up = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(big, default))! }, ws.LocalDir, ws.RemoteDir);
        Assert.Equal(TransferState.Completed, await Workspace.WaitFinalAsync(up, 3_600_000));
        var upSeconds = sw.Elapsed.TotalSeconds;
        Assert.Equal(size, new FileInfo(Path.Combine(ws.RemoteDir, "huge.bin")).Length);
        Assert.Equal(size, up.Snapshot().TransferredBytes);
        var hash = Workspace.Sha256(big);
        Assert.Equal(hash, Workspace.Sha256(Path.Combine(ws.RemoteDir, "huge.bin")));
        File.Delete(big);

        var dl = Path.Combine(ws.LocalDir, "dl");
        Directory.CreateDirectory(dl);
        sw.Restart();
        var down = q.EnqueueItems(ws.Key, TransferDirection.Download, new[] { (await browse.GetEntryAsync(ws.RemoteDir + "/huge.bin", default))! }, ws.RemoteDir, dl);
        Assert.Equal(TransferState.Completed, await Workspace.WaitFinalAsync(down, 3_600_000));
        Assert.Equal(hash, Workspace.Sha256(Path.Combine(dl, "huge.bin")));
        Console.WriteLine($"4 GB+ upload {upSeconds:F0} s, download {sw.Elapsed.TotalSeconds:F0} s over loopback");
    }
}

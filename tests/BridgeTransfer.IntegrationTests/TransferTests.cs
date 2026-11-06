using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.IntegrationTests;

public class TransferTests
{
    private static void BuildTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "empty dir"));
        Directory.CreateDirectory(Path.Combine(root, "nested", "deeper", "deepest"));
        File.WriteAllBytes(Path.Combine(root, "empty.txt"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(root, "nested", "line endings.txt"), "unix\nwindows\r\nold mac\r");
        File.WriteAllText(Path.Combine(root, "nested", "deeper", "Ünïcödé 数据 ñ.csv"), "a,b\n1,2\n");
        Workspace.WriteRandom(Path.Combine(root, "nested", "deeper", "deepest", "binary.bin"), 3_500_000, 42);
        Workspace.WriteRandom(Path.Combine(root, "with spaces.dat"), 70_000, 9);
        var small = Path.Combine(root, "many small");
        Directory.CreateDirectory(small);
        for (var i = 0; i < 400; i++)
        {
            File.WriteAllBytes(Path.Combine(small, $"s{i:000}.txt"), System.Text.Encoding.UTF8.GetBytes($"file {i}\n"));
        }
    }

    [FixtureFact]
    public async Task Upload_and_download_tree_is_byte_identical_by_sha256()
    {
        await using var ws = new Workspace();
        var src = Path.Combine(ws.LocalDir, "project");
        BuildTree(src);
        var browse = await ws.ConnectAsync();
        await using var q = ws.NewQueue();

        var up = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(src, default))! }, ws.LocalDir, ws.RemoteDir);
        Assert.Equal(TransferState.Completed, await Workspace.WaitFinalAsync(up));
        Assert.Equal(Workspace.Manifest(src), Workspace.Manifest(Path.Combine(ws.RemoteDir, "project")));
        Assert.False(ws.RemoteHasStagingFiles());

        // Modification times are preserved (to the second).
        var srcTime = File.GetLastWriteTimeUtc(Path.Combine(src, "with spaces.dat"));
        var dstTime = File.GetLastWriteTimeUtc(Path.Combine(ws.RemoteDir, "project", "with spaces.dat"));
        Assert.True(Math.Abs((srcTime - dstTime).TotalSeconds) < 1);

        var back = Path.Combine(ws.LocalDir, "back");
        Directory.CreateDirectory(back);
        var down = q.EnqueueItems(ws.Key, TransferDirection.Download, new[] { (await browse.GetEntryAsync(ws.RemoteDir + "/project", default))! }, ws.RemoteDir, back);
        Assert.Equal(TransferState.Completed, await Workspace.WaitFinalAsync(down));
        Assert.Equal(Workspace.Manifest(src), Workspace.Manifest(Path.Combine(back, "project")));
        Assert.False(ws.LocalHasStagingFiles());
    }

    [FixtureFact]
    public async Task Cancel_midway_upload_and_download_leaves_destinations_intact()
    {
        await using var ws = new Workspace();
        var big = Path.Combine(ws.LocalDir, "big.bin");
        Workspace.WriteRandom(big, 400_000_000, 5);
        File.WriteAllText(Path.Combine(ws.RemoteDir, "big.bin"), "original remote");
        Workspace.Sh($"chown -R {Env.User} '{ws.RemoteDir}'");
        await ws.ConnectAsync();
        await using var q = ws.NewQueue();
        q.ConflictRaised += r => q.ResolveConflict(r, ConflictChoice.Replace, false);

        var up = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(big, default))! }, ws.LocalDir, ws.RemoteDir);
        await Workspace.WaitUntilAsync(() => up.Snapshot().TransferredBytes > 20_000_000);
        q.Cancel(up);
        Assert.Equal(TransferState.Cancelled, await Workspace.WaitFinalAsync(up));
        Assert.Equal("original remote", File.ReadAllText(Path.Combine(ws.RemoteDir, "big.bin")));
        await Workspace.WaitUntilAsync(() => !ws.RemoteHasStagingFiles(), 10_000);

        // Download cancellation.
        Workspace.WriteRandom(Path.Combine(ws.RemoteDir, "big2.bin"), 400_000_000, 6);
        Workspace.Sh($"chown -R {Env.User} '{ws.RemoteDir}'");
        var dl = Path.Combine(ws.LocalDir, "dl");
        Directory.CreateDirectory(dl);
        File.WriteAllText(Path.Combine(dl, "big2.bin"), "original local");
        var browse = await ws.ConnectAsync();
        var down = q.EnqueueItems(ws.Key, TransferDirection.Download, new[] { (await browse.GetEntryAsync(ws.RemoteDir + "/big2.bin", default))! }, ws.RemoteDir, dl);
        await Workspace.WaitUntilAsync(() => down.Snapshot().TransferredBytes > 20_000_000);
        q.Cancel(down);
        Assert.Equal(TransferState.Cancelled, await Workspace.WaitFinalAsync(down));
        Assert.Equal("original local", File.ReadAllText(Path.Combine(dl, "big2.bin")));
        Assert.False(ws.LocalHasStagingFiles());
    }

    [FixtureFact]
    public async Task Network_interruption_is_retried_with_reconnect_and_result_is_identical()
    {
        await using var ws = new Workspace();
        using var proxy = new TcpProxy(Env.Port);
        await ws.ConnectAsync(); // trusts the host key for 127.0.0.1:Env.Port
        // Trust the same key for the proxy port (the proxy forwards to the same server).
        var rec = ws.KnownHosts.GetAll().Single();
        var copy = new BridgeTransfer.Core.Security.KnownHostRecord { Host = rec.Host, Port = proxy.Port, Algorithm = rec.Algorithm, PublicKeyBase64 = rec.PublicKeyBase64, FingerprintSha256 = rec.FingerprintSha256 };
        ws.KnownHosts.Save(new[] { rec, copy });
        ws.Profile.Port = proxy.Port;
        ws.Key = ws.Key with { Port = proxy.Port };

        var file = Path.Combine(ws.LocalDir, "data.bin");
        Workspace.WriteRandom(file, 150_000_000, 11);
        await using var q = ws.NewQueue();
        var job = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(file, default))! }, ws.LocalDir, ws.RemoteDir);
        await Workspace.WaitUntilAsync(() => job.Snapshot().TransferredBytes > 30_000_000);
        proxy.SeverAll();

        Assert.Equal(TransferState.Completed, await Workspace.WaitFinalAsync(job));
        Assert.True(job.Files[0].Attempts >= 1, "A retry should have happened.");
        Assert.Equal(Workspace.Sha256(file), Workspace.Sha256(Path.Combine(ws.RemoteDir, "data.bin")));
        Workspace.Sh($"sleep 1");
        Assert.False(ws.RemoteHasStagingFiles(), "Staging file from the interrupted attempt must be cleaned up.");
    }

    [FixtureFact]
    public async Task Existing_destinations_replace_keep_both_and_skip()
    {
        await using var ws = new Workspace();
        foreach (var n in new[] { "r.txt", "k.txt", "s.txt" })
        {
            File.WriteAllText(Path.Combine(ws.LocalDir, n), "NEW " + n);
            File.WriteAllText(Path.Combine(ws.RemoteDir, n), "OLD " + n);
            File.SetLastWriteTimeUtc(Path.Combine(ws.RemoteDir, n), DateTime.UtcNow.AddDays(-3));
        }

        Workspace.Sh($"chown -R {Env.User} '{ws.RemoteDir}'");
        await ws.ConnectAsync();
        await using var q = ws.NewQueue();
        q.ConflictRaised += r => q.ResolveConflict(r, r.RelativePath switch { "r.txt" => ConflictChoice.Replace, "k.txt" => ConflictChoice.KeepBoth, _ => ConflictChoice.Skip }, false);
        var entries = new List<FileEntry>();
        foreach (var n in new[] { "r.txt", "k.txt", "s.txt" })
        {
            entries.Add((await ws.Local.GetEntryAsync(Path.Combine(ws.LocalDir, n), default))!);
        }

        var job = q.EnqueueItems(ws.Key, TransferDirection.Upload, entries, ws.LocalDir, ws.RemoteDir);
        Assert.Equal(TransferState.CompletedWithWarning, await Workspace.WaitFinalAsync(job));
        Assert.Equal("NEW r.txt", File.ReadAllText(Path.Combine(ws.RemoteDir, "r.txt")));
        Assert.Equal("OLD k.txt", File.ReadAllText(Path.Combine(ws.RemoteDir, "k.txt")));
        Assert.Equal("NEW k.txt", File.ReadAllText(Path.Combine(ws.RemoteDir, "k (2).txt")));
        Assert.Equal("OLD s.txt", File.ReadAllText(Path.Combine(ws.RemoteDir, "s.txt")));
    }

    [FixtureFact]
    public async Task Server_without_safe_replace_keeps_original()
    {
        await using var ws = new Workspace(port: Env.PortNoReplace);
        File.WriteAllText(Path.Combine(ws.LocalDir, "a.txt"), "replacement");
        File.WriteAllText(Path.Combine(ws.RemoteDir, "a.txt"), "original");
        Workspace.Sh($"chown -R {Env.User} '{ws.RemoteDir}'");
        await ws.ConnectAsync();
        await using var q = ws.NewQueue();
        q.ConflictRaised += r => q.ResolveConflict(r, ConflictChoice.Replace, false);
        var job = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(Path.Combine(ws.LocalDir, "a.txt"), default))! }, ws.LocalDir, ws.RemoteDir);
        Assert.Equal(TransferState.Failed, await Workspace.WaitFinalAsync(job));
        Assert.Equal("original", File.ReadAllText(Path.Combine(ws.RemoteDir, "a.txt")));
        Assert.Contains("kept unchanged", job.Files[0].Message);
        Assert.False(ws.RemoteHasStagingFiles());

        // New files still work on this server (plain rename).
        File.WriteAllText(Path.Combine(ws.LocalDir, "new.txt"), "fresh");
        var job2 = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(Path.Combine(ws.LocalDir, "new.txt"), default))! }, ws.LocalDir, ws.RemoteDir);
        Assert.Equal(TransferState.Completed, await Workspace.WaitFinalAsync(job2));
    }

    [FixtureFact]
    public async Task Permission_denied_destination_and_unreadable_source_folder()
    {
        await using var ws = new Workspace();
        var locked = Path.Combine(ws.RemoteDir, "readonly");
        Directory.CreateDirectory(locked);
        Workspace.Sh($"chown root:root '{locked}' && chmod 555 '{locked}'");
        File.WriteAllText(Path.Combine(ws.LocalDir, "a.txt"), "x");
        var browse = await ws.ConnectAsync();
        await using var q = ws.NewQueue();
        var job = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(Path.Combine(ws.LocalDir, "a.txt"), default))! }, ws.LocalDir, locked);
        Assert.Equal(TransferState.Failed, await Workspace.WaitFinalAsync(job));
        Assert.Equal(FailureKind.PermissionDenied, job.Files[0].Failure);

        var tree = Path.Combine(ws.RemoteDir, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "secret"));
        File.WriteAllText(Path.Combine(tree, "ok.txt"), "ok");
        File.WriteAllText(Path.Combine(tree, "secret", "s.txt"), "s");
        Workspace.Sh($"chown -R {Env.User} '{tree}' && chown root:root '{tree}/secret' && chmod 700 '{tree}/secret'");
        var listEx = await Assert.ThrowsAsync<StoreException>(() => browse.ListAsync(tree + "/secret", default));
        Assert.Equal(FailureKind.PermissionDenied, listEx.Kind);

        var dl = Path.Combine(ws.LocalDir, "dl");
        Directory.CreateDirectory(dl);
        var down = q.EnqueueItems(ws.Key, TransferDirection.Download, new[] { (await browse.GetEntryAsync(tree, default))! }, ws.RemoteDir, dl);
        Assert.Equal(TransferState.CompletedWithWarning, await Workspace.WaitFinalAsync(down));
        Assert.Contains(down.ScanSkipped, s => s.Reason == SkipReason.Unreadable && s.RelativePath == "tree/secret");
        Assert.True(File.Exists(Path.Combine(dl, "tree", "ok.txt")));
    }

    [FixtureFact]
    public async Task Source_changing_during_upload_is_flagged_and_destination_untouched()
    {
        await using var ws = new Workspace();
        var file = Path.Combine(ws.LocalDir, "growing.log");
        Workspace.WriteRandom(file, 200_000_000, 3);
        await ws.ConnectAsync();
        await using var q = ws.NewQueue();
        var job = q.EnqueueItems(ws.Key, TransferDirection.Upload, new[] { (await ws.Local.GetEntryAsync(file, default))! }, ws.LocalDir, ws.RemoteDir);
        await Workspace.WaitUntilAsync(() => job.Snapshot().TransferredBytes > 10_000_000);
        await File.AppendAllTextAsync(file, "appended while uploading\n");
        Assert.Equal(TransferState.Failed, await Workspace.WaitFinalAsync(job));
        Assert.Equal(FailureKind.SourceChanged, job.Files[0].Failure);
        Assert.False(File.Exists(Path.Combine(ws.RemoteDir, "growing.log")));
        Assert.False(ws.RemoteHasStagingFiles());
    }

    [FixtureFact]
    public async Task Ten_thousand_entries_list_quickly()
    {
        await using var ws = new Workspace();
        var dir = Path.Combine(ws.RemoteDir, "many");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 10_000; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"entry_{i:00000}.dat"), Array.Empty<byte>());
        }

        Workspace.Sh($"chown -R {Env.User} '{dir}'");
        var browse = await ws.ConnectAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = await browse.ListAsync(dir, default);
        sw.Stop();
        Assert.Equal(10_000, list.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Listing took {sw.Elapsed}");
        Console.WriteLine($"Listed 10,000 remote entries in {sw.ElapsedMilliseconds} ms");

        var localMany = Path.Combine(ws.LocalDir, "many");
        Directory.CreateDirectory(localMany);
        for (var i = 0; i < 10_000; i++)
        {
            File.WriteAllBytes(Path.Combine(localMany, $"entry_{i:00000}.dat"), Array.Empty<byte>());
        }

        sw.Restart();
        var localList = await ws.Local.ListAsync(localMany, default);
        Assert.Equal(10_000, localList.Count);
        Console.WriteLine($"Listed 10,000 local entries in {sw.ElapsedMilliseconds} ms");
    }
}

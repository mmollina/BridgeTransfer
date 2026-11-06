using BridgeTransfer.Core.Comparison;
using BridgeTransfer.Core.Models;
using BridgeTransfer.UnitTests.Fakes;

namespace BridgeTransfer.UnitTests;

public class ComparisonTests
{
    private static readonly DateTime T0 = new(2025, 5, 1, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<ComparisonResult> CompareAsync(InMemoryFileStore src, string srcRoot, InMemoryFileStore dst, string dstRoot, TransferDirection dir)
    {
        var a = await TreeScanner.ScanAsync(src, srcRoot, null, default);
        var b = await TreeScanner.ScanAsync(dst, dstRoot, null, default);
        return FolderComparer.Compare(a, b, dir);
    }

    private static ComparisonRow Row(ComparisonResult r, string rel) => r.Rows.Single(x => x.RelativePath == rel);

    [Fact]
    public async Task Categories_and_default_selection()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true);
        var remote = new InMemoryFileStore(EndpointSide.Remote);
        local.File("/s/new.txt", "n", T0)
             .File("/s/newer.txt", "aa", T0.AddMinutes(5))
             .File("/s/older.txt", "aa", T0)
             .File("/s/same.txt", "same", T0)
             .File("/s/within_tol.txt", "same", T0.AddSeconds(1.5))
             .File("/s/ambig.txt", "short", T0)
             .Dir("/s/newdir").File("/s/newdir/f.txt", "f", T0)
             .File("/s/clash", "file", T0);
        remote.File("/d/newer.txt", "bb", T0)
              .File("/d/older.txt", "bb", T0.AddMinutes(5))
              .File("/d/same.txt", "same", T0)
              .File("/d/within_tol.txt", "same", T0)
              .File("/d/ambig.txt", "longer", T0.AddSeconds(1))
              .File("/d/extra.txt", "x", T0)
              .Dir("/d/clash");

        var r = await CompareAsync(local, "/s", remote, "/d", TransferDirection.Upload);

        Assert.Equal(ComparisonCategory.OnlyOnSource, Row(r, "new.txt").Category);
        Assert.True(Row(r, "new.txt").IsSelected);
        Assert.Equal(ComparisonCategory.SourceNewer, Row(r, "newer.txt").Category);
        Assert.True(Row(r, "newer.txt").IsSelected);
        Assert.Equal(SyncAction.Replace, Row(r, "newer.txt").Action);
        Assert.Equal(ComparisonCategory.DestinationNewer, Row(r, "older.txt").Category);
        Assert.False(Row(r, "older.txt").IsSelected);
        Assert.True(Row(r, "older.txt").ReplacesNewer);
        Assert.Equal(ComparisonCategory.SameMetadata, Row(r, "same.txt").Category);
        Assert.False(Row(r, "same.txt").IsSelected);
        Assert.Equal(ComparisonCategory.SameMetadata, Row(r, "within_tol.txt").Category);
        Assert.Equal(ComparisonCategory.AmbiguousTime, Row(r, "ambig.txt").Category);
        Assert.False(Row(r, "ambig.txt").IsSelected);
        Assert.Equal(ComparisonCategory.OnlyOnSource, Row(r, "newdir").Category);
        Assert.Equal(SyncAction.CreateFolder, Row(r, "newdir").Action);
        Assert.True(Row(r, "newdir/f.txt").IsSelected);
        Assert.Equal(ComparisonCategory.OnlyOnDestination, Row(r, "extra.txt").Category);
        Assert.False(Row(r, "extra.txt").IsSelectable);
        Assert.Equal(ComparisonCategory.TypeConflict, Row(r, "clash").Category);
        Assert.False(Row(r, "clash").IsSelectable);
    }

    [Fact]
    public async Task Same_metadata_is_never_called_identical()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true).File("/s/a", "AAAA", T0);
        var remote = new InMemoryFileStore(EndpointSide.Remote).File("/d/a", "BBBB", T0);
        var r = await CompareAsync(local, "/s", remote, "/d", TransferDirection.Upload);
        Assert.Equal(ComparisonCategory.SameMetadata, Row(r, "a").Category);
        Assert.Contains("not compared", Row(r, "a").Reason);
    }

    [Fact]
    public async Task Download_flags_case_collisions_and_windows_invalid_names()
    {
        var remote = new InMemoryFileStore(EndpointSide.Remote)
            .File("/d/Data.csv", "1", T0).File("/d/data.csv", "2", T0)
            .File("/d/aux.txt", "x", T0)
            .File("/d/fine.txt", "x", T0)
            .Link("/d/lnk");
        var local = new InMemoryFileStore(EndpointSide.Local, true).Dir("/l");
        var r = await CompareAsync(remote, "/d", local, "/l", TransferDirection.Download);
        Assert.Equal(ComparisonCategory.UnsupportedName, Row(r, "Data.csv").Category);
        Assert.Equal(ComparisonCategory.UnsupportedName, Row(r, "data.csv").Category);
        Assert.Equal(ComparisonCategory.UnsupportedName, Row(r, "aux.txt").Category);
        Assert.Equal(ComparisonCategory.SkippedLink, Row(r, "lnk").Category);
        Assert.True(Row(r, "fine.txt").IsSelected);
        Assert.Empty(FolderComparer.ValidateSelection(r));
    }

    [Fact]
    public async Task Unreadable_destination_subtree_blocks_actions_instead_of_meaning_absent()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true).File("/s/sub/a.txt", "a", T0).File("/s/top.txt", "t", T0);
        var remote = new InMemoryFileStore(EndpointSide.Remote).File("/d/sub/a.txt", "a", T0);
        remote.MarkUnreadable("/d/sub");
        var r = await CompareAsync(local, "/s", remote, "/d", TransferDirection.Upload);
        Assert.False(r.DestinationComplete);
        Assert.Equal(ComparisonCategory.Incomplete, Row(r, "sub").Category);
        Assert.DoesNotContain(r.Rows, x => x.RelativePath == "sub/a.txt" && x.IsSelectable);
        Assert.True(Row(r, "top.txt").IsSelected);
    }

    [Fact]
    public async Task Unreadable_source_subtree_does_not_report_destination_items_as_only_on_destination()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true).File("/s/sub/a.txt", "a", T0);
        local.MarkUnreadable("/s/sub");
        var remote = new InMemoryFileStore(EndpointSide.Remote).File("/d/sub/b.txt", "b", T0);
        var r = await CompareAsync(local, "/s", remote, "/d", TransferDirection.Upload);
        Assert.Equal(ComparisonCategory.Incomplete, Row(r, "sub").Category);
        Assert.DoesNotContain(r.Rows, x => x.Category == ComparisonCategory.OnlyOnDestination);
    }

    [Fact]
    public async Task Missing_time_metadata_is_ambiguous_and_unselected()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true).File("/s/a", "x", T0);
        var remote = new InMemoryFileStore(EndpointSide.Remote).File("/d/a", "x", T0);
        var a = await TreeScanner.ScanAsync(local, "/s", null, default);
        var b = await TreeScanner.ScanAsync(remote, "/d", null, default);
        b.Items["a"] = b.Items["a"] with { ModifiedUtc = null };
        var r = FolderComparer.Compare(a, b, TransferDirection.Upload);
        Assert.Equal(ComparisonCategory.AmbiguousTime, Row(r, "a").Category);
        Assert.False(Row(r, "a").IsSelected);
    }

    [Fact]
    public async Task Destination_case_variant_on_windows_is_not_silently_matched()
    {
        var remote = new InMemoryFileStore(EndpointSide.Remote).File("/d/Report.txt", "new", T0.AddHours(1));
        var local = new InMemoryFileStore(EndpointSide.Local, true).File("/l/report.txt", "old", T0);
        var r = await CompareAsync(remote, "/d", local, "/l", TransferDirection.Download);
        Assert.Equal(ComparisonCategory.UnsupportedName, Row(r, "Report.txt").Category);
    }

    [Fact]
    public async Task Stale_preview_is_put_back_into_review()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true).File("/s/a.txt", "a", T0).File("/s/b.txt", "b", T0);
        var remote = new InMemoryFileStore(EndpointSide.Remote).Dir("/d");
        var r = await CompareAsync(local, "/s", remote, "/d", TransferDirection.Upload);
        Assert.Equal(2, r.Selected.Count());

        local.Touch("/s/a.txt", "changed"u8.ToArray(), T0.AddMinutes(1));
        remote.File("/d/b.txt", "appeared", T0);
        var changed = await SyncPlanBuilder.RevalidateAsync(r, local, remote, default);
        Assert.Equal(2, changed);
        Assert.Empty(r.Selected);
        Assert.True(Row(r, "a.txt").NeedsReview);
        Assert.True(Row(r, "b.txt").NeedsReview);
    }

    [Fact]
    public async Task Plan_items_carry_expectations()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true).File("/s/a.txt", "abc", T0.AddHours(1));
        var remote = new InMemoryFileStore(EndpointSide.Remote).File("/d/a.txt", "x", T0);
        var r = await CompareAsync(local, "/s", remote, "/d", TransferDirection.Upload);
        var items = SyncPlanBuilder.Build(r, local, remote);
        var item = Assert.Single(items);
        Assert.Equal("/d/a.txt", item.DestinationPath);
        Assert.True(item.ExpectedDestination.Exists);
        Assert.Equal(1, item.ExpectedDestination.Snapshot!.Length);
        Assert.Equal(3, item.ExpectedSource!.Length);
        Assert.False(item.NewerReplaceApproved);
    }

    [Fact]
    public async Task Scanning_large_flat_folder_is_fast_enough()
    {
        var remote = new InMemoryFileStore(EndpointSide.Remote);
        for (var i = 0; i < 10_000; i++)
        {
            remote.File($"/d/f{i:00000}.dat", "x", T0);
        }

        var local = new InMemoryFileStore(EndpointSide.Local, true).Dir("/l");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await CompareAsync(remote, "/d", local, "/l", TransferDirection.Download);
        Assert.Equal(10_000, r.Rows.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"Took {sw.Elapsed}");
    }
}

using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Transfers;
using BridgeTransfer.UnitTests.Fakes;
using static BridgeTransfer.UnitTests.Fakes.TestUtil;

namespace BridgeTransfer.UnitTests;

public class ConflictRuleTests
{
    private static readonly DateTime Old = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Source files vs existing destination files:
    ///  newer_same   : source newer, same size
    ///  same_diff    : same time, different size
    ///  same_same    : same time, same size
    ///  older_diff   : destination newer, different size
    ///  older_same   : destination newer, same size
    /// </summary>
    private static (InMemoryFileStore Local, InMemoryFileStore Remote, FakeSessionFactory Factory) Setup()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true);
        var remote = new InMemoryFileStore(EndpointSide.Remote);
        local.File("/c/d/newer_same", "AAAA", New).File("/c/d/same_diff", "AAAAAA", Old).File("/c/d/same_same", "AAAA", Old)
             .File("/c/d/older_diff", "AAAAAA", Old).File("/c/d/older_same", "AAAA", Old);
        remote.File("/r/d/newer_same", "BBBB", Old).File("/r/d/same_diff", "BB", Old).File("/r/d/same_same", "BBBB", Old)
              .File("/r/d/older_diff", "BB", New).File("/r/d/older_same", "BBBB", New);
        return (local, remote, new FakeSessionFactory(local, remote));
    }

    private static async Task<(TransferJob Job, List<ConflictRequest> Asked, InMemoryFileStore Remote)> RunAsync(ConflictChoice rule)
    {
        var (local, remote, factory) = Setup();
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var asked = new List<ConflictRequest>();
        q.ConflictRaised += r =>
        {
            lock (asked)
            {
                asked.Add(r);
            }

            q.ResolveConflict(r, ConflictChoice.Skip, false);
        };
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { (await local.GetEntryAsync("/c/d", default))! }, "/c", "/r", rule);
        await WaitFinalAsync(job);
        return (job, asked, remote);
    }

    private static string Text(InMemoryFileStore s, string name) => s.ReadText("/r/d/" + name);

    [Fact]
    public async Task Replace_if_newer()
    {
        var (_, asked, remote) = await RunAsync(ConflictChoice.ReplaceIfNewer);
        Assert.Equal("AAAA", Text(remote, "newer_same"));
        Assert.Equal("BB", Text(remote, "same_diff"));
        Assert.Equal("BBBB", Text(remote, "same_same"));
        Assert.Equal("BB", Text(remote, "older_diff"));
        Assert.Equal("BBBB", Text(remote, "older_same"));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Replace_if_size_differs_never_replaces_a_newer_destination_without_asking()
    {
        var (_, asked, remote) = await RunAsync(ConflictChoice.ReplaceIfSizeDiffers);
        Assert.Equal("BBBB", Text(remote, "newer_same"));
        Assert.Equal("AAAAAA", Text(remote, "same_diff"));
        Assert.Equal("BBBB", Text(remote, "same_same"));
        Assert.Equal("BB", Text(remote, "older_diff")); // asked, answered Skip
        Assert.Equal("BBBB", Text(remote, "older_same"));
        Assert.Equal(new[] { "d/older_diff" }, asked.Select(a => a.RelativePath));
    }

    [Fact]
    public async Task Replace_if_newer_or_size_differs()
    {
        var (job, asked, remote) = await RunAsync(ConflictChoice.ReplaceIfNewerOrSizeDiffers);
        Assert.Equal("AAAA", Text(remote, "newer_same"));
        Assert.Equal("AAAAAA", Text(remote, "same_diff"));
        Assert.Equal("BBBB", Text(remote, "same_same"));
        Assert.Equal("BBBB", Text(remote, "older_same"));
        Assert.Equal(new[] { "d/older_diff" }, asked.Select(a => a.RelativePath));
        Assert.Contains("same size", job.Files.Single(f => f.RelativePath == "d/same_same").Message);
    }

    [Fact]
    public async Task Preset_skip_never_asks_and_needs_no_per_file_requests()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true);
        var remote = new InMemoryFileStore(EndpointSide.Remote);
        for (var i = 0; i < 60; i++)
        {
            local.File($"/c/d/f{i}", "x", New);
            remote.File($"/r/d/f{i}", "y", Old);
        }

        await using var q = new TransferQueue(new FakeSessionFactory(local, remote), new NullJournal(), FastOptions());
        var asked = 0;
        q.ConflictRaised += _ => asked++;
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { (await local.GetEntryAsync("/c/d", default))! }, "/c", "/r", ConflictChoice.Skip);
        await WaitFinalAsync(job);
        Assert.All(job.Files, f => Assert.Equal(TransferState.Skipped, f.State));
        Assert.Equal(0, asked);
        Assert.True(remote.Operations.Count(o => o.StartsWith("stat /r/d/f")) == 0, "Existing files are found from the folder listing, not one request per file.");
        Assert.True(remote.Operations.Count(o => o.StartsWith("list ")) <= 2);
    }

    [Fact]
    public async Task New_files_still_cannot_overwrite_something_that_appears_after_the_listing()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true).File("/c/a.txt", "new");
        var remote = new InMemoryFileStore(EndpointSide.Remote).Dir("/r");
        var appeared = false;
        remote.BeforeOperation = (op, path) =>
        {
            if (op == "rename" && !appeared)
            {
                appeared = true;
                remote.File("/r/a.txt", "arrived meanwhile");
            }
        };
        await using var q = new TransferQueue(new FakeSessionFactory(local, remote), new NullJournal(), FastOptions());
        ConflictRequest? asked = null;
        q.ConflictRaised += r => asked = r;
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { (await local.GetEntryAsync("/c/a.txt", default))! }, "/c", "/r");
        await WaitUntilAsync(() => asked is not null);
        Assert.Equal("arrived meanwhile", remote.ReadText("/r/a.txt"));
        q.ResolveConflict(asked!, ConflictChoice.Skip, false);
        await WaitFinalAsync(job);
        Assert.Equal("arrived meanwhile", remote.ReadText("/r/a.txt"));
    }

    [Fact]
    public async Task A_choice_for_the_whole_queue_resolves_other_transfers()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, true).File("/c/a.txt", "1", New).File("/c/b.txt", "2", New);
        var remote = new InMemoryFileStore(EndpointSide.Remote).File("/r/a.txt", "old", Old).File("/r/b.txt", "old", Old);
        await using var q = new TransferQueue(new FakeSessionFactory(local, remote), new NullJournal(), FastOptions());
        var asked = new System.Collections.Concurrent.ConcurrentQueue<ConflictRequest>();
        q.ConflictRaised += asked.Enqueue;
        var j1 = q.EnqueueItems(Key, TransferDirection.Upload, new[] { (await local.GetEntryAsync("/c/a.txt", default))! }, "/c", "/r");
        var j2 = q.EnqueueItems(Key, TransferDirection.Upload, new[] { (await local.GetEntryAsync("/c/b.txt", default))! }, "/c", "/r");
        await WaitUntilAsync(() => asked.Count == 2);
        q.ResolveConflict(asked.First(), ConflictChoice.ReplaceIfNewer, ConflictScope.WholeQueue);
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(j1));
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(j2));
        Assert.Equal("1", remote.ReadText("/r/a.txt"));
        Assert.Equal("2", remote.ReadText("/r/b.txt"));
    }
}

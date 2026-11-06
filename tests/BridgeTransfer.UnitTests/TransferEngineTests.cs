using BridgeTransfer.Core;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Transfers;
using BridgeTransfer.UnitTests.Fakes;
using static BridgeTransfer.UnitTests.Fakes.TestUtil;

namespace BridgeTransfer.UnitTests;

public class TransferEngineTests
{
    private static (InMemoryFileStore Local, InMemoryFileStore Remote, FakeSessionFactory Factory) Setup()
    {
        var local = new InMemoryFileStore(EndpointSide.Local, caseInsensitive: true);
        var remote = new InMemoryFileStore(EndpointSide.Remote);
        return (local, remote, new FakeSessionFactory(local, remote));
    }

    private static FileEntry Entry(IFileStore store, string path) => store.GetEntryAsync(path, default).Result!;

    private static bool NoStagingLeft(InMemoryFileStore s) => !s.AllPaths().Any(p => p.Contains(AppInfo.StagingPrefix));

    [Fact]
    public async Task Upload_folder_creates_folder_inside_destination_with_empty_dirs_and_exact_bytes()
    {
        var (local, remote, factory) = Setup();
        var big = Bytes(300_000, 7);
        local.File("/c/project/a.bin", big).File("/c/project/sub/b.txt", "line1\r\nline2\n").Dir("/c/project/empty");
        remote.Dir("/data");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());

        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/project") }, "/c", "/data");
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));

        Assert.Equal(big, remote.Read("/data/project/a.bin"));
        Assert.Equal("line1\r\nline2\n", remote.ReadText("/data/project/sub/b.txt"));
        Assert.True(remote.Get("/data/project/empty")!.Kind == EntryKind.Directory);
        Assert.True(NoStagingLeft(remote));
        Assert.Equal(local.Get("/c/project/a.bin")!.ModifiedUtc, remote.Get("/data/project/a.bin")!.ModifiedUtc);
    }

    [Fact]
    public async Task Partial_file_is_never_visible_under_final_name()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/big.bin", Bytes(1_000_000));
        remote.Dir("/data");
        var sawFinalEarly = false;
        remote.OnWriteProgress = (path, _) =>
        {
            if (remote.Exists("/data/big.bin"))
            {
                sawFinalEarly = true;
            }

            Assert.StartsWith(AppInfo.StagingPrefix, System.IO.Path.GetFileName(path));
        };
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/big.bin") }, "/c", "/data");
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));
        Assert.False(sawFinalEarly);
    }

    [Fact]
    public async Task Existing_destination_asks_and_skip_keeps_original()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.txt", "new", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        remote.File("/data/a.txt", "old", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        ConflictRequest? asked = null;
        q.ConflictRaised += r => asked = r;

        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.txt") }, "/c", "/data");
        await WaitUntilAsync(() => asked is not null);
        Assert.Equal(ConflictKind.FileExists, asked!.Kind);

        q.ResolveConflict(asked, ConflictChoice.Skip, false);
        Assert.Equal(TransferState.Skipped, await WaitFinalAsync(job));
        Assert.Equal("old", remote.ReadText("/data/a.txt"));
        Assert.True(NoStagingLeft(remote));
    }

    [Fact]
    public async Task Replace_after_confirmation_replaces_and_keep_both_creates_new_name()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.txt", "new").File("/c/b.dat", "newb");
        remote.File("/data/a.txt", "old").File("/data/b.dat", "oldb");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var requests = new System.Collections.Concurrent.ConcurrentQueue<ConflictRequest>();
        q.ConflictRaised += requests.Enqueue;

        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.txt"), Entry(local, "/c/b.dat") }, "/c", "/data");
        await WaitUntilAsync(() => requests.Count == 2);
        foreach (var r in requests)
        {
            q.ResolveConflict(r, r.RelativePath == "a.txt" ? ConflictChoice.Replace : ConflictChoice.KeepBoth, false);
        }

        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));
        Assert.Equal("new", remote.ReadText("/data/a.txt"));
        Assert.Equal("oldb", remote.ReadText("/data/b.dat"));
        Assert.Equal("newb", remote.ReadText("/data/b (2).dat"));
    }

    [Fact]
    public async Task Apply_replace_to_batch_never_replaces_newer_destination_automatically()
    {
        var (local, remote, factory) = Setup();
        var old = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        local.File("/c/x1.txt", "s1", newer).File("/c/x2.txt", "s2", newer).File("/c/n.txt", "sn", old);
        remote.File("/data/x1.txt", "d1", old).File("/data/x2.txt", "d2", old).File("/data/n.txt", "dn", newer);
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        q.MaxConcurrency = 1;
        var requests = new System.Collections.Concurrent.ConcurrentQueue<ConflictRequest>();
        q.ConflictRaised += requests.Enqueue;

        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/x1.txt"), Entry(local, "/c/x2.txt"), Entry(local, "/c/n.txt") }, "/c", "/data");
        await WaitUntilAsync(() => requests.Any(r => r.Kind == ConflictKind.FileExists));
        q.ResolveConflict(requests.First(r => r.Kind == ConflictKind.FileExists), ConflictChoice.Replace, applyToRemaining: true);

        await WaitUntilAsync(() => requests.Any(r => r.Kind == ConflictKind.DestinationNewer) && job.State == TransferState.WaitingForDecision);
        Assert.Equal("s1", remote.ReadText("/data/x1.txt"));
        Assert.Equal("s2", remote.ReadText("/data/x2.txt"));
        Assert.Equal("dn", remote.ReadText("/data/n.txt"));

        var newerReq = requests.First(r => r.Kind == ConflictKind.DestinationNewer);
        q.ResolveConflict(newerReq, ConflictChoice.Replace, applyToRemaining: false);
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));
        Assert.Equal("sn", remote.ReadText("/data/n.txt"));
    }

    [Fact]
    public async Task Folder_and_file_type_conflict_cannot_be_replaced()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/thing", "file");
        remote.Dir("/data/thing");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        ConflictRequest? asked = null;
        q.ConflictRaised += r => asked = r;
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/thing") }, "/c", "/data");
        await WaitUntilAsync(() => asked is not null);
        Assert.Equal(ConflictKind.TypeConflict, asked!.Kind);
        Assert.False(asked.ReplaceAllowed);
        Assert.Throws<InvalidOperationException>(() => q.ResolveConflict(asked, ConflictChoice.Replace, false));
        q.ResolveConflict(asked, ConflictChoice.Skip, false);
        Assert.Equal(TransferState.Skipped, await WaitFinalAsync(job));
        Assert.Equal(EntryKind.Directory, remote.Get("/data/thing")!.Kind);
    }

    [Fact]
    public async Task Unsupported_replace_keeps_original_and_removes_owned_staging()
    {
        var (local, remote, factory) = Setup();
        remote.SupportsReplace = false;
        local.File("/c/a.txt", "new");
        remote.File("/data/a.txt", "original");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        q.ConflictRaised += r => q.ResolveConflict(r, ConflictChoice.Replace, false);
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.txt") }, "/c", "/data");
        Assert.Equal(TransferState.Failed, await WaitFinalAsync(job));
        Assert.Equal("original", remote.ReadText("/data/a.txt"));
        Assert.True(NoStagingLeft(remote));
        Assert.Equal(FailureKind.ReplaceNotSupported, job.Files[0].Failure);
    }

    [Fact]
    public async Task Transient_failure_mid_write_is_retried_and_completes_with_exact_bytes()
    {
        var (local, remote, factory) = Setup();
        var data = Bytes(500_000, 3);
        local.File("/c/a.bin", data);
        remote.Dir("/data");
        var failures = 0;
        remote.OnWriteProgress = (path, total) =>
        {
            if (total > 200_000 && Interlocked.Increment(ref failures) == 1)
            {
                throw new StoreException(FailureKind.Transient, "Connection reset");
            }
        };
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.bin") }, "/c", "/data");
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));
        Assert.Equal(data, remote.Read("/data/a.bin"));
        Assert.True(NoStagingLeft(remote));
        Assert.Equal(1, job.Files[0].Attempts);
        Assert.True(factory.Opened >= 2, "A fresh session must be opened after a transient failure.");
    }

    [Fact]
    public async Task Persistent_transient_failure_gives_up_after_bounded_attempts()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.bin", Bytes(200_000));
        remote.Dir("/data");
        var writes = 0;
        remote.BeforeOperation = (op, _) =>
        {
            if (op == "create")
            {
                Interlocked.Increment(ref writes);
                throw new StoreException(FailureKind.Transient, "timeout");
            }
        };
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.bin") }, "/c", "/data");
        Assert.Equal(TransferState.Failed, await WaitFinalAsync(job));
        Assert.Equal(3, writes);
        Assert.False(remote.Exists("/data/a.bin"));
    }

    [Fact]
    public async Task Authentication_failure_is_not_retried()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.bin", Bytes(100));
        remote.Dir("/data");
        var opens = 0;
        factory.OnOpen = _ =>
        {
            Interlocked.Increment(ref opens);
            return opens == 1 ? null : new StoreException(FailureKind.Authentication, "denied");
        };
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        q.MaxConcurrency = 1;
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.bin") }, "/c", "/data");

        // First open succeeds for the scan; the session is reused, so make it fail by forcing a transient error first.
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));

        var local2 = local.File("/c/b.bin", Bytes(100));
        remote.BeforeOperation = (op, _) =>
        {
            if (op == "create")
            {
                throw new StoreException(FailureKind.Transient, "reset");
            }
        };
        var job2 = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/b.bin") }, "/c", "/data");
        Assert.Equal(TransferState.Failed, await WaitFinalAsync(job2));
        Assert.Equal(FailureKind.Authentication, job2.Files[0].Failure);
        Assert.True(opens <= 3, $"Authentication must not be retried repeatedly (opens={opens}).");
    }

    [Fact]
    public async Task Cancelling_mid_transfer_leaves_existing_destination_untouched()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.bin", Bytes(2_000_000));
        remote.File("/data/a.bin", "original");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        q.ConflictRaised += r => q.ResolveConflict(r, ConflictChoice.Replace, false);
        TransferJob? job = null;
        var gate = new ManualResetEventSlim();
        remote.OnWriteProgress = (_, total) =>
        {
            if (total > 500_000 && job is not null && !job.CancelRequested)
            {
                q.Cancel(job);
            }
        };
        job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.bin") }, "/c", "/data");
        Assert.Equal(TransferState.Cancelled, await WaitFinalAsync(job));
        Assert.Equal("original", remote.ReadText("/data/a.bin"));
        Assert.True(NoStagingLeft(remote));
    }

    [Fact]
    public async Task Source_changing_during_transfer_fails_without_touching_destination()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/log.txt", Bytes(400_000));
        remote.Dir("/data");
        var changed = false;
        local.OnReadProgress = (_, total) =>
        {
            if (total > 100_000 && !changed)
            {
                changed = true;
                local.Touch("/c/log.txt", Bytes(400_100), DateTime.UtcNow);
            }
        };
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/log.txt") }, "/c", "/data");
        Assert.Equal(TransferState.Failed, await WaitFinalAsync(job));
        Assert.Equal(FailureKind.SourceChanged, job.Files[0].Failure);
        Assert.False(remote.Exists("/data/log.txt"));
        Assert.True(NoStagingLeft(remote));
    }

    [Fact]
    public async Task Overlapping_jobs_never_write_same_destination_at_once()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.bin", Bytes(1_000_000));
        remote.Dir("/data");
        remote.ChunkSize = 16 * 1024;
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        q.MaxConcurrency = 4;
        var requests = new System.Collections.Concurrent.ConcurrentQueue<ConflictRequest>();
        q.ConflictRaised += requests.Enqueue;
        var job1 = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.bin") }, "/c", "/data");
        var job2 = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.bin") }, "/c", "/data");
        await WaitUntilAsync(() =>
            (job1.State == TransferState.Completed && job2.State == TransferState.WaitingForDecision) ||
            (job2.State == TransferState.Completed && job1.State == TransferState.WaitingForDecision));
        await WaitUntilAsync(() => requests.Count == 1);
        Assert.Equal(1, remote.MaxConcurrentWritersObserved);
        Assert.Single(requests);
    }

    [Fact]
    public async Task Unknown_staging_like_files_are_never_deleted()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.txt", "x");
        remote.File("/data/.bt-staging-deadbeef-cafebabe-123456.part", "not ours").File("/data/report.part", "user file");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.txt") }, "/c", "/data");
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));
        Assert.True(remote.Exists("/data/.bt-staging-deadbeef-cafebabe-123456.part"));
        Assert.True(remote.Exists("/data/report.part"));
    }

    [Fact]
    public async Task Uncertain_rename_is_reconciled_without_copying_again()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.bin", Bytes(300_000));
        remote.Dir("/data");
        var thrown = 0;
        remote.AfterRename = (from, to) =>
        {
            if (to == "/data/a.bin" && Interlocked.Increment(ref thrown) == 1)
            {
                throw new StoreException(FailureKind.Transient, "connection lost after rename");
            }
        };
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.bin") }, "/c", "/data");
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));
        Assert.Equal(local.Read("/c/a.bin"), remote.Read("/data/a.bin"));
        Assert.Equal(1, remote.Operations.Count(o => o.StartsWith("create ")));
        Assert.Contains("Confirmed", job.Files[0].Message);
    }

    [Fact]
    public async Task Download_skips_windows_invalid_names_and_case_collisions_and_links()
    {
        var (local, remote, factory) = Setup();
        local.Dir("/c/dl");
        remote.File("/data/proj/ok.txt", "ok")
              .File("/data/proj/bad:name.txt", "x")
              .File("/data/proj/CON.txt", "x")
              .File("/data/proj/Readme", "1").File("/data/proj/README", "2")
              .Link("/data/proj/link");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Download, new[] { Entry(remote, "/data/proj") }, "/data", "/c/dl");
        Assert.Equal(TransferState.CompletedWithWarning, await WaitFinalAsync(job));
        Assert.Equal("ok", local.ReadText("/c/dl/proj/ok.txt"));
        Assert.Equal(5, job.ScanSkipped.Count);
        Assert.Contains(job.ScanSkipped, s => s.Reason == SkipReason.CaseCollision && s.RelativePath == "proj/README");
        Assert.Contains(job.ScanSkipped, s => s.Reason == SkipReason.SymbolicLink);
        Assert.Contains(job.ScanSkipped, s => s.Reason == SkipReason.UnsupportedName && s.RelativePath == "proj/CON.txt");
        Assert.False(local.Exists("/c/dl/proj/readme"));
    }

    [Fact]
    public async Task Unreadable_subfolder_is_reported_not_treated_as_empty()
    {
        var (local, remote, factory) = Setup();
        local.Dir("/c/dl");
        remote.File("/data/p/a.txt", "a").File("/data/p/secret/s.txt", "s");
        remote.MarkUnreadable("/data/p/secret");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Download, new[] { Entry(remote, "/data/p") }, "/data", "/c/dl");
        Assert.Equal(TransferState.CompletedWithWarning, await WaitFinalAsync(job));
        Assert.Contains(job.ScanSkipped, s => s.Reason == SkipReason.Unreadable && s.RelativePath == "p/secret");
    }

    [Fact]
    public async Task Destination_folder_that_is_a_link_is_refused()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/p/a.txt", "a");
        remote.Dir("/data").Link("/data/p");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/p") }, "/c", "/data");
        Assert.Equal(TransferState.Failed, await WaitFinalAsync(job));
        Assert.Equal(FailureKind.LinkRedirect, job.Directories[0].Failure);
        Assert.Equal(TransferState.Skipped, job.Files[0].State);
    }

    [Fact]
    public async Task Local_disk_full_is_detected_before_writing()
    {
        var (local, remote, factory) = Setup();
        local.Dir("/c/dl");
        local.AvailableSpace = 1000;
        remote.File("/data/big.bin", Bytes(50_000));
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Download, new[] { Entry(remote, "/data/big.bin") }, "/data", "/c/dl");
        Assert.Equal(TransferState.Failed, await WaitFinalAsync(job));
        Assert.Equal(FailureKind.DiskFull, job.Files[0].Failure);
        Assert.DoesNotContain(local.AllPaths(), p => p.StartsWith("/c/dl/"));
    }

    [Fact]
    public async Task Retry_failed_requeues_and_revalidates()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.bin", Bytes(1000));
        remote.Dir("/data");
        var deny = true;
        remote.BeforeOperation = (op, _) =>
        {
            if (op == "create" && deny)
            {
                throw new StoreException(FailureKind.PermissionDenied, "Permission denied");
            }
        };
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.bin") }, "/c", "/data");
        Assert.Equal(TransferState.Failed, await WaitFinalAsync(job));
        Assert.Equal(1, remote.Operations.Count(o => o.StartsWith("create ")));

        deny = false;
        Assert.Equal(1, q.RetryFailed());
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));
    }

    [Fact]
    public async Task Staging_files_are_journaled_before_creation()
    {
        var (local, remote, factory) = Setup();
        local.File("/c/a.bin", Bytes(300_000));
        remote.Dir("/data");
        var journal = new RecordingJournal();
        var journaledBeforeCreate = false;
        remote.BeforeOperation = (op, path) =>
        {
            if (op == "create" && path.Contains(AppInfo.StagingPrefix))
            {
                journaledBeforeCreate = journal.Last?.Jobs.SelectMany(j => j.StagingFiles).Any(s => s.Path == path) == true;
            }
        };
        await using var q = new TransferQueue(factory, journal, FastOptions());
        var job = q.EnqueueItems(Key, TransferDirection.Upload, new[] { Entry(local, "/c/a.bin") }, "/c", "/data");
        Assert.Equal(TransferState.Completed, await WaitFinalAsync(job));
        Assert.True(journaledBeforeCreate);
    }

    [Fact]
    public async Task Sync_job_skips_items_that_changed_after_preview()
    {
        var (local, remote, factory) = Setup();
        var t = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        local.File("/c/r/a.txt", "A", t).File("/c/r/b.txt", "B", t);
        remote.Dir("/data/r");
        await using var q = new TransferQueue(factory, new NullJournal(), FastOptions());
        var items = new List<SyncItem>
        {
            new("a.txt", EntryKind.File, "/c/r/a.txt", "/data/r/a.txt", new EntrySnapshot(EntryKind.File, 1, t), new ExpectedDestination(false, null), false),
            new("b.txt", EntryKind.File, "/c/r/b.txt", "/data/r/b.txt", new EntrySnapshot(EntryKind.File, 1, t), new ExpectedDestination(false, null), false),
        };
        remote.File("/data/r/b.txt", "appeared");
        var job = q.EnqueueSync(Key, TransferDirection.Upload, "/c/r", "/data/r", items, (root, rel) => remote.CombineRelative(root, rel));
        Assert.Equal(TransferState.CompletedWithWarning, await WaitFinalAsync(job));
        Assert.Equal("A", remote.ReadText("/data/r/a.txt"));
        Assert.Equal("appeared", remote.ReadText("/data/r/b.txt"));
        Assert.Equal(FailureKind.DestinationChanged, job.Files.Single(f => f.RelativePath == "b.txt").Failure);
    }

    [Fact]
    public async Task Byte_counters_are_64_bit()
    {
        var job = TransferQueueTestAccess.NewFileTask(5_000_000_000L);
        job.SetBytesDone(4_999_999_999L);
        Assert.Equal(4_999_999_999L, job.BytesDone);
        await Task.CompletedTask;
    }

    private sealed class RecordingJournal : IJobJournal
    {
        public JournalDocument? Last;

        public void Write(JournalDocument document) => Last = document;

        public JournalDocument? Read() => Last;
    }
}

internal static class TransferQueueTestAccess
{
    public static FileTask NewFileTask(long length)
    {
        var job = (TransferJob)Activator.CreateInstance(typeof(TransferJob), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
            new object[] { JobKind.Items, TestUtil.Key, TransferDirection.Upload, "x", "/s", "/d" }, null)!;
        return (FileTask)Activator.CreateInstance(typeof(FileTask), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
            new object?[] { job, "x", "/s/x", "/d/x", length, null }, null)!;
    }
}

using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Paths;
using BridgeTransfer.Core.Storage;

namespace BridgeTransfer.Core.Transfers;

/// <summary>One file or folder of a reviewed synchronization plan.</summary>
public sealed record SyncItem(
    string RelativePath,
    EntryKind Kind,
    string SourcePath,
    string DestinationPath,
    EntrySnapshot? ExpectedSource,
    ExpectedDestination ExpectedDestination,
    bool NewerReplaceApproved);

/// <summary>
/// The transfer queue. Runs scans, folder creation and file copies on background workers with a
/// global concurrency limit. Each worker uses its own connection from <see cref="ITransferSessionFactory"/>.
/// </summary>
public sealed class TransferQueue : IAsyncDisposable, IStagingRegistry, IDestinationLocks
{
    private readonly object _gate = new();
    private readonly List<TransferJob> _jobs = new();
    private readonly Dictionary<string, Guid> _locks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedStagingFile> _staging = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SessionPool _pool;
    private readonly IJobJournal _journal;
    private readonly FileTransferExecutor _executor;
    private readonly Task _pump;
    private readonly Action<string>? _log;
    private int _running;
    private int _maxConcurrency = 2;
    private bool _journalDirty;
    private DateTime _lastTrim = DateTime.UtcNow;

    public TransferQueue(ITransferSessionFactory factory, IJobJournal journal, TransferOptions options, Action<string>? log = null)
    {
        Options = options;
        _journal = journal;
        _log = log;
        _pool = new SessionPool(factory);
        _executor = new FileTransferExecutor(options, this, this);
        _pump = Task.Run(PumpLoopAsync);
    }

    public TransferOptions Options { get; }

    /// <summary>Raised on a worker thread when a file needs a decision about an existing destination.</summary>
    public event Action<ConflictRequest>? ConflictRaised;

    /// <summary>Raised on a worker thread when a job reaches a final state.</summary>
    public event Action<TransferJob>? JobFinished;

    /// <summary>Raised when jobs are added or removed.</summary>
    public event Action? JobsChanged;

    public int MaxConcurrency
    {
        get => Volatile.Read(ref _maxConcurrency);
        set
        {
            Volatile.Write(ref _maxConcurrency, Math.Clamp(value, 1, 4));
            Signal();
        }
    }

    private volatile bool _stopStartingNew;

    /// <summary>When true, running files finish but no new file is started.</summary>
    public bool StopStartingNew
    {
        get => _stopStartingNew;
        set
        {
            _stopStartingNew = value;
            Signal();
        }
    }

    public IReadOnlyList<TransferJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.ToList();
            }
        }
    }

    public bool HasActiveWork => Jobs.Any(j => !j.State.IsFinal());

    public bool HasActiveWorkFor(ConnectionKey key) => Jobs.Any(j => j.Connection == key && !j.State.IsFinal());

    public IReadOnlyList<ConflictRequest> PendingConflicts
    {
        get
        {
            var list = new List<ConflictRequest>();
            foreach (var job in Jobs)
            {
                lock (job.Sync)
                {
                    list.AddRange(job.Files.Where(f => f.State == TransferState.WaitingForDecision && f.PendingConflict is not null).Select(f => f.PendingConflict!));
                }
            }

            return list;
        }
    }

    /// <summary>Queues selected items to be copied into <paramref name="destinationDirectory"/>.</summary>
    public TransferJob EnqueueItems(ConnectionKey connection, TransferDirection direction, IReadOnlyList<FileEntry> selection, string sourceDirectory, string destinationDirectory)
    {
        if (selection.Count == 0)
        {
            throw new ArgumentException("Nothing selected.", nameof(selection));
        }

        var name = selection.Count == 1 ? selection[0].Name : $"{selection.Count} items";
        var job = new TransferJob(JobKind.Items, connection, direction, name, sourceDirectory, destinationDirectory)
        {
            Selection = selection.ToList(),
            Phase = JobPhase.NeedsScan,
        };
        Add(job);
        return job;
    }

    /// <summary>Queues a reviewed synchronization plan. Every item carries the state it had at review time.</summary>
    public TransferJob EnqueueSync(ConnectionKey connection, TransferDirection direction, string sourceRoot, string destinationRoot, IReadOnlyList<SyncItem> items, Func<string, string, string> combineDestination)
    {
        var fileCount = items.Count(i => i.Kind == EntryKind.File);
        var job = new TransferJob(JobKind.Sync, connection, direction, $"Sync: {fileCount} files", sourceRoot, destinationRoot)
        {
            Phase = JobPhase.NeedsDirectories,
        };

        var dirs = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Kind == EntryKind.Directory)
            {
                dirs[item.RelativePath] = true;
            }

            for (var p = RelativePath.GetParent(item.RelativePath); !string.IsNullOrEmpty(p); p = RelativePath.GetParent(p))
            {
                dirs.TryAdd(p, false);
            }
        }

        foreach (var (rel, explicitDir) in dirs.OrderBy(d => RelativePath.Depth(d.Key)).ThenBy(d => d.Key, StringComparer.Ordinal))
        {
            job.Directories.Add(new DirectoryTask(rel, combineDestination(destinationRoot, rel), explicitDir));
        }

        foreach (var item in items.Where(i => i.Kind == EntryKind.File))
        {
            job.Files.Add(new FileTask(job, item.RelativePath, item.SourcePath, item.DestinationPath, item.ExpectedSource?.Length ?? 0, item.ExpectedSource?.ModifiedUtc)
            {
                ExpectedSource = item.ExpectedSource,
                ExpectedDestination = item.ExpectedDestination,
                NewerReplaceApproved = item.NewerReplaceApproved,
            });
        }

        Add(job);
        return job;
    }

    private void Add(TransferJob job)
    {
        lock (_gate)
        {
            _jobs.Add(job);
            _journalDirty = true;
        }

        JobsChanged?.Invoke();
        Signal();
    }

    public void Cancel(TransferJob job)
    {
        lock (job.Sync)
        {
            if (job.State.IsFinal())
            {
                return;
            }

            job.CancelRequested = true;
            foreach (var f in job.Files.Where(f => f.State is TransferState.Queued or TransferState.WaitingForDecision))
            {
                f.State = TransferState.Cancelled;
                f.Message = "Cancelled before it started.";
                f.PendingConflict = null;
            }

            foreach (var d in job.Directories.Where(d => !d.State.IsFinal()))
            {
                d.State = TransferState.Cancelled;
            }
        }

        try
        {
            job.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        MarkDirty();
        CheckFinished(job);
        Signal();
    }

    public void CancelAll()
    {
        foreach (var job in Jobs)
        {
            Cancel(job);
        }
    }

    /// <summary>Cancels all work bound to a connection (used before disconnecting or switching servers).</summary>
    public async Task CancelForConnectionAsync(ConnectionKey key)
    {
        foreach (var job in Jobs.Where(j => j.Connection == key))
        {
            Cancel(job);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && Jobs.Any(j => j.Connection == key && !j.State.IsFinal()))
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        await _pool.CloseAsync(key).ConfigureAwait(false);
    }

    /// <summary>Re-queues failed files and folders. The usual checks run again before anything is written.</summary>
    public int RetryFailed(IEnumerable<TransferJob>? jobs = null)
    {
        var count = 0;
        foreach (var job in (jobs ?? Jobs).Where(j => !j.CancelRequested))
        {
            lock (job.Sync)
            {
                if (job.JobFailure is not null)
                {
                    job.JobFailure = null;
                    job.JobMessage = null;
                    job.ScanAttempts = 0;
                    job.Phase = job.Kind == JobKind.Items && job.Files.Count == 0 && job.Directories.Count == 0 ? JobPhase.NeedsScan : JobPhase.NeedsDirectories;
                    count++;
                }

                if (job.Directories.Any(d => d.State == TransferState.Failed) || job.Files.Any(f => f.State == TransferState.Skipped && f.Failure == FailureKind.TypeConflict && f.Message == ParentProblemMessage))
                {
                    foreach (var d in job.Directories.Where(d => d.State == TransferState.Failed))
                    {
                        d.State = TransferState.Queued;
                        d.Message = null;
                        d.Failure = null;
                    }

                    foreach (var f in job.Files.Where(f => f.Message == ParentProblemMessage))
                    {
                        f.Reset();
                    }

                    job.Phase = JobPhase.NeedsDirectories;
                    count++;
                }

                foreach (var f in job.Files.Where(f => f.State == TransferState.Failed))
                {
                    f.Reset();
                    count++;
                }

                job.FinishedUtc = null;
                job.FinishedRaised = false;
            }
        }

        MarkDirty();
        Signal();
        return count;
    }

    public void ClearFinished()
    {
        lock (_gate)
        {
            _jobs.RemoveAll(j => j.State.IsFinal());
            _journalDirty = true;
        }

        JobsChanged?.Invoke();
    }

    /// <summary>Applies the user's decision for one conflict, optionally to the rest of the batch.</summary>
    public void ResolveConflict(ConflictRequest request, ConflictChoice choice, bool applyToRemaining)
    {
        if (choice == ConflictChoice.Ask)
        {
            return;
        }

        var job = Jobs.FirstOrDefault(j => j.Id == request.JobId);
        if (job is null)
        {
            return;
        }

        if (choice == ConflictChoice.Replace && !request.ReplaceAllowed)
        {
            throw new InvalidOperationException("A folder or link cannot be replaced by a file.");
        }

        lock (job.Sync)
        {
            var task = job.Files.FirstOrDefault(f => f.Id == request.TaskId);
            if (task is null || task.State != TransferState.WaitingForDecision)
            {
                return;
            }

            task.Decision = choice;
            task.NewerReplaceApproved = choice == ConflictChoice.Replace && request.Kind == ConflictKind.DestinationNewer;
            task.PendingConflict = null;
            task.State = TransferState.Queued;
            task.Message = choice switch
            {
                ConflictChoice.Skip => "Will skip.",
                ConflictChoice.Replace => "Will replace the existing file.",
                _ => "Will keep both files.",
            };

            if (applyToRemaining)
            {
                job.ConflictPolicy.ApplyToRemaining(request.Kind, choice);
                foreach (var other in job.Files.Where(f => f.State == TransferState.WaitingForDecision && f.PendingConflict is not null))
                {
                    var c = job.ConflictPolicy.ChoiceFor(other.PendingConflict!.Kind);
                    if (c != ConflictChoice.Ask)
                    {
                        other.PendingConflict = null;
                        other.Decision = null;
                        other.State = TransferState.Queued;
                    }
                }
            }
        }

        MarkDirty();
        Signal();
    }

    /// <summary>Owned staging files that could not be removed yet.</summary>
    public IReadOnlyList<OwnedStagingFile> OutstandingStagingFiles
    {
        get
        {
            lock (_gate)
            {
                return _staging.Values.ToList();
            }
        }
    }

    // ---- IStagingRegistry ----
    void IStagingRegistry.Register(TransferJob job, FileTask task, EndpointSide side, string path)
    {
        lock (_gate)
        {
            _staging[path] = new OwnedStagingFile { Side = side, Path = path, JobId = job.Id, TaskId = task.Id, CreatedUtc = DateTime.UtcNow };
            _journalDirty = true;
        }

        // Persist before the file is created so a crash never leaves an unrecorded staging file.
        FlushJournal();
    }

    void IStagingRegistry.Unregister(string path)
    {
        lock (_gate)
        {
            if (_staging.Remove(path))
            {
                _journalDirty = true;
            }
        }
    }

    // ---- IDestinationLocks ----
    private static string LockKey(TransferJob job, string path) =>
        job.Direction.DestinationSide() == EndpointSide.Local
            ? "L|" + path.ToUpperInvariant()
            : $"R|{job.Connection.Host.ToLowerInvariant()}:{job.Connection.Port}|{path}";

    bool IDestinationLocks.TryAcquire(TransferJob job, string path, Guid owner)
    {
        lock (_gate)
        {
            var key = LockKey(job, path);
            if (_locks.TryGetValue(key, out var existing))
            {
                return existing == owner;
            }

            _locks[key] = owner;
            return true;
        }
    }

    void IDestinationLocks.Release(TransferJob job, string path, Guid owner)
    {
        lock (_gate)
        {
            var key = LockKey(job, path);
            if (_locks.TryGetValue(key, out var existing) && existing == owner)
            {
                _locks.Remove(key);
            }
        }
    }

    // ---- Scheduling ----
    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void MarkDirty()
    {
        lock (_gate)
        {
            _journalDirty = true;
        }
    }

    private async Task PumpLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(400), _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                StartWork();
                SampleMeters();
                FlushJournal();
                if (DateTime.UtcNow - _lastTrim > TimeSpan.FromSeconds(10))
                {
                    _lastTrim = DateTime.UtcNow;
                    await _pool.TrimAsync(Options.IdleSessionLifetime).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("Queue pump error: " + ex);
            }
        }
    }

    private void SampleMeters()
    {
        foreach (var job in Jobs)
        {
            long bytes;
            lock (job.Sync)
            {
                if (job.State != TransferState.Transferring)
                {
                    job.Meter.Reset();
                    continue;
                }

                bytes = job.Files.Sum(f => f.State is TransferState.Completed or TransferState.CompletedWithWarning ? f.ExpectedLength : f.State == TransferState.Transferring ? f.BytesDone : 0);
            }

            job.Meter.Record(bytes);
        }
    }

    private void StartWork()
    {
        if (StopStartingNew)
        {
            return;
        }

        var starts = new List<Func<Task>>();
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var job in _jobs)
            {
                if (_running >= MaxConcurrency)
                {
                    break;
                }

                lock (job.Sync)
                {
                    if (job.CancelRequested || job.JobFailure is not null)
                    {
                        continue;
                    }

                    switch (job.Phase)
                    {
                        case JobPhase.NeedsScan when now >= job.ScanNotBeforeUtc:
                            job.Phase = JobPhase.Scanning;
                            _running++;
                            starts.Add(() => RunScanAsync(job));
                            continue;
                        case JobPhase.NeedsDirectories when now >= job.ScanNotBeforeUtc:
                            job.Phase = JobPhase.CreatingDirectories;
                            _running++;
                            starts.Add(() => RunDirectoriesAsync(job));
                            continue;
                        case JobPhase.Files:
                            foreach (var task in job.Files)
                            {
                                if (_running >= MaxConcurrency)
                                {
                                    break;
                                }

                                if (task.State != TransferState.Queued || task.NotBeforeUtc > now)
                                {
                                    continue;
                                }

                                var key = LockKey(job, task.DestinationPath);
                                if (_locks.TryGetValue(key, out var owner) && owner != task.Id)
                                {
                                    continue; // Another task is writing this destination; wait.
                                }

                                _locks[key] = task.Id;
                                task.State = TransferState.Transferring;
                                task.Message = null;
                                _running++;
                                var t = task;
                                starts.Add(() => RunFileAsync(t));
                            }

                            break;
                    }
                }
            }
        }

        foreach (var start in starts)
        {
            _ = Task.Run(start);
        }
    }

    private void WorkDone()
    {
        lock (_gate)
        {
            _running--;
            _journalDirty = true;
        }

        Signal();
    }

    private (IFileStore Source, IFileStore Destination) Stores(TransferJob job, ITransferSession session) =>
        job.Direction == TransferDirection.Upload ? (session.Local, session.Remote) : (session.Remote, session.Local);

    private async Task RunScanAsync(TransferJob job)
    {
        ITransferSession? session = null;
        var healthy = true;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token, _shutdown.Token);
            session = await _pool.RentAsync(job.Connection, cts.Token).ConfigureAwait(false);
            var (src, dst) = Stores(job, session);
            var plan = await TransferPlanner.PlanAsync(src, dst, job.Selection, job.DestinationDirectory, n => job.ScannedFolders = n, cts.Token).ConfigureAwait(false);
            lock (job.Sync)
            {
                job.Directories.Clear();
                job.Files.Clear();
                job.ScanSkipped.Clear();
                foreach (var d in plan.Directories)
                {
                    job.Directories.Add(new DirectoryTask(d.RelativePath, d.DestinationPath, true));
                }

                foreach (var f in plan.Files)
                {
                    job.Files.Add(new FileTask(job, f.RelativePath, f.SourcePath, f.DestinationPath, f.Length, f.ModifiedUtc));
                }

                job.ScanSkipped.AddRange(plan.Skipped);
                job.Phase = JobPhase.NeedsDirectories;
            }
        }
        catch (OperationCanceledException)
        {
            lock (job.Sync)
            {
                job.Phase = JobPhase.Files;
            }
        }
        catch (StoreException ex) when (ex.IsTransient)
        {
            healthy = false;
            lock (job.Sync)
            {
                job.ScanAttempts++;
                if (job.ScanAttempts < Options.MaxAttempts)
                {
                    job.Phase = JobPhase.NeedsScan;
                    job.ScanNotBeforeUtc = DateTime.UtcNow + Options.DelayFor(job.ScanAttempts);
                    job.JobMessage = $"Connection problem while scanning; retrying (attempt {job.ScanAttempts + 1} of {Options.MaxAttempts}).";
                }
                else
                {
                    job.Phase = JobPhase.Files;
                    job.JobFailure = ex.Kind;
                    job.JobMessage = "Scanning failed after several attempts: " + ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            healthy = ex is not StoreException;
            lock (job.Sync)
            {
                job.Phase = JobPhase.Files;
                job.JobFailure = ex is StoreException se ? se.Kind : FailureKind.Unknown;
                job.JobMessage = "Could not read the selection: " + ex.Message;
            }

            _log?.Invoke($"Scan failed for job {job.Id}: {ex}");
        }
        finally
        {
            if (session is not null)
            {
                _pool.Return(job.Connection, session, healthy);
            }

            WorkDone();
            CheckFinished(job);
        }
    }

    internal const string ParentProblemMessage = "Skipped because its folder could not be created on the destination.";

    private async Task RunDirectoriesAsync(TransferJob job)
    {
        ITransferSession? session = null;
        var healthy = true;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token, _shutdown.Token);
            session = await _pool.RentAsync(job.Connection, cts.Token).ConfigureAwait(false);
            var (_, dst) = Stores(job, session);

            var root = await dst.GetEntryAsync(job.DestinationDirectory, cts.Token).ConfigureAwait(false);
            if (root is null || !(root.IsDirectory || root.IsLink))
            {
                lock (job.Sync)
                {
                    job.Phase = JobPhase.Files;
                    job.JobFailure = FailureKind.NotFound;
                    job.JobMessage = $"The destination folder {job.DestinationDirectory} does not exist.";
                    foreach (var f in job.Files.Where(f => !f.State.IsFinal()))
                    {
                        f.State = TransferState.Skipped;
                        f.Message = "The destination folder does not exist.";
                    }
                }

                return;
            }

            List<DirectoryTask> dirs;
            lock (job.Sync)
            {
                dirs = job.Directories.Where(d => !d.State.IsFinal()).OrderBy(d => RelativePath.Depth(d.RelativePath)).ToList();
            }

            var failedRel = new List<string>();
            foreach (var dir in dirs)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (failedRel.Any(f => RelativePath.IsSameOrBelow(f, dir.RelativePath, StringComparison.Ordinal)))
                {
                    lock (job.Sync)
                    {
                        dir.State = TransferState.Skipped;
                        dir.Message = "Its parent folder could not be created.";
                    }

                    continue;
                }

                var (state, kind, message) = await EnsureDirectoryAsync(dst, dir.DestinationPath, cts.Token).ConfigureAwait(false);
                lock (job.Sync)
                {
                    dir.State = state;
                    dir.Failure = kind;
                    dir.Message = message;
                }

                if (state == TransferState.Failed)
                {
                    failedRel.Add(dir.RelativePath);
                }
            }

            lock (job.Sync)
            {
                foreach (var d in job.Directories.Where(d => d.State == TransferState.Failed))
                {
                    failedRel.Add(d.RelativePath);
                }

                foreach (var f in job.Files.Where(f => f.State == TransferState.Queued))
                {
                    if (failedRel.Any(r => RelativePath.IsSameOrBelow(r, f.RelativePath, StringComparison.Ordinal) && r != f.RelativePath))
                    {
                        f.State = TransferState.Skipped;
                        f.Failure = FailureKind.TypeConflict;
                        f.Message = ParentProblemMessage;
                    }
                }

                job.Phase = JobPhase.Files;
                job.JobMessage = null;
            }
        }
        catch (OperationCanceledException)
        {
            lock (job.Sync)
            {
                job.Phase = JobPhase.Files;
            }
        }
        catch (StoreException ex) when (ex.IsTransient)
        {
            healthy = false;
            lock (job.Sync)
            {
                job.ScanAttempts++;
                if (job.ScanAttempts < Options.MaxAttempts)
                {
                    job.Phase = JobPhase.NeedsDirectories;
                    job.ScanNotBeforeUtc = DateTime.UtcNow + Options.DelayFor(job.ScanAttempts);
                    job.JobMessage = "Connection problem while creating folders; retrying.";
                }
                else
                {
                    job.Phase = JobPhase.Files;
                    job.JobFailure = ex.Kind;
                    job.JobMessage = "Creating folders failed after several attempts: " + ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            healthy = ex is not StoreException;
            lock (job.Sync)
            {
                job.Phase = JobPhase.Files;
                job.JobFailure = ex is StoreException se ? se.Kind : FailureKind.Unknown;
                job.JobMessage = "Could not prepare the destination: " + ex.Message;
            }

            _log?.Invoke($"Directory phase failed for job {job.Id}: {ex}");
        }
        finally
        {
            if (session is not null)
            {
                _pool.Return(job.Connection, session, healthy);
            }

            WorkDone();
            CheckFinished(job);
        }
    }

    private static async Task<(TransferState, FailureKind?, string?)> EnsureDirectoryAsync(IFileStore dst, string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var entry = await dst.GetEntryAsync(path, ct).ConfigureAwait(false);
            if (entry is null)
            {
                try
                {
                    await dst.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
                    return (TransferState.Completed, null, null);
                }
                catch (StoreException ex) when (ex.Kind == FailureKind.AlreadyExists)
                {
                    continue;
                }
                catch (StoreException ex) when (!ex.IsTransient && ex.Kind != FailureKind.Cancelled)
                {
                    return (TransferState.Failed, ex.Kind, "The folder could not be created: " + ex.Message);
                }
            }

            return entry.Kind switch
            {
                EntryKind.Directory => (TransferState.Completed, null, "Folder already existed; contents were merged."),
                EntryKind.SymbolicLink => (TransferState.Failed, FailureKind.LinkRedirect, "The destination folder is a link. Writing through links is refused."),
                _ => (TransferState.Failed, FailureKind.TypeConflict, "A file with this name exists where a folder is needed."),
            };
        }

        return (TransferState.Failed, FailureKind.DestinationChanged, "The destination kept changing while the folder was being created.");
    }

    private async Task RunFileAsync(FileTask task)
    {
        var job = task.Job;
        ITransferSession? session = null;
        var healthy = true;
        FileOutcome outcome;
        var lockedPath = task.DestinationPath;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token, _shutdown.Token);
        task.Cts = cts;
        try
        {
            session = await _pool.RentAsync(job.Connection, cts.Token).ConfigureAwait(false);
            var (src, dst) = Stores(job, session);
            outcome = await _executor.ExecuteAsync(task, src, dst, cts.Token).ConfigureAwait(false);
            if (outcome.Kind == FileOutcomeKind.Retry)
            {
                healthy = false;
            }
        }
        catch (OperationCanceledException)
        {
            outcome = new FileOutcome(FileOutcomeKind.Cancelled, "Cancelled. The destination was left unchanged.", FailureKind.Cancelled);
        }
        catch (StoreException ex)
        {
            healthy = false;
            outcome = ex.IsTransient
                ? new FileOutcome(FileOutcomeKind.Retry, ex.Message, ex.Kind)
                : new FileOutcome(FileOutcomeKind.Failed, ex.Message, ex.Kind);
        }
        catch (Exception ex)
        {
            healthy = false;
            _log?.Invoke($"Unexpected error for {task.SourcePath}: {ex}");
            outcome = new FileOutcome(FileOutcomeKind.Failed, "Unexpected error: " + ex.Message, FailureKind.Unknown);
        }
        finally
        {
            task.Cts = null;
            if (session is not null)
            {
                _pool.Return(job.Connection, session, healthy);
            }
        }

        ConflictRequest? raise = null;

        // Lock order is always _gate before job.Sync; release destination locks before taking job.Sync.
        ((IDestinationLocks)this).Release(job, lockedPath, task.Id);
        ((IDestinationLocks)this).Release(job, task.DestinationPath, task.Id);
        lock (job.Sync)
        {
            task.Message = outcome.Message;
            task.Failure = outcome.Failure;
            switch (outcome.Kind)
            {
                case FileOutcomeKind.Completed:
                    task.State = TransferState.Completed;
                    break;
                case FileOutcomeKind.CompletedWithWarning:
                    task.State = TransferState.CompletedWithWarning;
                    task.HasWarning = true;
                    break;
                case FileOutcomeKind.Skipped:
                    task.State = TransferState.Skipped;
                    break;
                case FileOutcomeKind.Failed:
                    task.State = TransferState.Failed;
                    break;
                case FileOutcomeKind.Cancelled:
                    task.State = TransferState.Cancelled;
                    break;
                case FileOutcomeKind.NeedsDecision when job.CancelRequested:
                    task.State = TransferState.Cancelled;
                    break;
                case FileOutcomeKind.NeedsDecision:
                    task.State = TransferState.WaitingForDecision;
                    task.PendingConflict = outcome.Conflict;
                    raise = outcome.Conflict;
                    break;
                case FileOutcomeKind.Requeue:
                    task.Requeues++;
                    if (task.Requeues > 5)
                    {
                        task.State = TransferState.Failed;
                        task.Failure = FailureKind.DestinationChanged;
                        task.Message = "The destination kept changing during the transfer.";
                    }
                    else
                    {
                        task.State = job.CancelRequested ? TransferState.Cancelled : TransferState.Queued;
                    }

                    break;
                case FileOutcomeKind.Retry:
                    task.Attempts++;
                    if (job.CancelRequested)
                    {
                        task.State = TransferState.Cancelled;
                    }
                    else if (task.Attempts < Options.MaxAttempts)
                    {
                        task.State = TransferState.Queued;
                        task.NotBeforeUtc = DateTime.UtcNow + Options.DelayFor(task.Attempts);
                        task.Message = $"Connection problem; will retry (attempt {task.Attempts + 1} of {Options.MaxAttempts}). {outcome.Message}";
                    }
                    else
                    {
                        task.State = TransferState.Failed;
                        task.Message = $"Failed after {Options.MaxAttempts} attempts: {outcome.Message}";
                    }

                    break;
            }
        }

        WorkDone();
        if (raise is not null)
        {
            ConflictRaised?.Invoke(raise);
        }

        CheckFinished(job);
    }

    private void CheckFinished(TransferJob job)
    {
        bool raise;
        lock (job.Sync)
        {
            var state = job.ComputeState();
            raise = state.IsFinal() && !job.FinishedRaised;
            if (raise)
            {
                job.FinishedRaised = true;
                job.FinishedUtc = DateTime.UtcNow;
            }
        }

        if (raise)
        {
            MarkDirty();
            JobFinished?.Invoke(job);
        }
    }

    // ---- Journal ----
    private void FlushJournal()
    {
        JournalDocument doc;
        lock (_gate)
        {
            if (!_journalDirty)
            {
                return;
            }

            _journalDirty = false;
            doc = new JournalDocument { WrittenUtc = DateTime.UtcNow };
            foreach (var job in _jobs)
            {
                var staging = _staging.Values.Where(s => s.JobId == job.Id).ToList();
                var state = job.State;
                if (state.IsFinal() && staging.Count == 0)
                {
                    continue;
                }

                var snap = job.Snapshot();
                doc.Jobs.Add(new JournalJob
                {
                    JobId = job.Id,
                    Kind = job.Kind,
                    DisplayName = job.DisplayName,
                    Direction = job.Direction,
                    ProfileId = job.Connection.ProfileId,
                    Host = job.Connection.Host,
                    Port = job.Connection.Port,
                    Username = job.Connection.Username,
                    SourcePaths = job.Selection.Select(s => s.FullPath).ToList(),
                    SourceDescription = job.SourceDescription,
                    DestinationDirectory = job.DestinationDirectory,
                    LastState = state.Label(),
                    TotalFiles = snap.TotalFiles,
                    FinishedFiles = snap.CompletedFiles + snap.FailedItems + snap.CancelledFiles,
                    CreatedUtc = job.CreatedUtc,
                    StagingFiles = staging,
                });
            }

            // Staging files whose job was cleared from the list are still recorded.
            foreach (var orphan in _staging.Values.Where(s => _jobs.All(j => j.Id != s.JobId)).GroupBy(s => s.JobId))
            {
                doc.Jobs.Add(new JournalJob { JobId = orphan.Key, DisplayName = "Leftover partial files", LastState = "Cancelled", StagingFiles = orphan.ToList(), CreatedUtc = DateTime.UtcNow });
            }
        }

        try
        {
            _journal.Write(doc);
        }
        catch (Exception ex)
        {
            _log?.Invoke("Could not write queue journal: " + ex.Message);
            MarkDirty();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref _running) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _journalDirty = true;
        }

        FlushJournal();
        await _pool.CloseAsync(null).ConfigureAwait(false);
        _signal.Dispose();
    }
}

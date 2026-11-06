using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;

namespace BridgeTransfer.Core.Transfers;

/// <summary>Expected state of a destination when a synchronization plan was reviewed.</summary>
public sealed record ExpectedDestination(bool Exists, EntrySnapshot? Snapshot);

/// <summary>One file inside a job.</summary>
public sealed class FileTask
{
    internal FileTask(TransferJob job, string relativePath, string sourcePath, string destinationPath, long length, DateTime? sourceModifiedUtc)
    {
        Job = job;
        RelativePath = relativePath;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        OriginalDestinationPath = destinationPath;
        ExpectedLength = length;
        SourceModifiedUtc = sourceModifiedUtc;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public TransferJob Job { get; }

    public string RelativePath { get; }

    public string SourcePath { get; }

    /// <summary>Final destination; changes when the user chooses "Keep both".</summary>
    public string DestinationPath { get; internal set; }

    public string OriginalDestinationPath { get; }

    public long ExpectedLength { get; internal set; }

    public DateTime? SourceModifiedUtc { get; internal set; }

    public TransferState State { get; internal set; } = TransferState.Queued;

    public string? Message { get; internal set; }

    public FailureKind? Failure { get; internal set; }

    public bool HasWarning { get; internal set; }

    private long _bytesDone;

    public long BytesDone => Interlocked.Read(ref _bytesDone);

    internal void SetBytesDone(long value) => Interlocked.Exchange(ref _bytesDone, value);

    public int Attempts { get; internal set; }

    internal DateTime NotBeforeUtc { get; set; }

    /// <summary>Decision for an existing destination, set by the user or batch policy.</summary>
    public ConflictChoice? Decision { get; internal set; }

    /// <summary>The user explicitly approved replacing a destination that is newer than the source.</summary>
    public bool NewerReplaceApproved { get; internal set; }

    /// <summary>For synchronization jobs: what the source looked like at preview time.</summary>
    public EntrySnapshot? ExpectedSource { get; internal set; }

    /// <summary>For synchronization jobs: what the destination looked like at preview time.</summary>
    public ExpectedDestination? ExpectedDestination { get; internal set; }

    /// <summary>Destination metadata captured when the user chose Replace; must still match at promotion.</summary>
    internal EntrySnapshot? ReplaceTarget { get; set; }

    /// <summary>Staging files created by this task that may still exist (owned by this application).</summary>
    internal List<string> OwnedStagingPaths { get; } = new();

    /// <summary>Set while a rename was in flight, so a retry can reconcile an uncertain outcome.</summary>
    internal (string Staging, string Final, EntrySnapshot Source)? PendingPromotion { get; set; }

    internal CancellationTokenSource? Cts { get; set; }

    internal int Requeues { get; set; }

    /// <summary>Ignore the folder listing and check the live destination (set after a surprise).</summary>
    internal bool FreshDestination { get; set; }

    /// <summary>The open question while this file waits for a decision.</summary>
    public ConflictRequest? PendingConflict { get; internal set; }

    internal void Reset()
    {
        State = TransferState.Queued;
        Message = null;
        Failure = null;
        HasWarning = false;
        Attempts = 0;
        Requeues = 0;
        PendingConflict = null;
        SetBytesDone(0);
        NotBeforeUtc = default;
    }
}

/// <summary>A folder that must exist on the destination before files are written.</summary>
public sealed class DirectoryTask
{
    internal DirectoryTask(string relativePath, string destinationPath, bool explicitlySelected)
    {
        RelativePath = relativePath;
        DestinationPath = destinationPath;
        ExplicitlySelected = explicitlySelected;
    }

    public string RelativePath { get; }

    public string DestinationPath { get; }

    public bool ExplicitlySelected { get; }

    public TransferState State { get; internal set; } = TransferState.Queued;

    public string? Message { get; internal set; }

    public FailureKind? Failure { get; internal set; }
}

public enum JobKind
{
    /// <summary>Selected files and folders copied into a destination folder.</summary>
    Items,

    /// <summary>A reviewed one-way synchronization plan.</summary>
    Sync,
}

internal enum JobPhase
{
    NeedsScan,
    Scanning,
    NeedsDirectories,
    CreatingDirectories,
    Files,
}

/// <summary>One user action: a set of selected items, or one reviewed synchronization plan.</summary>
public sealed class TransferJob
{
    internal readonly object Sync = new();

    internal TransferJob(JobKind kind, ConnectionKey connection, TransferDirection direction, string displayName, string sourceDescription, string destinationDirectory)
    {
        Kind = kind;
        Connection = connection;
        Direction = direction;
        DisplayName = displayName;
        SourceDescription = sourceDescription;
        DestinationDirectory = destinationDirectory;
        InitIndex();
    }

    public Guid Id { get; } = Guid.NewGuid();

    public JobKind Kind { get; }

    public ConnectionKey Connection { get; }

    public TransferDirection Direction { get; }

    public string DisplayName { get; }

    /// <summary>Source folder or description shown in the queue.</summary>
    public string SourceDescription { get; }

    /// <summary>The folder the selection is copied into.</summary>
    public string DestinationDirectory { get; }

    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    public IReadOnlyList<FileEntry> Selection { get; internal set; } = Array.Empty<FileEntry>();

    internal JobPhase Phase { get; set; }

    internal int ScanAttempts { get; set; }

    internal DateTime ScanNotBeforeUtc { get; set; }

    public List<FileTask> Files { get; } = new();

    public List<DirectoryTask> Directories { get; } = new();

    public List<PlannedSkip> ScanSkipped { get; } = new();

    public BatchConflictPolicy ConflictPolicy { get; } = new();

    /// <summary>Destination folder listings taken while preparing folders.</summary>
    public DestinationIndex DestinationIndex { get; private set; } = null!;

    internal void InitIndex() => DestinationIndex = new DestinationIndex(Direction.DestinationSide() == EndpointSide.Local);

    public bool CancelRequested { get; internal set; }

    internal CancellationTokenSource Cts { get; } = new();

    public string? JobMessage { get; internal set; }

    public FailureKind? JobFailure { get; internal set; }

    public bool IsScanning => Phase is JobPhase.NeedsScan or JobPhase.Scanning;

    public int ScannedFolders { get; internal set; }

    internal ThroughputMeter Meter { get; } = new();

    public DateTime? FinishedUtc { get; internal set; }

    internal bool FinishedRaised { get; set; }

    /// <summary>Aggregated state of the whole job.</summary>
    public TransferState State
    {
        get
        {
            lock (Sync)
            {
                return ComputeState();
            }
        }
    }

    internal TransferState ComputeState()
    {
        if (JobFailure is not null)
        {
            return TransferState.Failed;
        }

        if (Phase is JobPhase.NeedsScan)
        {
            return CancelRequested ? TransferState.Cancelled : TransferState.Queued;
        }

        if (Phase is JobPhase.Scanning)
        {
            return TransferState.Scanning;
        }

        if (CancelRequested && Phase == JobPhase.Files && Files.Count == 0)
        {
            return TransferState.Cancelled;
        }

        var anyRunning = false;
        var anyQueued = false;
        var anyWaiting = false;
        int failed = 0, cancelled = 0, skipped = 0, warn = 0, done = 0;
        foreach (var f in Files)
        {
            switch (f.State)
            {
                case TransferState.Transferring:
                    anyRunning = true;
                    break;
                case TransferState.Queued:
                    anyQueued = true;
                    break;
                case TransferState.WaitingForDecision:
                    anyWaiting = true;
                    break;
                case TransferState.Failed:
                    failed++;
                    break;
                case TransferState.Cancelled:
                    cancelled++;
                    break;
                case TransferState.Skipped:
                    skipped++;
                    break;
                case TransferState.CompletedWithWarning:
                    warn++;
                    break;
                case TransferState.Completed:
                    done++;
                    break;
            }
        }

        var dirFailed = Directories.Count(d => d.State == TransferState.Failed);
        var dirPending = Directories.Any(d => !d.State.IsFinal());
        if (Phase is JobPhase.NeedsDirectories or JobPhase.CreatingDirectories)
        {
            return CancelRequested && Phase == JobPhase.NeedsDirectories ? TransferState.Cancelled : (Phase == JobPhase.CreatingDirectories ? TransferState.Transferring : TransferState.Queued);
        }

        if (anyRunning)
        {
            return TransferState.Transferring;
        }

        if (anyQueued || dirPending)
        {
            return done + warn + failed + skipped > 0 || anyWaiting ? TransferState.Transferring : TransferState.Queued;
        }

        if (anyWaiting)
        {
            return TransferState.WaitingForDecision;
        }

        if (failed > 0 || dirFailed > 0)
        {
            return TransferState.Failed;
        }

        if (cancelled > 0)
        {
            return TransferState.Cancelled;
        }

        if (done + warn == 0 && Directories.All(d => d.State != TransferState.Completed))
        {
            return skipped > 0 || ScanSkipped.Count > 0 ? TransferState.Skipped : TransferState.Completed;
        }

        if (warn > 0 || skipped > 0 || ScanSkipped.Count > 0)
        {
            return TransferState.CompletedWithWarning;
        }

        return TransferState.Completed;
    }

    public JobSnapshot Snapshot()
    {
        lock (Sync)
        {
            long total = 0, doneBytes = 0;
            int completed = 0, failed = 0, skipped = 0, cancelled = 0, waiting = 0, warnings = 0;
            string? current = null;
            foreach (var f in Files)
            {
                total += f.ExpectedLength;
                switch (f.State)
                {
                    case TransferState.Completed:
                        completed++;
                        doneBytes += f.ExpectedLength;
                        break;
                    case TransferState.CompletedWithWarning:
                        completed++;
                        warnings++;
                        doneBytes += f.ExpectedLength;
                        break;
                    case TransferState.Failed:
                        failed++;
                        break;
                    case TransferState.Skipped:
                        skipped++;
                        break;
                    case TransferState.Cancelled:
                        cancelled++;
                        break;
                    case TransferState.WaitingForDecision:
                        waiting++;
                        break;
                    case TransferState.Transferring:
                        doneBytes += f.BytesDone;
                        current ??= f.RelativePath;
                        break;
                }
            }

            failed += Directories.Count(d => d.State == TransferState.Failed);
            var state = ComputeState();
            var speed = state == TransferState.Transferring ? Meter.BytesPerSecond : 0;
            TimeSpan? eta = null;
            if (speed > 0 && Meter.HasEnoughSamples && total > doneBytes)
            {
                eta = TimeSpan.FromSeconds((total - doneBytes) / speed);
            }

            return new JobSnapshot(
                Id, DisplayName, Direction, SourceDescription, DestinationDirectory, state,
                Files.Count, completed, failed, skipped + ScanSkipped.Count, cancelled, waiting, warnings,
                total, doneBytes, speed, eta, current, JobMessage, IsScanning, ScannedFolders, Kind);
        }
    }
}

public sealed record JobSnapshot(
    Guid Id,
    string DisplayName,
    TransferDirection Direction,
    string Source,
    string Destination,
    TransferState State,
    int TotalFiles,
    int CompletedFiles,
    int FailedItems,
    int SkippedItems,
    int CancelledFiles,
    int WaitingFiles,
    int Warnings,
    long TotalBytes,
    long TransferredBytes,
    double BytesPerSecond,
    TimeSpan? Remaining,
    string? CurrentFile,
    string? Message,
    bool IsScanning,
    int ScannedFolders,
    JobKind Kind)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)TransferredBytes / TotalBytes, 0, 1) : (State.IsFinal() ? 1 : 0);
}

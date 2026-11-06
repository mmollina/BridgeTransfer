namespace BridgeTransfer.Core.Transfers;

public enum TransferState
{
    Queued,
    Scanning,
    Transferring,
    WaitingForDecision,
    Completed,
    CompletedWithWarning,
    Failed,
    Cancelled,
    Skipped,
}

public static class TransferStateExtensions
{
    public static bool IsFinal(this TransferState s) =>
        s is TransferState.Completed or TransferState.CompletedWithWarning or TransferState.Failed or TransferState.Cancelled or TransferState.Skipped;

    public static string Label(this TransferState s) => s switch
    {
        TransferState.Queued => "Queued",
        TransferState.Scanning => "Scanning",
        TransferState.Transferring => "Transferring",
        TransferState.WaitingForDecision => "Waiting for decision",
        TransferState.Completed => "Completed",
        TransferState.CompletedWithWarning => "Completed with warning",
        TransferState.Failed => "Failed",
        TransferState.Cancelled => "Cancelled",
        TransferState.Skipped => "Skipped",
        _ => s.ToString(),
    };
}

/// <summary>What to do when the destination file already exists.</summary>
public enum ConflictChoice
{
    Ask,
    Skip,
    Replace,
    KeepBoth,
}

/// <summary>Why an entry was not included in a transfer or comparison.</summary>
public enum SkipReason
{
    SymbolicLink,
    UnsupportedName,
    CaseCollision,
    Unreadable,
    SpecialFile,
    ParentProblem,
}

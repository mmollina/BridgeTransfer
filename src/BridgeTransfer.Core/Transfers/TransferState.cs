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

    /// <summary>Replace only if the new file is newer; otherwise skip.</summary>
    ReplaceIfNewer,

    /// <summary>Replace only if the sizes differ; otherwise skip.</summary>
    ReplaceIfSizeDiffers,

    /// <summary>Replace if the new file is newer or the sizes differ; otherwise skip.</summary>
    ReplaceIfNewerOrSizeDiffers,
}

/// <summary>How far a conflict decision reaches.</summary>
public enum ConflictScope
{
    ThisFile,
    ThisTransfer,
    WholeQueue,
}

public static class ConflictChoiceExtensions
{
    /// <summary>True for the "replace only if…" rules, which decide per file from size and time.</summary>
    public static bool IsRule(this ConflictChoice c) =>
        c is ConflictChoice.ReplaceIfNewer or ConflictChoice.ReplaceIfSizeDiffers or ConflictChoice.ReplaceIfNewerOrSizeDiffers;

    public static string Label(this ConflictChoice c) => c switch
    {
        ConflictChoice.Ask => "Ask me each time",
        ConflictChoice.Skip => "Skip",
        ConflictChoice.Replace => "Replace",
        ConflictChoice.KeepBoth => "Keep both",
        ConflictChoice.ReplaceIfNewer => "Replace if newer",
        ConflictChoice.ReplaceIfSizeDiffers => "Replace if the size differs",
        ConflictChoice.ReplaceIfNewerOrSizeDiffers => "Replace if newer or the size differs",
        _ => c.ToString(),
    };
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

namespace BridgeTransfer.Core.Transfers;

public sealed class TransferOptions
{
    /// <summary>Total attempts per file for transient (network) failures.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay before attempt 2, 3, ...</summary>
    public TimeSpan[] RetryDelays { get; set; } = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15) };

    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>Extra free space required on a local destination beyond the file size.</summary>
    public long LocalFreeSpaceMarginBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Modification-time tolerance when deciding whether a destination is newer.</summary>
    public TimeSpan TimeTolerance { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan IdleSessionLifetime { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan DelayFor(int failedAttempts)
    {
        if (RetryDelays.Length == 0)
        {
            return TimeSpan.Zero;
        }

        return RetryDelays[Math.Clamp(failedAttempts - 1, 0, RetryDelays.Length - 1)];
    }
}

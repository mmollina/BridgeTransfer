using BridgeTransfer.Core.Models;

namespace BridgeTransfer.Core.Persistence;

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>Persisted application settings. Contains no secrets.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppTheme Theme { get; set; } = AppTheme.Light;

    /// <summary>Number of files transferred at the same time (1 to 10). Each uses its own SFTP connection.</summary>
    public int MaxConcurrentTransfers { get; set; } = 2;

    public int ConnectTimeoutSeconds { get; set; } = 20;

    public int OperationTimeoutSeconds { get; set; } = 60;

    public int KeepAliveSeconds { get; set; } = 30;

    public bool ShowHiddenFiles { get; set; }

    public bool SidebarCollapsed { get; set; }

    public bool QueueCollapsed { get; set; }

    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>Show the destination paths and ask before starting each transfer.</summary>
    public bool ConfirmBeforeTransfer { get; set; } = true;

    /// <summary>What to do when a file already exists, preselected for each new transfer.</summary>
    public BridgeTransfer.Core.Transfers.ConflictChoice WhenFileExists { get; set; } = BridgeTransfer.Core.Transfers.ConflictChoice.Ask;

    public Guid? LastProfileId { get; set; }

    public string? LastLocalFolder { get; set; }

    public List<ConnectionProfile> Profiles { get; set; } = new();

    public List<FolderBookmark> Bookmarks { get; set; } = new();

    /// <summary>Clamps values loaded from disk into supported ranges.</summary>
    public AppSettings Normalize()
    {
        MaxConcurrentTransfers = Math.Clamp(MaxConcurrentTransfers, 1, 10);
        ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 5, 300);
        OperationTimeoutSeconds = Math.Clamp(OperationTimeoutSeconds, 10, 600);
        KeepAliveSeconds = Math.Clamp(KeepAliveSeconds, 0, 600);
        Profiles ??= new();
        Bookmarks ??= new();
        Profiles.RemoveAll(p => p is null);
        Bookmarks.RemoveAll(b => b is null);
        return this;
    }
}

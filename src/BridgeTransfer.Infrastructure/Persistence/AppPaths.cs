using BridgeTransfer.Core;

namespace BridgeTransfer.Infrastructure.Persistence;

/// <summary>Locations of persistent state: %LOCALAPPDATA%\BridgeTransfer by default.</summary>
public sealed class AppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory
            ?? Environment.GetEnvironmentVariable("BRIDGETRANSFER_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.ProductName);
    }

    public string DataDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string KnownHostsFile => Path.Combine(DataDirectory, "known_hosts.json");

    public string JournalFile => Path.Combine(DataDirectory, "queue.json");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}

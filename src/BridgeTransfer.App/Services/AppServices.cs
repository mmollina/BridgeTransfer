using BridgeTransfer.Core.Persistence;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Transfers;
using BridgeTransfer.Infrastructure.Local;
using BridgeTransfer.Infrastructure.Logging;
using BridgeTransfer.Infrastructure.Persistence;
using BridgeTransfer.Infrastructure.Sftp;

namespace BridgeTransfer.App.Services;

/// <summary>Composition root: owns settings, logging, host-key policy, the connection and the transfer queue.</summary>
public sealed class AppServices : IAsyncDisposable
{
    public AppServices()
    {
        Paths = new AppPaths();
        Paths.EnsureCreated();
        Log = new FileLog(Paths.LogDirectory);
        SettingsStore = new SettingsStore(Paths);
        var loaded = SettingsStore.Load();
        Settings = loaded.Settings;
        StartupWarning = loaded.Warning;
        Verifier = new HostKeyVerifier(new KnownHostsFileStore(Paths.KnownHostsFile));
        Local = new LocalFileStore();
        Connection = new ConnectionManager(this);
        SftpConnector.OnKeyExchangeFallback = msg => Log.Warn(msg);

        // Unfinished work from the previous run is moved aside for review before the new queue writes its journal.
        Interrupted = TakeInterruptedJournal();

        SessionFactory = new SftpTransferSessionFactory(key => Connection.TicketFor(key), Verifier, Local);
        Queue = new TransferQueue(SessionFactory, new JournalFileStore(Paths.JournalFile), BuildTransferOptions(), msg => Log.Warn(msg))
        {
            MaxConcurrency = Settings.MaxConcurrentTransfers,
        };
        Log.Info($"BridgeTransfer {BridgeTransfer.Core.AppInfo.Version} started. Data folder: {Paths.DataDirectory}");
    }

    public AppPaths Paths { get; }

    public FileLog Log { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public string? StartupWarning { get; }

    public HostKeyVerifier Verifier { get; }

    public LocalFileStore Local { get; }

    public ConnectionManager Connection { get; }

    public SftpTransferSessionFactory SessionFactory { get; }

    public TransferQueue Queue { get; }

    public JournalDocument? Interrupted { get; set; }

    public string InterruptedFile => System.IO.Path.Combine(Paths.DataDirectory, "interrupted.json");

    public TransferOptions BuildTransferOptions() => new() { PreserveTimestamps = Settings.PreserveTimestamps };

    public SftpConnectOptions ConnectOptions => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(Settings.ConnectTimeoutSeconds),
        OperationTimeout = TimeSpan.FromSeconds(Settings.OperationTimeoutSeconds),
        KeepAliveInterval = TimeSpan.FromSeconds(Settings.KeepAliveSeconds),
    };

    public void SaveSettings()
    {
        try
        {
            Settings.Normalize();
            SettingsStore.Save(Settings);
            Queue.MaxConcurrency = Settings.MaxConcurrentTransfers;
            Queue.Options.PreserveTimestamps = Settings.PreserveTimestamps;
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }

    private JournalDocument? TakeInterruptedJournal()
    {
        var previous = new JournalFileStore(Paths.JournalFile).Read();
        var pendingFile = System.IO.Path.Combine(Paths.DataDirectory, "interrupted.json");
        var pending = new JournalFileStore(pendingFile).Read() ?? new JournalDocument();
        if (previous is not null)
        {
            foreach (var job in previous.Jobs.Where(j => pending.Jobs.All(p => p.JobId != j.JobId)))
            {
                pending.Jobs.Add(job);
            }

            new JournalFileStore(pendingFile).Write(pending);
            try
            {
                System.IO.File.Delete(Paths.JournalFile);
            }
            catch (System.IO.IOException)
            {
            }
        }

        return pending.Jobs.Count > 0 ? pending : null;
    }

    public void SaveInterrupted()
    {
        var store = new JournalFileStore(InterruptedFile);
        if (Interrupted is null || Interrupted.Jobs.Count == 0)
        {
            try
            {
                System.IO.File.Delete(InterruptedFile);
            }
            catch (System.IO.IOException)
            {
            }

            Interrupted = null;
            return;
        }

        store.Write(Interrupted);
    }

    public async ValueTask DisposeAsync()
    {
        await Queue.DisposeAsync();
        Connection.Disconnect();
        Log.Info("BridgeTransfer closed.");
    }
}

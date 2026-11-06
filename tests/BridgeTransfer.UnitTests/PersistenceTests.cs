using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Persistence;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Transfers;
using BridgeTransfer.Infrastructure.Logging;
using BridgeTransfer.Infrastructure.Persistence;

namespace BridgeTransfer.UnitTests;

public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bt-unit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
        }
    }

    private AppPaths Paths()
    {
        var p = new AppPaths(_dir);
        p.EnsureCreated();
        return p;
    }

    [Fact]
    public void Settings_round_trip_without_secrets()
    {
        var paths = Paths();
        var store = new SettingsStore(paths);
        var s = store.Load().Settings;
        s.Theme = AppTheme.Dark;
        s.MaxConcurrentTransfers = 3;
        s.Profiles.Add(new ConnectionProfile { DisplayName = "Lab VM", Host = "10.0.0.5", Port = 2200, Username = "me", PrivateKeyPath = @"C:\Users\me\.ssh\id_ed25519" });
        s.Bookmarks.Add(new FolderBookmark { Name = "Results", ProfileId = s.Profiles[0].Id, LocalRoot = @"C:\data", RemoteRoot = "/srv/data" });
        store.Save(s);

        var loaded = new SettingsStore(paths).Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(AppTheme.Dark, loaded.Settings.Theme);
        Assert.Equal(3, loaded.Settings.MaxConcurrentTransfers);
        Assert.Equal(2200, loaded.Settings.Profiles[0].Port);
        Assert.Equal("/srv/data", loaded.Settings.Bookmarks[0].RemoteRoot);
        var json = File.ReadAllText(paths.SettingsFile);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupt_settings_restore_backup_and_keep_corrupt_copy()
    {
        var paths = Paths();
        var store = new SettingsStore(paths);
        var s = new AppSettings();
        s.Profiles.Add(new ConnectionProfile { DisplayName = "Keep me", Host = "h", Username = "u", PrivateKeyPath = "k" });
        store.Save(s);
        store.Save(s); // second save creates the .bak of a valid file
        File.WriteAllText(paths.SettingsFile, "{ this is not json");

        var loaded = new SettingsStore(paths).Load();
        Assert.NotNull(loaded.Warning);
        Assert.Equal("Keep me", loaded.Settings.Profiles.Single().DisplayName);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.unreadable-*"));
    }

    [Fact]
    public void Corrupt_settings_without_backup_fall_back_to_defaults_but_keep_file()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsFile, "garbage");
        var loaded = new SettingsStore(paths).Load();
        Assert.NotNull(loaded.Warning);
        Assert.Empty(loaded.Settings.Profiles);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.unreadable-*"));
    }

    [Fact]
    public void Newer_schema_is_not_overwritten()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsFile, "{\"schemaVersion\": 99, \"profiles\": []}");
        var store = new SettingsStore(paths);
        var loaded = store.Load();
        Assert.True(loaded.ReadOnly);
        store.Save(loaded.Settings);
        Assert.Contains("99", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var s = new AppSettings { MaxConcurrentTransfers = 12, ConnectTimeoutSeconds = 0 }.Normalize();
        Assert.Equal(4, s.MaxConcurrentTransfers);
        Assert.Equal(5, s.ConnectTimeoutSeconds);
    }

    [Fact]
    public void Known_hosts_persist()
    {
        var paths = Paths();
        var v = new HostKeyVerifier(new KnownHostsFileStore(paths.KnownHostsFile));
        v.TrustNew(new PresentedHostKey("vm", 22, "ssh-ed25519", "AAAA", "fp"));
        var v2 = new HostKeyVerifier(new KnownHostsFileStore(paths.KnownHostsFile));
        Assert.Equal(HostKeyStatus.Trusted, v2.Check(new PresentedHostKey("vm", 22, "ssh-ed25519", "AAAA", "fp")).Status);
    }

    [Fact]
    public void Journal_round_trip_for_interrupted_job_restoration()
    {
        var paths = Paths();
        var j = new JournalFileStore(paths.JournalFile);
        j.Write(new JournalDocument
        {
            Jobs =
            {
                new JournalJob
                {
                    JobId = Guid.NewGuid(), DisplayName = "project", Direction = TransferDirection.Upload, Host = "vm", Port = 22,
                    SourcePaths = { @"C:\data\project" }, DestinationDirectory = "/data", LastState = "Transferring",
                    StagingFiles = { new OwnedStagingFile { Side = EndpointSide.Remote, Path = "/data/project/.bt-staging-1-2-3.part" } },
                },
            },
        });
        var doc = new JournalFileStore(paths.JournalFile).Read()!;
        Assert.Equal("Transferring", doc.Jobs[0].LastState);
        Assert.Equal("/data/project/.bt-staging-1-2-3.part", doc.Jobs[0].StagingFiles[0].Path);
        File.WriteAllText(paths.JournalFile, "{broken");
        Assert.Null(new JournalFileStore(paths.JournalFile).Read());
    }

    [Fact]
    public void Log_redacts_registered_secrets_and_rotates()
    {
        var paths = Paths();
        var log = new FileLog(paths.LogDirectory, maxBytes: 2000, maxFiles: 3);
        using (SecretRedactor.Register("Sup3rSecret!"))
        {
            for (var i = 0; i < 200; i++)
            {
                log.Info($"attempt {i} with Sup3rSecret! and password=abc123");
            }
        }

        var files = Directory.GetFiles(paths.LogDirectory, "bridgetransfer*.log");
        Assert.InRange(files.Length, 2, 3);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("Sup3rSecret!", text);
            Assert.DoesNotContain("abc123", text);
        }
    }
}

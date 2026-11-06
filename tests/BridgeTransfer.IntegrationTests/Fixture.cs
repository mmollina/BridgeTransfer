using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Transfers;
using BridgeTransfer.Infrastructure.Local;
using BridgeTransfer.Infrastructure.Sftp;

namespace BridgeTransfer.IntegrationTests;

/// <summary>Reads tests/fixture/.run/fixture.env written by start-sftp-fixture.sh.</summary>
public static class Env
{
    private static readonly Lazy<Dictionary<string, string>?> Values = new(Load);

    private static Dictionary<string, string>? Load()
    {
        var path = Environment.GetEnvironmentVariable("BT_IT_FIXTURE");
        if (path is null)
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "BridgeTransfer.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            path = dir is null ? null : Path.Combine(dir, "tests", "fixture", ".run", "fixture.env");
        }

        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllLines(path).Where(l => l.Contains('=')).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
    }

    public static bool Available => Values.Value is not null;

    public static string Get(string key) => Values.Value![key];

    public static string Host => Get("BT_IT_HOST");

    public static int Port => int.Parse(Get("BT_IT_PORT"));

    public static int PortNoReplace => int.Parse(Get("BT_IT_PORT_NOREPLACE"));

    public static string User => Get("BT_IT_USER");

    public static string Root => Get("BT_IT_ROOT");

    public static bool LargeTests => Environment.GetEnvironmentVariable("BT_IT_LARGE") == "1";
}

/// <summary>A fact that is skipped (and reported as skipped) when the SFTP fixture is not running.</summary>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute()
    {
        if (!Env.Available)
        {
            Skip = "SFTP fixture not running (tests/fixture/start-sftp-fixture.sh).";
        }
    }
}

public sealed class LargeFixtureFactAttribute : FactAttribute
{
    public LargeFixtureFactAttribute()
    {
        if (!Env.Available)
        {
            Skip = "SFTP fixture not running.";
        }
        else if (!Env.LargeTests)
        {
            Skip = "Large-file test disabled; set BT_IT_LARGE=1 to run it (needs about 13 GB free disk).";
        }
    }
}

public sealed class MemoryKnownHosts : IKnownHostsStore
{
    private List<KnownHostRecord> _records = new();

    public IReadOnlyList<KnownHostRecord> GetAll() => _records.ToList();

    public void Save(IReadOnlyList<KnownHostRecord> records) => _records = records.ToList();
}

/// <summary>Per-test workspace: a fresh remote folder and a fresh local folder, plus helpers.</summary>
public sealed class Workspace : IAsyncDisposable
{
    public Workspace(int? port = null, AuthenticationKind auth = AuthenticationKind.PrivateKey)
    {
        Port = port ?? Env.Port;
        Id = Guid.NewGuid().ToString("N")[..8];
        RemoteDir = $"{Env.Root}/ws-{Id}";
        LocalDir = Path.Combine(Path.GetTempPath(), "bt-it", "ws " + Id + " ü");
        Directory.CreateDirectory(LocalDir);
        Directory.CreateDirectory(RemoteDir);
        Sh($"chown -R {Env.User}:{Env.User} '{RemoteDir}'");
        Verifier = new HostKeyVerifier(KnownHosts);
        Profile = new ConnectionProfile
        {
            DisplayName = "Fixture",
            Host = Env.Host,
            Port = Port,
            Username = Env.User,
            Authentication = auth,
            PrivateKeyPath = auth == AuthenticationKind.PrivateKey ? Env.Get("BT_IT_KEY") : null,
        };
        Credentials = new SessionCredentials(auth == AuthenticationKind.Password ? Env.Get("BT_IT_PASSWORD") : null, null);
        Key = new ConnectionKey(Profile.Id, Profile.Host, Profile.Port, Profile.Username);
    }

    public int Port { get; }

    public string Id { get; }

    /// <summary>Remote folder (same machine, so tests can also inspect it directly).</summary>
    public string RemoteDir { get; }

    public string LocalDir { get; }

    public MemoryKnownHosts KnownHosts { get; } = new();

    public HostKeyVerifier Verifier { get; }

    public ConnectionProfile Profile { get; set; }

    public SessionCredentials Credentials { get; set; }

    public ConnectionKey Key { get; set; }

    public SftpConnectOptions Options { get; set; } = new() { ConnectTimeout = TimeSpan.FromSeconds(10), OperationTimeout = TimeSpan.FromSeconds(30) };

    public LocalFileStore Local { get; } = new();

    private readonly List<IDisposable> _disposables = new();

    public async Task<SftpFileStore> ConnectAsync(HostKeyPrompt? prompt = null, ConnectionProfile? profile = null, SessionCredentials? creds = null)
    {
        prompt ??= (_, _) => Task.FromResult(true);
        var client = await SftpConnector.ConnectAsync(profile ?? Profile, creds ?? Credentials, Verifier, prompt, Options, CancellationToken.None);
        var store = new SftpFileStore(client);
        _disposables.Add(store);
        return store;
    }

    public TransferQueue NewQueue(TransferOptions? options = null)
    {
        var factory = new SftpTransferSessionFactory(k => k == Key ? new ConnectionTicket(Key, Profile, Credentials, Options) : null, Verifier, Local);
        return new TransferQueue(factory, new NullJournal(), options ?? new TransferOptions { RetryDelays = new[] { TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600) } }, s => Console.WriteLine(s));
    }

    public static void Sh(string command)
    {
        var p = Process.Start(new ProcessStartInfo("/bin/bash", new[] { "-c", command }) { RedirectStandardError = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{command}' failed: {p.StandardError.ReadToEnd()}");
        }
    }

    public static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    public static void WriteRandom(string path, long length, int seed = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rnd = new Random(seed);
        var buf = new byte[1024 * 1024];
        using var fs = File.Create(path);
        for (long left = length; left > 0;)
        {
            rnd.NextBytes(buf);
            var n = (int)Math.Min(buf.Length, left);
            fs.Write(buf, 0, n);
            left -= n;
        }
    }

    /// <summary>Relative path → SHA-256 for every file under a folder (and "dir" markers for folders).</summary>
    public static SortedDictionary<string, string> Manifest(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            result[Path.GetRelativePath(root, d).Replace('\\', '/')] = "dir";
        }

        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            result[Path.GetRelativePath(root, f).Replace('\\', '/')] = Sha256(f);
        }

        return result;
    }

    public static async Task<TransferState> WaitFinalAsync(TransferJob job, int timeoutMs = 120_000)
    {
        var sw = Stopwatch.StartNew();
        while (!job.State.IsFinal())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Job still {job.State}: " + string.Join("; ", job.Files.Select(f => $"{f.RelativePath}={f.State}:{f.Message}")));
            }

            await Task.Delay(50);
        }

        return job.State;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 60_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }

    public bool RemoteHasStagingFiles() =>
        Directory.EnumerateFiles(RemoteDir, "*", SearchOption.AllDirectories).Any(f => Path.GetFileName(f).StartsWith(BridgeTransfer.Core.AppInfo.StagingPrefix, StringComparison.Ordinal));

    public bool LocalHasStagingFiles() =>
        Directory.EnumerateFiles(LocalDir, "*", SearchOption.AllDirectories).Any(f => Path.GetFileName(f).StartsWith(BridgeTransfer.Core.AppInfo.StagingPrefix, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        await Task.Yield();
        try
        {
            Sh($"chmod -R u+rwx '{RemoteDir}' 2>/dev/null; rm -rf '{RemoteDir}'");
            Directory.Delete(LocalDir, true);
        }
        catch
        {
        }
    }
}

/// <summary>TCP forwarder that can sever all connections, used to simulate network interruptions.</summary>
public sealed class TcpProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _targetPort;
    private readonly List<TcpClient> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private long _bytes;

    public TcpProxy(int targetPort)
    {
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoop();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public long BytesForwarded => Interlocked.Read(ref _bytes);

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            var outbound = new TcpClient();
            await outbound.ConnectAsync(IPAddress.Loopback, _targetPort);
            lock (_clients)
            {
                _clients.Add(inbound);
                _clients.Add(outbound);
            }

            _ = Pump(inbound.GetStream(), outbound.GetStream());
            _ = Pump(outbound.GetStream(), inbound.GetStream());
        }
    }

    private async Task Pump(NetworkStream from, NetworkStream to)
    {
        var buf = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buf, _cts.Token)) > 0)
            {
                await to.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                Interlocked.Add(ref _bytes, n);
            }
        }
        catch
        {
        }
    }

    /// <summary>Abruptly closes every open connection (new connections are still accepted).</summary>
    public void SeverAll()
    {
        lock (_clients)
        {
            foreach (var c in _clients)
            {
                try
                {
                    c.Client.LingerState = new LingerOption(true, 0);
                    c.Close();
                }
                catch
                {
                }
            }

            _clients.Clear();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        SeverAll();
        _listener.Stop();
    }
}

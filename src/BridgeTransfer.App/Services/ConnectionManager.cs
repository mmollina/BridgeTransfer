using System.Windows.Threading;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Infrastructure.Sftp;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BridgeTransfer.App.Services;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Lost,
}

/// <summary>
/// The active server connection: one browsing connection plus the in-memory credentials that transfer
/// workers use to open their own connections. Credentials are discarded on disconnect.
/// </summary>
public sealed partial class ConnectionManager : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _health;
    private SessionCredentials? _credentials;

    public ConnectionManager(AppServices services)
    {
        _services = services;
        _health = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        _health.Tick += (_, _) => CheckHealth();
        _health.Start();
    }

    [ObservableProperty]
    private ConnectionStatus _status;

    [ObservableProperty]
    private ConnectionProfile? _profile;

    [ObservableProperty]
    private string _statusMessage = "Not connected";

    [ObservableProperty]
    private string? _serverIdentity;

    [ObservableProperty]
    private DateTime? _connectedSince;

    public SftpFileStore? Browse { get; private set; }

    public ConnectionKey? Key { get; private set; }

    public bool IsConnected => Status == ConnectionStatus.Connected && Browse is { IsConnected: true };

    public event Action? Changed;

    partial void OnStatusChanged(ConnectionStatus value)
    {
        OnPropertyChanged(nameof(IsConnected));
        Changed?.Invoke();
    }

    public ConnectionTicket? TicketFor(ConnectionKey key)
    {
        var profile = Profile;
        var creds = _credentials;
        if (profile is null || creds is null || Key != key || Status is ConnectionStatus.Disconnected or ConnectionStatus.Connecting)
        {
            return null;
        }

        return new ConnectionTicket(key, profile, creds, _services.ConnectOptions);
    }

    /// <summary>Connects the browsing session. Throws StoreException subclasses on failure.</summary>
    public async Task ConnectAsync(ConnectionProfile profile, SessionCredentials credentials, HostKeyPrompt prompt, CancellationToken ct)
    {
        Disconnect();
        Profile = profile;
        Status = ConnectionStatus.Connecting;
        StatusMessage = $"Connecting to {profile.Summary}…";
        _services.Log.Info($"Connecting to {profile.Host}:{profile.Port} as {profile.Username} ({profile.Authentication})");
        try
        {
            var client = await SftpConnector.ConnectAsync(profile, credentials, _services.Verifier, prompt, _services.ConnectOptions, ct);
            Browse = new SftpFileStore(client);
            _credentials = credentials;
            Key = new ConnectionKey(profile.Id, profile.Host, profile.Port, profile.Username);
            var alg = client.ConnectionInfo.CurrentHostKeyAlgorithm;
            var trusted = _services.Verifier.GetTrusted(profile.Host, profile.Port).FirstOrDefault(r => r.Algorithm == alg)
                          ?? _services.Verifier.GetTrusted(profile.Host, profile.Port).FirstOrDefault();
            ServerIdentity = trusted is null ? alg : $"{trusted.Algorithm} {trusted.DisplayFingerprint}";
            ConnectedSince = DateTime.Now;
            StatusMessage = $"Connected to {profile.Summary}";
            Status = ConnectionStatus.Connected;
            _services.Log.Info($"Connected to {profile.Host}:{profile.Port}");
        }
        catch
        {
            credentials.Dispose();
            Profile = null;
            Status = ConnectionStatus.Disconnected;
            StatusMessage = "Not connected";
            throw;
        }
    }

    /// <summary>Reconnects after a lost connection using the credentials kept for this session.</summary>
    public async Task ReconnectAsync(HostKeyPrompt prompt, CancellationToken ct)
    {
        var profile = Profile ?? throw new InvalidOperationException("No connection to restore.");
        var creds = _credentials ?? throw new InvalidOperationException("No credentials for this session.");
        var key = Key;
        Browse?.Dispose();
        Browse = null;
        Status = ConnectionStatus.Connecting;
        StatusMessage = $"Reconnecting to {profile.Summary}…";
        try
        {
            var client = await SftpConnector.ConnectAsync(profile, creds, _services.Verifier, prompt, _services.ConnectOptions, ct);
            Browse = new SftpFileStore(client);
            Key = key;
            Status = ConnectionStatus.Connected;
            StatusMessage = $"Connected to {profile.Summary}";
            ConnectedSince = DateTime.Now;
        }
        catch
        {
            Status = ConnectionStatus.Lost;
            StatusMessage = "Connection lost";
            throw;
        }
    }

    public bool HasCredentialsFor(ConnectionProfile profile) => _credentials is not null && Profile?.Id == profile.Id;

    public void MarkLost(string reason)
    {
        if (Status != ConnectionStatus.Connected)
        {
            return;
        }

        _services.Log.Warn("Connection lost: " + reason);
        Status = ConnectionStatus.Lost;
        StatusMessage = "Connection lost";
    }

    public void Disconnect()
    {
        if (Browse is not null)
        {
            _services.Log.Info("Disconnected.");
        }

        Browse?.Dispose();
        Browse = null;
        _credentials?.Dispose();
        _credentials = null;
        Key = null;
        Profile = null;
        ServerIdentity = null;
        ConnectedSince = null;
        StatusMessage = "Not connected";
        Status = ConnectionStatus.Disconnected;
    }

    private void CheckHealth()
    {
        if (Status == ConnectionStatus.Connected && Browse is { IsConnected: false })
        {
            MarkLost("keepalive detected the connection is closed");
        }
    }
}

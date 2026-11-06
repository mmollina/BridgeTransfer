namespace BridgeTransfer.Core.Security;

/// <summary>The host key a server presented during the SSH handshake.</summary>
public sealed record PresentedHostKey(string Host, int Port, string Algorithm, string PublicKeyBase64, string FingerprintSha256)
{
    /// <summary>Fingerprint in the same form as OpenSSH: "SHA256:xxxx".</summary>
    public string DisplayFingerprint => "SHA256:" + FingerprintSha256;
}

/// <summary>A host key the user has explicitly trusted.</summary>
public sealed class KnownHostRecord
{
    public string Host { get; set; } = "";

    public int Port { get; set; }

    public string Algorithm { get; set; } = "";

    public string PublicKeyBase64 { get; set; } = "";

    public string FingerprintSha256 { get; set; } = "";

    public DateTime TrustedUtc { get; set; }

    public string DisplayFingerprint => "SHA256:" + FingerprintSha256;
}

public enum HostKeyStatus
{
    /// <summary>The key exactly matches a trusted record for this host and port.</summary>
    Trusted,

    /// <summary>No key is trusted for this host and port yet.</summary>
    Unknown,

    /// <summary>A different key is trusted for this host and port. Connection must be blocked.</summary>
    Changed,
}

public sealed record HostKeyCheck(HostKeyStatus Status, PresentedHostKey Presented, IReadOnlyList<KnownHostRecord> TrustedKeys);

/// <summary>Storage for trusted host keys.</summary>
public interface IKnownHostsStore
{
    IReadOnlyList<KnownHostRecord> GetAll();

    void Save(IReadOnlyList<KnownHostRecord> records);
}

/// <summary>
/// Host-key policy shared by browsing and transfer connections. Thread-safe.
/// A key is accepted automatically only if it matches a trusted record exactly.
/// </summary>
public sealed class HostKeyVerifier
{
    private readonly IKnownHostsStore _store;
    private readonly object _gate = new();

    public HostKeyVerifier(IKnownHostsStore store) => _store = store;

    public static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    public HostKeyCheck Check(PresentedHostKey presented)
    {
        lock (_gate)
        {
            var host = NormalizeHost(presented.Host);
            var records = _store.GetAll().Where(r => r.Host == host && r.Port == presented.Port).ToList();
            if (records.Count == 0)
            {
                return new HostKeyCheck(HostKeyStatus.Unknown, presented, records);
            }

            var match = records.Any(r =>
                string.Equals(r.Algorithm, presented.Algorithm, StringComparison.Ordinal) &&
                string.Equals(r.PublicKeyBase64, presented.PublicKeyBase64, StringComparison.Ordinal));
            return new HostKeyCheck(match ? HostKeyStatus.Trusted : HostKeyStatus.Changed, presented, records);
        }
    }

    /// <summary>Trusts a key for a host that currently has no trusted key. Refuses to overwrite a different trusted key.</summary>
    public void TrustNew(PresentedHostKey presented)
    {
        lock (_gate)
        {
            var check = Check(presented);
            if (check.Status == HostKeyStatus.Changed)
            {
                throw new InvalidOperationException("A different key is already trusted for this server. Remove it in connection settings first.");
            }

            if (check.Status == HostKeyStatus.Trusted)
            {
                return;
            }

            var all = _store.GetAll().ToList();
            all.Add(ToRecord(presented));
            _store.Save(all);
        }
    }

    /// <summary>Removes every trusted key for host:port. This is the explicit "forget server identity" action.</summary>
    public int Forget(string host, int port)
    {
        lock (_gate)
        {
            var h = NormalizeHost(host);
            var all = _store.GetAll().ToList();
            var removed = all.RemoveAll(r => r.Host == h && r.Port == port);
            if (removed > 0)
            {
                _store.Save(all);
            }

            return removed;
        }
    }

    public IReadOnlyList<KnownHostRecord> GetTrusted(string host, int port)
    {
        lock (_gate)
        {
            var h = NormalizeHost(host);
            return _store.GetAll().Where(r => r.Host == h && r.Port == port).ToList();
        }
    }

    private static KnownHostRecord ToRecord(PresentedHostKey p) => new()
    {
        Host = NormalizeHost(p.Host),
        Port = p.Port,
        Algorithm = p.Algorithm,
        PublicKeyBase64 = p.PublicKeyBase64,
        FingerprintSha256 = p.FingerprintSha256,
        TrustedUtc = DateTime.UtcNow,
    };
}

/// <summary>Thrown when a server presents a key different from the trusted one.</summary>
public sealed class HostKeyMismatchException : Exception
{
    public HostKeyMismatchException(HostKeyCheck check)
        : base($"The server {check.Presented.Host}:{check.Presented.Port} presented a different host key than the one you trusted.")
    {
        Check = check;
    }

    public HostKeyCheck Check { get; }
}

/// <summary>Thrown when an unknown host key was not accepted (user cancelled or no prompt available).</summary>
public sealed class HostKeyNotTrustedException : Exception
{
    public HostKeyNotTrustedException(PresentedHostKey presented)
        : base($"The identity of {presented.Host}:{presented.Port} was not confirmed.")
    {
        Presented = presented;
    }

    public PresentedHostKey Presented { get; }
}

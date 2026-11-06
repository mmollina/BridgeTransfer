using System.Net.Sockets;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Storage;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace BridgeTransfer.Infrastructure.Sftp;

/// <summary>Secrets for one connected session. Kept in memory only; never persisted or logged.</summary>
public sealed class SessionCredentials : IDisposable
{
    private readonly IDisposable _passwordRegistration;
    private readonly IDisposable _passphraseRegistration;

    public SessionCredentials(string? password, string? passphrase)
    {
        Password = password;
        Passphrase = passphrase;
        _passwordRegistration = SecretRedactor.Register(password);
        _passphraseRegistration = SecretRedactor.Register(passphrase);
    }

    public string? Password { get; private set; }

    public string? Passphrase { get; private set; }

    public void Dispose()
    {
        Password = null;
        Passphrase = null;
        _passwordRegistration.Dispose();
        _passphraseRegistration.Dispose();
    }
}

public sealed class SftpConnectOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Asks the user whether to trust an unknown host key. Must not block the UI thread.</summary>
public delegate Task<bool> HostKeyPrompt(PresentedHostKey key, CancellationToken cancellationToken);

public enum KeyFileProblem
{
    NotFound,
    PassphraseRequired,
    WrongPassphraseOrCorrupt,
    UnsupportedFormat,
}

/// <summary>A problem with the private key file. The key file is never modified or converted.</summary>
public sealed class KeyFileException : StoreException
{
    public KeyFileException(KeyFileProblem problem, string message, Exception? inner = null)
        : base(FailureKind.Authentication, message, inner)
    {
        Problem = problem;
    }

    public KeyFileProblem Problem { get; }
}

/// <summary>The server presented a key that differs from the trusted one. Connection was blocked.</summary>
public sealed class HostKeyChangedException : StoreException
{
    public HostKeyChangedException(HostKeyCheck check)
        : base(FailureKind.HostKeyRejected, $"The identity of {check.Presented.Host}:{check.Presented.Port} has changed. The connection was blocked.")
    {
        Check = check;
    }

    public HostKeyCheck Check { get; }
}

public sealed class HostKeyRejectedException : StoreException
{
    public HostKeyRejectedException(PresentedHostKey key)
        : base(FailureKind.HostKeyRejected, $"The identity of {key.Host}:{key.Port} was not confirmed, so no login details were sent.")
    {
        Key = key;
    }

    public PresentedHostKey Key { get; }
}

/// <summary>
/// Opens SFTP connections. The host key is verified in <see cref="BaseClient.HostKeyReceived"/>, which
/// SSH.NET raises during key exchange, before any authentication data is sent.
/// </summary>
public static class SftpConnector
{
    public const string SupportedKeyFormats =
        "OpenSSH keys (\"BEGIN OPENSSH PRIVATE KEY\", as made by ssh-keygen), PuTTY .ppk keys (versions 2 and 3) and PEM/PKCS#8 keys, " +
        "of type Ed25519, ECDSA (256/384/521) or RSA, with or without a passphrase.";

    /// <summary>Loads the private key to check it, without modifying the file.</summary>
    public static PrivateKeyFile LoadKey(string path, string? passphrase)
    {
        if (!File.Exists(path))
        {
            throw new KeyFileException(KeyFileProblem.NotFound, $"The private key file was not found: {path}");
        }

        try
        {
            return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new KeyFileException(KeyFileProblem.PassphraseRequired, "This private key is protected by a passphrase. Enter the passphrase to use it.", ex);
        }
        catch (Exception ex) when (LooksLikeWrongPassphrase(ex, passphrase))
        {
            throw new KeyFileException(KeyFileProblem.WrongPassphraseOrCorrupt, "The passphrase is not correct for this private key (or the key file is damaged).", ex);
        }
        catch (Exception ex) when (ex is SshException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            throw new KeyFileException(
                KeyFileProblem.UnsupportedFormat,
                "This private key could not be read. Supported: " + SupportedKeyFormats +
                " DSA keys and FIDO/security-key (sk-) keys are not supported. The key file was not changed.",
                ex);
        }
    }

    private static bool LooksLikeWrongPassphrase(Exception ex, string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            return false;
        }

        var text = ex.ToString();
        return text.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("padding", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("checkint", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("decrypt", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("mac check", StringComparison.OrdinalIgnoreCase) ||
               ex is System.Security.Cryptography.CryptographicException;
    }

    /// <summary>
    /// Connects and authenticates. Unknown host keys are accepted only if <paramref name="prompt"/>
    /// returns true; changed keys always block. Call from a background thread.
    /// </summary>
    public static async Task<SftpClient> ConnectAsync(
        ConnectionProfile profile,
        SessionCredentials credentials,
        HostKeyVerifier verifier,
        HostKeyPrompt? prompt,
        SftpConnectOptions options,
        CancellationToken cancellationToken)
    {
        var problems = profile.Validate();
        if (problems.Count > 0)
        {
            throw new StoreException(FailureKind.InvalidName, problems[0]);
        }

        var methods = new List<AuthenticationMethod>();
        PrivateKeyFile? key = null;
        if (profile.Authentication == AuthenticationKind.PrivateKey)
        {
            key = LoadKey(profile.PrivateKeyPath!, credentials.Passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(profile.Username, key));
        }
        else
        {
            var password = credentials.Password ?? "";
            methods.Add(new PasswordAuthenticationMethod(profile.Username, password));
            var kbd = new KeyboardInteractiveAuthenticationMethod(profile.Username);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts)
                {
                    if (p.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Response = password;
                    }
                }
            };
            methods.Add(kbd);
        }

        try
        {
            return await ConnectOnceAsync(profile, methods, verifier, prompt, options, avoidX25519: false, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException ex) when (IsLocalCryptoGap(ex))
        {
            // The local crypto provider cannot do elliptic-curve key exchange (seen on some locked-down or
            // emulated Windows environments). Retry once with finite-field Diffie-Hellman; host-key
            // verification is unchanged.
            OnKeyExchangeFallback?.Invoke("Elliptic-curve key exchange is not available from this system's crypto provider; using Diffie-Hellman key exchange (2048 bits or more) instead.");
            try
            {
                return await ConnectOnceAsync(profile, methods, verifier, prompt, options, avoidX25519: true, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                key?.Dispose();
                throw;
            }
        }
        catch
        {
            key?.Dispose();
            throw;
        }
    }

    /// <summary>Raised (for logging) when key exchange falls back from Curve25519.</summary>
    public static Action<string>? OnKeyExchangeFallback { get; set; }

    private static bool IsLocalCryptoGap(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is PlatformNotSupportedException ||
                (e is System.Security.Cryptography.CryptographicException && e.StackTrace?.Contains("KeyExchange", StringComparison.Ordinal) == true))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<SftpClient> ConnectOnceAsync(
        ConnectionProfile profile,
        List<AuthenticationMethod> methods,
        HostKeyVerifier verifier,
        HostKeyPrompt? prompt,
        SftpConnectOptions options,
        bool avoidX25519,
        CancellationToken cancellationToken)
    {
        var info = new ConnectionInfo(profile.Host, profile.Port, profile.Username, methods.ToArray())
        {
            Timeout = options.ConnectTimeout,
        };
        // Files are sent exactly as they are: no transport compression.
        foreach (var name in info.CompressionAlgorithms.Keys.Where(k => k != "none").ToList())
        {
            info.CompressionAlgorithms.Remove(name);
        }

        // Outdated SHA-1 key exchanges are not offered.
        foreach (var name in info.KeyExchangeAlgorithms.Keys.Where(k => k.EndsWith("-sha1", StringComparison.Ordinal)).ToList())
        {
            info.KeyExchangeAlgorithms.Remove(name);
        }

        if (avoidX25519)
        {
            // Keep only finite-field Diffie-Hellman exchanges (2048 bits or more), which SSH.NET computes itself.
            foreach (var name in info.KeyExchangeAlgorithms.Keys.Where(k => !k.StartsWith("diffie-hellman-group-exchange-sha256", StringComparison.Ordinal) &&
                                                                              !k.StartsWith("diffie-hellman-group16", StringComparison.Ordinal) &&
                                                                              !k.StartsWith("diffie-hellman-group14-sha256", StringComparison.Ordinal)).ToList())
            {
                info.KeyExchangeAlgorithms.Remove(name);
            }
        }

        var client = new SftpClient(info)
        {
            OperationTimeout = options.OperationTimeout,
            KeepAliveInterval = options.KeepAliveInterval <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : options.KeepAliveInterval,
        };

        HostKeyCheck? mismatch = null;
        PresentedHostKey? rejected = null;
        client.HostKeyReceived += (_, e) =>
        {
            var presented = new PresentedHostKey(profile.Host, profile.Port, e.HostKeyName, Convert.ToBase64String(e.HostKey), e.FingerPrintSHA256);
            var check = verifier.Check(presented);
            switch (check.Status)
            {
                case HostKeyStatus.Trusted:
                    e.CanTrust = true;
                    return;
                case HostKeyStatus.Changed:
                    mismatch = check;
                    e.CanTrust = false;
                    return;
                default:
                    var accepted = false;
                    if (prompt is not null && !cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            // Raised on an SSH.NET thread; the prompt marshals to the UI without blocking it.
                            accepted = prompt(presented, cancellationToken).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            accepted = false;
                        }
                    }

                    if (accepted)
                    {
                        verifier.TrustNew(presented);
                    }
                    else
                    {
                        rejected = presented;
                    }

                    e.CanTrust = accepted;
                    return;
            }
        };

        try
        {
            await Task.Run(() => client.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (mismatch is not null)
            {
                throw new HostKeyChangedException(mismatch);
            }

            if (rejected is not null)
            {
                throw new HostKeyRejectedException(rejected);
            }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw new StoreException(FailureKind.Cancelled, "Connecting was cancelled.", ex);
            }

            throw ex switch
            {
                SshAuthenticationException => new StoreException(FailureKind.Authentication,
                    profile.Authentication == AuthenticationKind.Password
                        ? "The VM rejected the user name or password."
                        : "The VM rejected the key. Check the user name and that the public key is in ~/.ssh/authorized_keys on the VM.", ex),
                SocketException se when se.SocketErrorCode == SocketError.HostNotFound => new StoreException(FailureKind.Transient, $"The host name '{profile.Host}' could not be found.", ex),
                SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused => new StoreException(FailureKind.Transient, $"{profile.Host} refused the connection on port {profile.Port}. Is the SSH server running?", ex),
                SocketException or SshOperationTimeoutException or TimeoutException => new StoreException(FailureKind.Transient, $"Could not reach {profile.Host}:{profile.Port}. Check that the VM is running and reachable.", ex),
                SshConnectionException => new StoreException(FailureKind.Transient, "The VM closed the connection while connecting.", ex),
                _ => SftpErrors.Translate(ex),
            };
        }
    }
}

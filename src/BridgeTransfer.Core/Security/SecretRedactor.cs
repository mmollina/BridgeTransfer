using System.Text.RegularExpressions;

namespace BridgeTransfer.Core.Security;

/// <summary>
/// Removes secrets from text before it is logged or shown as technical details.
/// Secrets in use (passwords, passphrases) are registered for the lifetime of a session.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "[redacted]";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Secrets = new(StringComparer.Ordinal);

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?(-----END [A-Z0-9 ]*PRIVATE KEY-----|$)", RegexOptions.Singleline)]
    private static partial Regex PemBlock();

    [GeneratedRegex(@"(?i)\b(password|passphrase|passwd|pwd)\b(\s*[=:]\s*)(""[^""]*""|'[^']*'|\S+)")]
    private static partial Regex KeyValueSecret();

    /// <summary>Registers a secret. Returns a token that unregisters it when disposed.</summary>
    public static IDisposable Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 2)
        {
            return new Registration(null);
        }

        lock (Gate)
        {
            Secrets[secret] = Secrets.TryGetValue(secret, out var n) ? n + 1 : 1;
        }

        return new Registration(secret);
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        string[] current;
        lock (Gate)
        {
            current = Secrets.Keys.OrderByDescending(s => s.Length).ToArray();
        }

        var result = text;
        foreach (var s in current)
        {
            result = result.Replace(s, Mask, StringComparison.Ordinal);
        }

        result = PemBlock().Replace(result, "[private key " + Mask + "]");
        result = KeyValueSecret().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        return result;
    }

    private sealed class Registration : IDisposable
    {
        private string? _secret;

        public Registration(string? secret) => _secret = secret;

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _secret, null);
            if (s is null)
            {
                return;
            }

            lock (Gate)
            {
                if (Secrets.TryGetValue(s, out var n))
                {
                    if (n <= 1)
                    {
                        Secrets.Remove(s);
                    }
                    else
                    {
                        Secrets[s] = n - 1;
                    }
                }
            }
        }
    }
}

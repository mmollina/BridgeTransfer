namespace BridgeTransfer.Core.Models;

/// <summary>A saved connection. Never contains passwords or passphrases.</summary>
public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string DisplayName { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; } = 22;

    public string Username { get; set; } = "";

    public AuthenticationKind Authentication { get; set; } = AuthenticationKind.PrivateKey;

    /// <summary>Path to the private key file. The key itself is never copied.</summary>
    public string? PrivateKeyPath { get; set; }

    public string? DefaultLocalFolder { get; set; }

    public string? DefaultRemoteFolder { get; set; }

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();

    public string Summary => $"{Username}@{Host}" + (Port == 22 ? "" : $":{Port}");

    /// <summary>Returns a list of problems; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            problems.Add("Enter a name for this connection.");
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            problems.Add("Enter the host name or IP address of the VM.");
        }
        else if (Host.Any(char.IsWhiteSpace) || Host.Contains('/') || Host.Contains('@'))
        {
            problems.Add("The host name must not contain spaces, '/' or '@'. Put the user name in the User name field.");
        }

        if (Port is < 1 or > 65535)
        {
            problems.Add("The port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            problems.Add("Enter the user name on the VM.");
        }

        if (Authentication == AuthenticationKind.PrivateKey && string.IsNullOrWhiteSpace(PrivateKeyPath))
        {
            problems.Add("Choose a private key file, or switch to password authentication.");
        }

        if (!string.IsNullOrWhiteSpace(DefaultRemoteFolder) && !DefaultRemoteFolder.StartsWith('/') && !DefaultRemoteFolder.StartsWith('~'))
        {
            problems.Add("The default remote folder must be an absolute Linux path such as /home/user/data.");
        }

        return problems;
    }
}

/// <summary>A pair of folders on the two machines.</summary>
public sealed class FolderBookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public Guid ProfileId { get; set; }

    public string LocalRoot { get; set; } = "";

    public string RemoteRoot { get; set; } = "";
}

/// <summary>Identity of a connected server, used to bind queued work to one server.</summary>
public sealed record ConnectionKey(Guid ProfileId, string Host, int Port, string Username)
{
    public override string ToString() => $"{Username}@{Host}:{Port}";
}

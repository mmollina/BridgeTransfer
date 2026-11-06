namespace BridgeTransfer.Core.Models;

public enum EndpointSide
{
    Local,
    Remote,
}

public enum TransferDirection
{
    /// <summary>This computer to the Linux VM.</summary>
    Upload,

    /// <summary>The Linux VM to this computer.</summary>
    Download,
}

public enum EntryKind
{
    File,
    Directory,
    SymbolicLink,
    Other,
}

public enum AuthenticationKind
{
    PrivateKey,
    Password,
}

public static class DirectionExtensions
{
    public static EndpointSide SourceSide(this TransferDirection d) => d == TransferDirection.Upload ? EndpointSide.Local : EndpointSide.Remote;

    public static EndpointSide DestinationSide(this TransferDirection d) => d == TransferDirection.Upload ? EndpointSide.Remote : EndpointSide.Local;

    public static string Label(this TransferDirection d) => d == TransferDirection.Upload ? "Upload" : "Download";

    public static string Arrow(this TransferDirection d) => d == TransferDirection.Upload ? "This computer → Linux VM" : "Linux VM → This computer";
}

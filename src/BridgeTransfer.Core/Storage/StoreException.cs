namespace BridgeTransfer.Core.Storage;

/// <summary>Category of a failure, used for retry decisions and plain-language messages.</summary>
public enum FailureKind
{
    Unknown,

    /// <summary>Network interruption or timeout; may be retried.</summary>
    Transient,
    Authentication,
    HostKeyRejected,
    PermissionDenied,
    NotFound,
    AlreadyExists,
    DiskFull,
    ReplaceNotSupported,
    SourceChanged,
    DestinationChanged,
    InvalidName,
    TypeConflict,
    LinkRedirect,
    VerificationFailed,
    Cancelled,
}

/// <summary>Exception thrown by file stores, carrying a failure category and a plain-language message.</summary>
public class StoreException : Exception
{
    public StoreException(FailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public FailureKind Kind { get; }

    public bool IsTransient => Kind == FailureKind.Transient;

    /// <summary>Technical details for the "Show details" section (inner exception chain, no secrets).</summary>
    public string TechnicalDetails
    {
        get
        {
            var parts = new List<string>();
            for (Exception? e = InnerException; e is not null; e = e.InnerException)
            {
                parts.Add($"{e.GetType().Name}: {e.Message}");
            }

            return parts.Count == 0 ? $"{Kind}" : $"{Kind}; " + string.Join(" -> ", parts);
        }
    }
}

public static class FailureText
{
    /// <summary>Whether a failure of this kind should be retried automatically.</summary>
    public static bool IsRetryable(FailureKind kind) => kind == FailureKind.Transient;

    public static string Describe(FailureKind kind) => kind switch
    {
        FailureKind.Transient => "The connection was interrupted or timed out.",
        FailureKind.Authentication => "The server rejected the user name, password or key.",
        FailureKind.HostKeyRejected => "The server's identity could not be verified.",
        FailureKind.PermissionDenied => "Permission was denied.",
        FailureKind.NotFound => "The file or folder no longer exists.",
        FailureKind.AlreadyExists => "An item with this name already exists.",
        FailureKind.DiskFull => "There is not enough free space.",
        FailureKind.ReplaceNotSupported => "The server cannot replace the existing file safely, so it was left unchanged.",
        FailureKind.SourceChanged => "The source file changed while it was being copied.",
        FailureKind.DestinationChanged => "The destination changed after it was checked.",
        FailureKind.InvalidName => "The name cannot be used on the destination.",
        FailureKind.TypeConflict => "A file and a folder have the same name.",
        FailureKind.LinkRedirect => "The destination is a link, so writing was refused.",
        FailureKind.VerificationFailed => "The copied size did not match the source.",
        FailureKind.Cancelled => "Cancelled.",
        _ => "An unexpected error occurred.",
    };
}

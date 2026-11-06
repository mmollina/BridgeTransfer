using BridgeTransfer.Core.Models;

namespace BridgeTransfer.Core.Storage;

/// <summary>
/// One side of a transfer (this computer or the Linux VM). Implementations translate library
/// exceptions into <see cref="StoreException"/>. Instances bound to an SFTP session are not
/// thread-safe; each transfer worker owns its own instance.
/// </summary>
public interface IFileStore
{
    EndpointSide Side { get; }

    /// <summary>Joins a directory and a validated child name using this side's rules.</summary>
    string Combine(string directory, string name);

    /// <summary>Joins a root and a '/'-separated relative path with containment checks.</summary>
    string CombineRelative(string root, string relativePath);

    string? GetParent(string path);

    string GetName(string path);

    /// <summary>Key identifying a destination path for write-locking (case-insensitive locally).</summary>
    string LockKey(string path);

    /// <summary>Metadata of the entry itself (links are not followed), or null if it does not exist.</summary>
    Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Lists a directory, excluding '.' and '..'. Throws for unreadable directories.</summary>
    Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken cancellationToken);

    /// <summary>Creates a single directory. Throws <see cref="FailureKind.AlreadyExists"/> if anything exists there.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Opens a file for sequential reading.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Creates a new file exclusively (fails if it exists) and returns a write stream.</summary>
    Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken);

    /// <summary>Creates a new file exclusively and fills it from <paramref name="content"/>. Reports acknowledged bytes.</summary>
    Task WriteNewFileAsync(string path, Stream content, Action<long> progress, CancellationToken cancellationToken);

    /// <summary>Copies the whole file into <paramref name="output"/>. Reports bytes read.</summary>
    Task ReadFileAsync(string path, Stream output, Action<long> progress, CancellationToken cancellationToken);

    /// <summary>Renames without replacing. Throws <see cref="FailureKind.AlreadyExists"/> if the target exists.</summary>
    Task MoveNoReplaceAsync(string from, string to, CancellationToken cancellationToken);

    /// <summary>
    /// Renames over an existing file in one step, so the old file stays in place until the new one
    /// replaces it. Throws <see cref="FailureKind.ReplaceNotSupported"/> when that cannot be done safely.
    /// </summary>
    Task MoveReplaceAsync(string from, string to, CancellationToken cancellationToken);

    /// <summary>Deletes a file. Used only for this application's own staging files.</summary>
    Task DeleteOwnedFileAsync(string path, CancellationToken cancellationToken);

    Task SetModifiedTimeAsync(string path, DateTime modifiedUtc, CancellationToken cancellationToken);

    /// <summary>Free bytes available at the path, or null if unknown.</summary>
    Task<long?> GetAvailableSpaceAsync(string path, CancellationToken cancellationToken);
}

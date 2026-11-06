namespace BridgeTransfer.Core.Models;

/// <summary>
/// Metadata for one directory entry. Link entries describe the link itself (lstat semantics),
/// never the target.
/// </summary>
public sealed record FileEntry(
    string Name,
    string FullPath,
    EntryKind Kind,
    long Length,
    DateTime? ModifiedUtc,
    bool IsHidden = false,
    string? LinkTarget = null)
{
    public bool IsDirectory => Kind == EntryKind.Directory;

    public bool IsFile => Kind == EntryKind.File;

    public bool IsLink => Kind == EntryKind.SymbolicLink;
}

/// <summary>Size and time of an entry at a point in time; used to detect changes.</summary>
public sealed record EntrySnapshot(EntryKind Kind, long Length, DateTime? ModifiedUtc)
{
    public static EntrySnapshot From(FileEntry e) => new(e.Kind, e.Length, e.ModifiedUtc);

    /// <summary>True when kind, length and modification time (to the second) are unchanged.</summary>
    public bool SameAs(EntrySnapshot? other)
    {
        if (other is null || other.Kind != Kind || other.Length != Length)
        {
            return false;
        }

        if (ModifiedUtc is null || other.ModifiedUtc is null)
        {
            return ModifiedUtc is null && other.ModifiedUtc is null;
        }

        return Math.Abs((ModifiedUtc.Value - other.ModifiedUtc.Value).TotalSeconds) < 1.0;
    }
}

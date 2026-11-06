using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;

namespace BridgeTransfer.Core.Transfers;

/// <summary>
/// Listings of destination folders taken once per job, so that existing files can be found and
/// skip/ask decisions made without one network round trip per file. Thread-safe.
/// A listing can be stale; every file that is actually written is still checked against the live
/// destination (exclusive create and no-replace rename), so staleness can only cost time, not data.
/// </summary>
public sealed class DestinationIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, FileEntry>> _folders;
    private readonly StringComparer _names;

    public DestinationIndex(bool caseInsensitive)
    {
        _names = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _folders = new Dictionary<string, Dictionary<string, FileEntry>>(_names);
    }

    private static string Key(IFileStore store, string folder) => store.GetParent(store.Combine(folder, "_")) ?? folder;

    public void SetListing(IFileStore store, string folder, IEnumerable<FileEntry> entries)
    {
        var map = new Dictionary<string, FileEntry>(_names);
        foreach (var e in entries)
        {
            map.TryAdd(e.Name, e);
        }

        lock (_gate)
        {
            _folders[Key(store, folder)] = map;
        }
    }

    public void SetEmpty(IFileStore store, string folder) => SetListing(store, folder, Array.Empty<FileEntry>());

    /// <summary>
    /// True when the parent folder of <paramref name="path"/> has been listed; <paramref name="entry"/>
    /// is then the listed item, or null if the folder did not contain it.
    /// </summary>
    public bool TryLookup(IFileStore store, string path, out FileEntry? entry)
    {
        entry = null;
        var parent = store.GetParent(path);
        if (parent is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_folders.TryGetValue(Key(store, parent), out var map))
            {
                return false;
            }

            entry = map.GetValueOrDefault(store.GetName(path));
            return true;
        }
    }
}

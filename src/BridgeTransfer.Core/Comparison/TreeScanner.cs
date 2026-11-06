using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Paths;
using BridgeTransfer.Core.Storage;

namespace BridgeTransfer.Core.Comparison;

public sealed record ScannedItem(string RelativePath, string Name, string FullPath, EntryKind Kind, long Length, DateTime? ModifiedUtc)
{
    public EntrySnapshot Snapshot => new(Kind, Length, ModifiedUtc);
}

/// <summary>Recursive listing of one folder tree. Unreadable folders are recorded, never treated as empty.</summary>
public sealed class ScanTree
{
    public ScanTree(string root) => Root = root;

    public string Root { get; }

    public Dictionary<string, ScannedItem> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>Relative paths of folders that could not be listed, with the reason.</summary>
    public Dictionary<string, string> Unreadable { get; } = new(StringComparer.Ordinal);

    /// <summary>Names returned by the server that are not legitimate child names ('.', '..', containing '/').</summary>
    public List<string> RejectedNames { get; } = new();

    public bool IsComplete => Unreadable.Count == 0;
}

public sealed record ScanProgress(int Folders, int Items, string CurrentFolder);

public static class TreeScanner
{
    /// <summary>
    /// Scans <paramref name="root"/> recursively. Links are recorded but never followed.
    /// Only reads metadata; never modifies anything.
    /// </summary>
    public static async Task<ScanTree> ScanAsync(IFileStore store, string root, Action<ScanProgress>? progress, CancellationToken ct)
    {
        var tree = new ScanTree(root);
        var rootEntry = await store.GetEntryAsync(root, ct).ConfigureAwait(false)
            ?? throw new StoreException(FailureKind.NotFound, $"The folder {root} does not exist.");
        if (!rootEntry.IsDirectory && !rootEntry.IsLink)
        {
            throw new StoreException(FailureKind.TypeConflict, $"{root} is not a folder.");
        }

        var stack = new Stack<(string Path, string Rel)>();
        stack.Push((root, ""));
        var folders = 0;
        var lastReport = DateTime.MinValue;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, rel) = stack.Pop();
            IReadOnlyList<FileEntry> children;
            try
            {
                children = await store.ListAsync(path, ct).ConfigureAwait(false);
            }
            catch (StoreException ex) when (ex.Kind is not (FailureKind.Transient or FailureKind.Cancelled))
            {
                if (rel.Length == 0)
                {
                    throw;
                }

                tree.Unreadable[rel] = ex.Message;
                continue;
            }

            folders++;
            foreach (var child in children)
            {
                if (RemotePath.ValidateChildName(child.Name) is not null)
                {
                    tree.RejectedNames.Add(rel.Length == 0 ? child.Name : rel + "/" + child.Name);
                    continue;
                }

                var childRel = RelativePath.Join(rel, child.Name);
                tree.Items[childRel] = new ScannedItem(childRel, child.Name, child.FullPath, child.Kind, child.Kind == EntryKind.File ? child.Length : 0, child.ModifiedUtc);
                if (child.Kind == EntryKind.Directory)
                {
                    stack.Push((child.FullPath, childRel));
                }
            }

            if (progress is not null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150))
            {
                lastReport = DateTime.UtcNow;
                progress(new ScanProgress(folders, tree.Items.Count, path));
            }
        }

        progress?.Invoke(new ScanProgress(folders, tree.Items.Count, root));
        return tree;
    }
}

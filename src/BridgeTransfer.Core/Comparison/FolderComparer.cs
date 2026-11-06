using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Paths;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.Core.Comparison;

public enum ComparisonCategory
{
    OnlyOnSource,
    OnlyOnDestination,
    SourceNewer,
    DestinationNewer,
    SameMetadata,
    AmbiguousTime,
    TypeConflict,
    UnsupportedName,
    SkippedLink,
    Incomplete,
}

public enum SyncAction
{
    None,
    CreateFolder,
    Copy,
    Replace,
}

public static class ComparisonCategoryText
{
    public static string Label(this ComparisonCategory c) => c switch
    {
        ComparisonCategory.OnlyOnSource => "Only on source",
        ComparisonCategory.OnlyOnDestination => "Only on destination",
        ComparisonCategory.SourceNewer => "Source newer",
        ComparisonCategory.DestinationNewer => "Destination newer",
        ComparisonCategory.SameMetadata => "Same metadata",
        ComparisonCategory.AmbiguousTime => "Same or unclear time, different size",
        ComparisonCategory.TypeConflict => "File/folder conflict",
        ComparisonCategory.UnsupportedName => "Unsupported name or case collision",
        ComparisonCategory.SkippedLink => "Skipped link",
        ComparisonCategory.Incomplete => "Unreadable or incomplete scan",
        _ => c.ToString(),
    };

    public static string Label(this SyncAction a) => a switch
    {
        SyncAction.CreateFolder => "Create folder",
        SyncAction.Copy => "Copy",
        SyncAction.Replace => "Replace",
        _ => "No action",
    };
}

/// <summary>One line of the comparison preview.</summary>
public sealed class ComparisonRow
{
    public ComparisonRow(string relativePath, ComparisonCategory category, ScannedItem? source, ScannedItem? destination, string reason)
    {
        RelativePath = relativePath;
        Category = category;
        Source = source;
        Destination = destination;
        Reason = reason;
    }

    public string RelativePath { get; }

    public ComparisonCategory Category { get; }

    public ScannedItem? Source { get; }

    public ScannedItem? Destination { get; }

    public string Reason { get; internal set; }

    /// <summary>Action performed if this row is selected.</summary>
    public SyncAction Action => Category switch
    {
        ComparisonCategory.OnlyOnSource => Source!.Kind == EntryKind.Directory ? SyncAction.CreateFolder : SyncAction.Copy,
        ComparisonCategory.SourceNewer or ComparisonCategory.DestinationNewer or ComparisonCategory.SameMetadata or ComparisonCategory.AmbiguousTime => SyncAction.Replace,
        _ => SyncAction.None,
    };

    /// <summary>Whether the user may select this row at all.</summary>
    public bool IsSelectable { get; internal set; }

    /// <summary>Replacing this row overwrites a destination that is newer than the source.</summary>
    public bool ReplacesNewer => Category == ComparisonCategory.DestinationNewer;

    public bool IsSelected { get; set; }

    /// <summary>Set when revalidation found the item changed since the scan.</summary>
    public bool NeedsReview { get; internal set; }
}

public sealed class ComparisonResult
{
    public required TransferDirection Direction { get; init; }

    public required string SourceRoot { get; init; }

    public required string DestinationRoot { get; init; }

    public required List<ComparisonRow> Rows { get; init; }

    public required bool SourceComplete { get; init; }

    public required bool DestinationComplete { get; init; }

    public IEnumerable<ComparisonRow> Selected => Rows.Where(r => r.IsSelected && r.IsSelectable);
}

/// <summary>
/// Compares two scanned trees for one-way synchronization. Equal size and time is reported as
/// "Same metadata", never as identical content. Nothing is ever proposed for deletion.
/// </summary>
public static class FolderComparer
{
    /// <summary>Default tolerance: SFTP v3 transfers whole seconds and FAT stores 2-second times.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(2);

    public static ComparisonResult Compare(ScanTree source, ScanTree destination, TransferDirection direction, TimeSpan? tolerance = null)
    {
        var tol = tolerance ?? DefaultTolerance;
        var destIsLocal = direction == TransferDirection.Download;
        var destComparer = destIsLocal ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var cmp = destIsLocal ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var destIndex = new Dictionary<string, ScannedItem>(destComparer);
        foreach (var item in destination.Items.Values.OrderBy(i => i.RelativePath, StringComparer.Ordinal))
        {
            destIndex.TryAdd(item.RelativePath, item);
        }

        var rows = new List<ComparisonRow>();
        var blocked = new List<string>(); // source-relative prefixes whose subtree is not compared
        var matchedDest = new HashSet<string>(destComparer);

        // Paths that would collide on a case-insensitive destination.
        var collisions = new HashSet<string>(StringComparer.Ordinal);
        if (destIsLocal)
        {
            foreach (var g in source.Items.Keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                foreach (var k in g)
                {
                    collisions.Add(k);
                }
            }
        }

        bool IsBlocked(string rel) => blocked.Any(b => RelativePath.IsSameOrBelow(b, rel, StringComparison.Ordinal) && b != rel);

        string? UnreadableAncestor(Dictionary<string, string> unreadable, string rel, StringComparison c) =>
            unreadable.Keys.FirstOrDefault(u => RelativePath.IsSameOrBelow(u, rel, c));

        foreach (var src in source.Items.Values.OrderBy(i => i.RelativePath, StringComparer.Ordinal))
        {
            var rel = src.RelativePath;
            if (IsBlocked(rel))
            {
                continue;
            }

            destIndex.TryGetValue(rel, out var dst);
            if (dst is not null)
            {
                matchedDest.Add(dst.RelativePath);
            }

            ComparisonRow Row(ComparisonCategory c, string reason, bool selectable, bool selected, bool block = false)
            {
                var row = new ComparisonRow(rel, c, src, dst, reason) { IsSelectable = selectable, IsSelected = selected && selectable };
                rows.Add(row);
                if (block)
                {
                    blocked.Add(rel);
                }

                return row;
            }

            if (collisions.Contains(rel))
            {
                Row(ComparisonCategory.UnsupportedName, TransferPlanner.CaseCollisionMessage, false, false, block: true);
                continue;
            }

            if (TransferPlanner.NameProblem(src.Name, destIsLocal) is { } nameProblem)
            {
                Row(ComparisonCategory.UnsupportedName, nameProblem, false, false, block: true);
                continue;
            }

            if (src.Kind == EntryKind.SymbolicLink)
            {
                Row(ComparisonCategory.SkippedLink, "The source is a link. Links are not followed or copied.", false, false, block: true);
                continue;
            }

            if (src.Kind == EntryKind.Other)
            {
                Row(ComparisonCategory.SkippedLink, "The source is a special file (device, socket or pipe) and is not copied.", false, false, block: true);
                continue;
            }

            if (UnreadableAncestor(destination.Unreadable, rel, cmp) is { } badDest)
            {
                Row(ComparisonCategory.Incomplete, $"The destination folder '{badDest}' could not be read, so this item cannot be compared.", false, false, block: true);
                continue;
            }

            if (source.Unreadable.ContainsKey(rel))
            {
                Row(ComparisonCategory.Incomplete, "This source folder could not be read: " + source.Unreadable[rel], false, false, block: true);
                continue;
            }

            if (dst is null)
            {
                var reason = src.Kind == EntryKind.Directory ? "Folder does not exist on the destination." : "File does not exist on the destination.";
                Row(ComparisonCategory.OnlyOnSource, reason, true, true);
                continue;
            }

            if (dst.Kind == EntryKind.SymbolicLink || dst.Kind == EntryKind.Other)
            {
                Row(ComparisonCategory.SkippedLink, "The destination is a link or special file. It is never written through or replaced.", false, false, block: true);
                continue;
            }

            if (destIsLocal && !string.Equals(dst.RelativePath, rel, StringComparison.Ordinal))
            {
                Row(ComparisonCategory.UnsupportedName, $"The destination has '{dst.RelativePath}', which differs only in upper/lower case. Windows treats them as the same name.", false, false, block: true);
                continue;
            }

            if (src.Kind != dst.Kind)
            {
                var what = src.Kind == EntryKind.Directory ? "a folder on the source but a file on the destination" : "a file on the source but a folder on the destination";
                Row(ComparisonCategory.TypeConflict, $"This is {what}. Nothing is merged automatically.", false, false, block: true);
                continue;
            }

            if (src.Kind == EntryKind.Directory)
            {
                continue; // Both folders: compare their contents.
            }

            if (src.ModifiedUtc is null || dst.ModifiedUtc is null)
            {
                var same = src.Length == dst.Length;
                Row(ComparisonCategory.AmbiguousTime, same
                    ? "Same size, but the modification time is unknown on one side."
                    : "Different size, and the modification time is unknown on one side.", true, false);
                continue;
            }

            var diff = src.ModifiedUtc.Value - dst.ModifiedUtc.Value;
            if (diff.Duration() <= tol)
            {
                if (src.Length == dst.Length)
                {
                    Row(ComparisonCategory.SameMetadata, "Same size and modification time (contents were not compared).", true, false);
                }
                else
                {
                    Row(ComparisonCategory.AmbiguousTime, "Same modification time but different size. Review before replacing.", true, false);
                }
            }
            else if (diff > tol)
            {
                Row(ComparisonCategory.SourceNewer, "The source was modified more recently. The destination will be replaced.", true, true);
            }
            else
            {
                Row(ComparisonCategory.DestinationNewer, "The destination was modified more recently. Replacing it requires your confirmation.", true, false);
            }
        }

        // Items only on the destination (never deleted). Report only the top-most item of a subtree.
        var reportedDestPrefixes = new List<string>();
        foreach (var dst in destination.Items.Values.OrderBy(i => i.RelativePath, StringComparer.Ordinal))
        {
            if (matchedDest.Contains(dst.RelativePath))
            {
                continue;
            }

            if (reportedDestPrefixes.Any(p => RelativePath.IsSameOrBelow(p, dst.RelativePath, StringComparison.Ordinal)))
            {
                continue;
            }

            // Inside a source subtree that was not compared (conflict, link, unreadable)?
            if (blocked.Any(b => RelativePath.IsSameOrBelow(b, dst.RelativePath, cmp) && !string.Equals(b, dst.RelativePath, cmp)))
            {
                continue;
            }

            if (UnreadableAncestor(source.Unreadable, dst.RelativePath, cmp) is { } badSrc)
            {
                rows.Add(new ComparisonRow(dst.RelativePath, ComparisonCategory.Incomplete, null, dst, $"The source folder '{badSrc}' could not be read, so it is unknown whether this exists on the source.") { IsSelectable = false });
                reportedDestPrefixes.Add(dst.RelativePath);
                continue;
            }

            var children = dst.Kind == EntryKind.Directory ? destination.Items.Keys.Count(k => k.StartsWith(dst.RelativePath + "/", StringComparison.Ordinal)) : 0;
            var reason = "Exists only on the destination. It is left untouched (nothing is ever deleted).";
            if (children > 0)
            {
                reason += $" Contains {children:N0} items.";
            }

            rows.Add(new ComparisonRow(dst.RelativePath, ComparisonCategory.OnlyOnDestination, null, dst, reason) { IsSelectable = false });
            reportedDestPrefixes.Add(dst.RelativePath);
        }

        foreach (var (rel, message) in destination.Unreadable)
        {
            if (!rows.Any(r => r.RelativePath == rel))
            {
                rows.Add(new ComparisonRow(rel, ComparisonCategory.Incomplete, source.Items.GetValueOrDefault(rel), null, "This destination folder could not be read: " + message) { IsSelectable = false });
            }
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return new ComparisonResult
        {
            Direction = direction,
            SourceRoot = source.Root,
            DestinationRoot = destination.Root,
            Rows = rows,
            SourceComplete = source.IsComplete,
            DestinationComplete = destination.IsComplete,
        };
    }

    /// <summary>
    /// Checks the whole selection: no two selected rows may write the same destination, and a selected
    /// item must not need a folder that is blocked. Returns plain-language problems.
    /// </summary>
    public static IReadOnlyList<string> ValidateSelection(ComparisonResult result)
    {
        var problems = new List<string>();
        var destIsLocal = result.Direction == TransferDirection.Download;
        var comparer = destIsLocal ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var selected = result.Selected.ToList();
        foreach (var g in selected.GroupBy(r => r.RelativePath, comparer).Where(g => g.Count() > 1))
        {
            problems.Add($"These selected items would write to the same destination: {string.Join(", ", g.Select(r => r.RelativePath))}");
        }

        var blockedRows = result.Rows.Where(r => !r.IsSelectable && r.Category is not ComparisonCategory.OnlyOnDestination).Select(r => r.RelativePath).ToList();
        foreach (var row in selected)
        {
            if (blockedRows.Any(b => RelativePath.IsSameOrBelow(b, row.RelativePath, destIsLocal ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && b != row.RelativePath))
            {
                problems.Add($"'{row.RelativePath}' is inside a folder that cannot be synchronized.");
            }
        }

        return problems;
    }
}

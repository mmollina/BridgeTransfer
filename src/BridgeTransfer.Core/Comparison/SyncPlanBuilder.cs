using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.Core.Comparison;

/// <summary>Revalidates a reviewed selection and turns it into queue items.</summary>
public static class SyncPlanBuilder
{
    /// <summary>
    /// Re-reads metadata for every selected row. Rows whose source or destination changed since the
    /// scan are unselected and marked <see cref="ComparisonRow.NeedsReview"/>. Returns the number changed.
    /// </summary>
    public static async Task<int> RevalidateAsync(ComparisonResult result, IFileStore source, IFileStore destination, CancellationToken ct)
    {
        var changed = 0;
        foreach (var row in result.Selected.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var srcPath = source.CombineRelative(result.SourceRoot, row.RelativePath);
            var dstPath = destination.CombineRelative(result.DestinationRoot, row.RelativePath);
            var src = await source.GetEntryAsync(srcPath, ct).ConfigureAwait(false);
            var dst = await destination.GetEntryAsync(dstPath, ct).ConfigureAwait(false);

            var srcChanged = row.Source is null || src is null || !row.Source.Snapshot.SameAs(EntrySnapshot.From(src) with { Length = src.IsFile ? src.Length : 0 });
            bool dstChanged;
            if (row.Destination is null)
            {
                dstChanged = dst is not null;
            }
            else
            {
                dstChanged = dst is null || !row.Destination.Snapshot.SameAs(EntrySnapshot.From(dst) with { Length = dst.IsFile ? dst.Length : 0 });
            }

            if (row.Source?.Kind == EntryKind.Directory && src is { IsDirectory: true } && (row.Destination is null ? dst is null || dst.IsDirectory : !dstChanged))
            {
                // Folder times change whenever contents change; only existence and type matter.
                continue;
            }

            if (srcChanged || dstChanged)
            {
                row.IsSelected = false;
                row.NeedsReview = true;
                row.Reason = (srcChanged ? "The source" : "The destination") + " changed after the comparison. Needs review: compare again.";
                changed++;
            }
        }

        return changed;
    }

    public static IReadOnlyList<SyncItem> Build(ComparisonResult result, IFileStore source, IFileStore destination)
    {
        var items = new List<SyncItem>();
        foreach (var row in result.Selected)
        {
            var src = row.Source!;
            items.Add(new SyncItem(
                row.RelativePath,
                src.Kind,
                source.CombineRelative(result.SourceRoot, row.RelativePath),
                destination.CombineRelative(result.DestinationRoot, row.RelativePath),
                src.Kind == EntryKind.File ? src.Snapshot : null,
                row.Destination is null ? new ExpectedDestination(false, null) : new ExpectedDestination(true, row.Destination.Snapshot),
                row.ReplacesNewer));
        }

        return items;
    }
}

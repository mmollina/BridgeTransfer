using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Paths;
using BridgeTransfer.Core.Storage;

namespace BridgeTransfer.Core.Transfers;

public sealed record PlannedDirectory(string RelativePath, string SourcePath, string DestinationPath);

public sealed record PlannedFile(string RelativePath, string SourcePath, string DestinationPath, long Length, DateTime? ModifiedUtc);

public sealed record PlannedSkip(string RelativePath, string SourcePath, SkipReason Reason, string Message);

/// <summary>Result of scanning a selection: everything to create and copy, and everything left out with reasons.</summary>
public sealed class TransferPlan
{
    public List<PlannedDirectory> Directories { get; } = new();

    public List<PlannedFile> Files { get; } = new();

    public List<PlannedSkip> Skipped { get; } = new();

    public long TotalBytes => Files.Sum(f => f.Length);

    /// <summary>True when some folder could not be read, so the plan does not cover everything selected.</summary>
    public bool IsIncomplete => Skipped.Any(s => s.Reason == SkipReason.Unreadable);
}

/// <summary>
/// Builds a <see cref="TransferPlan"/> by scanning selected items on the source side.
/// Uploading folder "project" into "/data" produces "/data/project".
/// </summary>
public static class TransferPlanner
{
    public static async Task<TransferPlan> PlanAsync(
        IFileStore source,
        IFileStore destination,
        IReadOnlyList<FileEntry> selection,
        string destinationDirectory,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        var plan = new TransferPlan();
        var destIsLocal = destination.Side == EndpointSide.Local;
        var scanned = 0;

        // Top-level names must not collide with each other on the destination either.
        var topLevel = selection.ToList();
        var topCollisions = CollidingNames(topLevel.Select(e => e.Name), destIsLocal);

        foreach (var item in topLevel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NameProblem(item.Name, destIsLocal) is { } problem)
            {
                plan.Skipped.Add(new PlannedSkip(item.Name, item.FullPath, SkipReason.UnsupportedName, problem));
                continue;
            }

            if (topCollisions.Contains(item.Name))
            {
                plan.Skipped.Add(new PlannedSkip(item.Name, item.FullPath, SkipReason.CaseCollision, CaseCollisionMessage));
                continue;
            }

            var destPath = destination.Combine(destinationDirectory, item.Name);
            switch (item.Kind)
            {
                case EntryKind.File:
                    plan.Files.Add(new PlannedFile(item.Name, item.FullPath, destPath, item.Length, item.ModifiedUtc));
                    break;
                case EntryKind.Directory:
                    plan.Directories.Add(new PlannedDirectory(item.Name, item.FullPath, destPath));
                    await ScanDirectoryAsync(source, destination, item.FullPath, item.Name, destPath, plan, destIsLocal, () => progress?.Invoke(++scanned), cancellationToken).ConfigureAwait(false);
                    break;
                case EntryKind.SymbolicLink:
                    plan.Skipped.Add(new PlannedSkip(item.Name, item.FullPath, SkipReason.SymbolicLink, LinkMessage));
                    break;
                default:
                    plan.Skipped.Add(new PlannedSkip(item.Name, item.FullPath, SkipReason.SpecialFile, SpecialMessage));
                    break;
            }
        }

        return plan;
    }

    public const string LinkMessage = "Links are skipped so that copying never follows them outside the selected folder.";
    public const string SpecialMessage = "Special files (devices, sockets, pipes) are not copied.";
    public const string CaseCollisionMessage = "Another item in the same folder has the same name apart from upper/lower case. Windows cannot store both.";

    public static string? NameProblem(string name, bool destinationIsLocal)
    {
        if (RemotePath.ValidateChildName(name) is { } p)
        {
            return p;
        }

        if (destinationIsLocal && WindowsNameRules.Validate(name) is { } w)
        {
            return w;
        }

        return null;
    }

    public static HashSet<string> CollidingNames(IEnumerable<string> names, bool destinationIsLocal)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!destinationIsLocal)
        {
            return set;
        }

        foreach (var group in WindowsNameRules.FindCaseCollisions(names))
        {
            foreach (var n in group)
            {
                set.Add(n);
            }
        }

        return set;
    }

    private static async Task ScanDirectoryAsync(
        IFileStore source,
        IFileStore destination,
        string sourceDir,
        string relativeDir,
        string destDir,
        TransferPlan plan,
        bool destIsLocal,
        Action tick,
        CancellationToken ct)
    {
        // Iterative depth-first traversal to avoid deep recursion on very deep trees.
        var stack = new Stack<(string Source, string Rel, string Dest)>();
        stack.Push((sourceDir, relativeDir, destDir));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (src, rel, dst) = stack.Pop();
            IReadOnlyList<FileEntry> children;
            try
            {
                children = await source.ListAsync(src, ct).ConfigureAwait(false);
            }
            catch (StoreException ex) when (ex.Kind != FailureKind.Cancelled && ex.Kind != FailureKind.Transient)
            {
                plan.Skipped.Add(new PlannedSkip(rel, src, SkipReason.Unreadable, "This folder could not be read: " + ex.Message));
                continue;
            }

            tick();
            var collisions = CollidingNames(children.Select(c => c.Name), destIsLocal);
            var subdirs = new List<(string, string, string)>();
            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                if (RemotePath.ValidateChildName(child.Name) is not null)
                {
                    continue; // '.', '..' or malformed server names never enter a plan.
                }

                var childRel = RelativePath.Join(rel, child.Name);
                if (NameProblem(child.Name, destIsLocal) is { } problem)
                {
                    plan.Skipped.Add(new PlannedSkip(childRel, child.FullPath, SkipReason.UnsupportedName, problem));
                    continue;
                }

                if (collisions.Contains(child.Name))
                {
                    plan.Skipped.Add(new PlannedSkip(childRel, child.FullPath, SkipReason.CaseCollision, CaseCollisionMessage));
                    continue;
                }

                var childDest = destination.Combine(dst, child.Name);
                switch (child.Kind)
                {
                    case EntryKind.File:
                        plan.Files.Add(new PlannedFile(childRel, child.FullPath, childDest, child.Length, child.ModifiedUtc));
                        break;
                    case EntryKind.Directory:
                        plan.Directories.Add(new PlannedDirectory(childRel, child.FullPath, childDest));
                        subdirs.Add((child.FullPath, childRel, childDest));
                        break;
                    case EntryKind.SymbolicLink:
                        plan.Skipped.Add(new PlannedSkip(childRel, child.FullPath, SkipReason.SymbolicLink, LinkMessage));
                        break;
                    default:
                        plan.Skipped.Add(new PlannedSkip(childRel, child.FullPath, SkipReason.SpecialFile, SpecialMessage));
                        break;
                }
            }

            for (var i = subdirs.Count - 1; i >= 0; i--)
            {
                stack.Push(subdirs[i]);
            }
        }
    }
}

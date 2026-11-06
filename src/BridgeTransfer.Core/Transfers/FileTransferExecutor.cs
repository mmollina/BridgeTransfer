using System.Security.Cryptography;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;

namespace BridgeTransfer.Core.Transfers;

public enum FileOutcomeKind
{
    Completed,
    CompletedWithWarning,
    Skipped,
    Failed,
    Cancelled,
    NeedsDecision,

    /// <summary>Transient failure; may be retried after a delay.</summary>
    Retry,

    /// <summary>The destination changed under us; re-run the checks without counting an attempt.</summary>
    Requeue,
}

public sealed record FileOutcome(FileOutcomeKind Kind, string? Message = null, FailureKind? Failure = null, ConflictRequest? Conflict = null);

/// <summary>Records application-owned staging files so they survive crashes and can be cleaned safely.</summary>
public interface IStagingRegistry
{
    /// <summary>Called before a staging file is created.</summary>
    void Register(TransferJob job, FileTask task, EndpointSide side, string path);

    void Unregister(string path);
}

/// <summary>Prevents two tasks from writing the same destination at once.</summary>
public interface IDestinationLocks
{
    bool TryAcquire(TransferJob job, string path, Guid owner);

    void Release(TransferJob job, string path, Guid owner);
}

/// <summary>
/// Copies one file following the integrity rules: write to an owned staging name, verify the
/// byte count and source metadata, then promote with a no-replace or one-step replace rename.
/// An existing destination is never removed before its replacement is ready.
/// </summary>
public sealed class FileTransferExecutor
{
    private readonly TransferOptions _options;
    private readonly IStagingRegistry _staging;
    private readonly IDestinationLocks _locks;

    public FileTransferExecutor(TransferOptions options, IStagingRegistry staging, IDestinationLocks locks)
    {
        _options = options;
        _staging = staging;
        _locks = locks;
    }

    public static string StagingName(FileTask task)
    {
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        return $"{AppInfo.StagingPrefix}{task.Job.Id:N}"[..(AppInfo.StagingPrefix.Length + 8)] + $"-{task.Id:N}"[..9] + $"-{random}{AppInfo.StagingSuffix}";
    }

    public async Task<FileOutcome> ExecuteAsync(FileTask task, IFileStore source, IFileStore destination, CancellationToken ct)
    {
        string? staging = null;
        try
        {
            await CleanupLeftoverStagingAsync(task, destination, ct).ConfigureAwait(false);

            if (task.PendingPromotion is { } pending)
            {
                var reconciled = await ReconcilePromotionAsync(task, destination, pending, ct).ConfigureAwait(false);
                if (reconciled is not null)
                {
                    return reconciled;
                }
            }

            // 0. Fast path for selected items: decide from the destination folder listing taken when the
            //    folders were prepared, so that skipping or asking costs no network round trip.
            var knownAbsent = false;
            if (task.Job.Kind == JobKind.Items && !task.FreshDestination &&
                task.Job.DestinationIndex.TryLookup(destination, task.DestinationPath, out var listed))
            {
                if (listed is null)
                {
                    knownAbsent = true;
                }
                else
                {
                    var scanned = new FileEntry(source.GetName(task.SourcePath), task.SourcePath, EntryKind.File, task.ExpectedLength, task.SourceModifiedUtc);
                    var early = Decide(task, scanned, listed);
                    if (early.Outcome is not null)
                    {
                        return early.Outcome;
                    }
                }
            }

            // 1. Source checks.
            var src = await source.GetEntryAsync(task.SourcePath, ct).ConfigureAwait(false);
            if (src is null)
            {
                return Fail(FailureKind.NotFound, "The source file no longer exists.");
            }

            if (!src.IsFile)
            {
                return Fail(FailureKind.TypeConflict, "The source is no longer a regular file.");
            }

            var srcSnap = EntrySnapshot.From(src);
            if (task.ExpectedSource is { } expectedSrc && !expectedSrc.SameAs(srcSnap))
            {
                return new FileOutcome(FileOutcomeKind.Skipped, "The source changed after the comparison. Compare again to review it.", FailureKind.SourceChanged);
            }

            task.ExpectedLength = src.Length;
            task.SourceModifiedUtc = src.ModifiedUtc;

            // 2. Destination checks and conflict decisions. A destination known to be absent is not
            //    checked again here: the exclusive create and the no-replace rename catch late arrivals.
            var dst = knownAbsent ? null : await destination.GetEntryAsync(task.DestinationPath, ct).ConfigureAwait(false);
            var replace = false;
            if (task.Job.Kind == JobKind.Sync)
            {
                var expected = task.ExpectedDestination ?? new ExpectedDestination(false, null);
                if (!expected.Exists && dst is not null)
                {
                    return new FileOutcome(FileOutcomeKind.Skipped, "Something now exists at the destination that was not there during the comparison. Compare again to review it.", FailureKind.DestinationChanged);
                }

                if (expected.Exists)
                {
                    if (dst is null || expected.Snapshot is null || !expected.Snapshot.SameAs(EntrySnapshot.From(dst)) || !dst.IsFile)
                    {
                        return new FileOutcome(FileOutcomeKind.Skipped, "The destination changed after the comparison. Compare again to review it.", FailureKind.DestinationChanged);
                    }

                    if (IsNewer(dst, src) && !task.NewerReplaceApproved)
                    {
                        return new FileOutcome(FileOutcomeKind.Skipped, "The destination is newer and replacing it was not approved.", FailureKind.DestinationChanged);
                    }

                    replace = true;
                    task.ReplaceTarget = EntrySnapshot.From(dst);
                }
            }
            else if (dst is not null)
            {
                var decision = Decide(task, src, dst);
                if (decision.Outcome is not null)
                {
                    return decision.Outcome;
                }

                if (decision.Action == ConflictChoice.KeepBoth)
                {
                    await ChooseKeepBothNameAsync(task, destination, ct).ConfigureAwait(false);
                }
                else
                {
                    replace = true;
                    task.ReplaceTarget = EntrySnapshot.From(dst);
                }
            }

            var parent = destination.GetParent(task.DestinationPath) ?? throw new StoreException(FailureKind.InvalidName, "The destination has no parent folder.");

            // 3. Free space on this computer.
            if (destination.Side == EndpointSide.Local)
            {
                var free = await destination.GetAvailableSpaceAsync(parent, ct).ConfigureAwait(false);
                if (free is { } f && f < src.Length + _options.LocalFreeSpaceMarginBytes)
                {
                    return Fail(FailureKind.DiskFull, $"Not enough free space on this computer: {Text.Format.Bytes(f)} available, {Text.Format.Bytes(src.Length)} needed.");
                }
            }

            // 4. Write to an owned staging file.
            staging = destination.Combine(parent, StagingName(task));
            _staging.Register(task.Job, task, destination.Side, staging);
            task.OwnedStagingPaths.Add(staging);
            task.SetBytesDone(0);

            long counted;
            if (destination.Side == EndpointSide.Remote)
            {
                await using var input = await source.OpenReadAsync(task.SourcePath, ct).ConfigureAwait(false);
                await using var counting = new CountingStream(input);
                await destination.WriteNewFileAsync(staging, counting, b => task.SetBytesDone(b), ct).ConfigureAwait(false);
                counted = counting.Count;
            }
            else
            {
                var output = await destination.CreateNewAsync(staging, ct).ConfigureAwait(false);
                await using (output.ConfigureAwait(false))
                {
                    await using var counting = new CountingStream(output, leaveOpen: true);
                    await source.ReadFileAsync(task.SourcePath, counting, b => task.SetBytesDone(b), ct).ConfigureAwait(false);
                    await counting.FlushAsync(ct).ConfigureAwait(false);
                    counted = counting.Count;
                    if (output is FileStream fs)
                    {
                        fs.Flush(flushToDisk: true);
                    }
                }
            }

            // 5. Verify length and that the source did not change while it was read.
            var srcAfter = await source.GetEntryAsync(task.SourcePath, ct).ConfigureAwait(false);
            if (srcAfter is null || !srcSnap.SameAs(EntrySnapshot.From(srcAfter)))
            {
                await DeleteStagingAsync(task, destination, staging).ConfigureAwait(false);
                return Fail(FailureKind.SourceChanged, "The source file changed while it was being copied. The destination was left unchanged; try again when the file is no longer being written.");
            }

            if (counted != src.Length)
            {
                await DeleteStagingAsync(task, destination, staging).ConfigureAwait(false);
                return Fail(FailureKind.VerificationFailed, $"Copied {counted:N0} bytes but the source has {src.Length:N0} bytes. The destination was left unchanged.");
            }

            // 6. Preserve the modification time on the staged file (a failure is only a warning) and
            //    verify its size from the same server reply.
            string? warning = null;
            FileEntry? staged = null;
            if (_options.PreserveTimestamps && src.ModifiedUtc is { } mtime)
            {
                try
                {
                    staged = await destination.SetModifiedTimeAsync(staging, mtime, ct).ConfigureAwait(false);
                }
                catch (StoreException ex) when (ex.Kind is not (FailureKind.Transient or FailureKind.Cancelled))
                {
                    warning = "The file was copied, but its modification time could not be preserved.";
                }
            }

            staged ??= await destination.GetEntryAsync(staging, ct).ConfigureAwait(false);
            if (staged is null || !staged.IsFile || staged.Length != counted)
            {
                await DeleteStagingAsync(task, destination, staging).ConfigureAwait(false);
                return Fail(FailureKind.VerificationFailed, "The size of the written file does not match the source. The destination was left unchanged.");
            }

            task.SetBytesDone(counted);

            ct.ThrowIfCancellationRequested();

            // 7. Promote. From here on, do not cancel mid-rename.
            return await PromoteAsync(task, destination, staging, srcSnap, replace, counted, warning).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (staging is not null && task.PendingPromotion is null)
            {
                await DeleteStagingAsync(task, destination, staging).ConfigureAwait(false);
            }

            return new FileOutcome(FileOutcomeKind.Cancelled, "Cancelled. The destination was left unchanged.", FailureKind.Cancelled);
        }
        catch (StoreException ex)
        {
            if (staging is not null && task.PendingPromotion is null && ex.Kind != FailureKind.Transient)
            {
                await DeleteStagingAsync(task, destination, staging).ConfigureAwait(false);
            }

            return ex.Kind switch
            {
                FailureKind.Cancelled => new FileOutcome(FileOutcomeKind.Cancelled, "Cancelled.", FailureKind.Cancelled),
                FailureKind.Transient => new FileOutcome(FileOutcomeKind.Retry, ex.Message, FailureKind.Transient),
                FailureKind.AlreadyExists when staging is null => new FileOutcome(FileOutcomeKind.Requeue, ex.Message, ex.Kind),
                _ => Fail(ex.Kind, ex.Message),
            };
        }
    }

    private static FileOutcome Fail(FailureKind kind, string message) => new(FileOutcomeKind.Failed, message, kind);

    /// <summary>True when <paramref name="a"/> was modified more than the tolerance after <paramref name="b"/>.</summary>
    private bool IsNewer(FileEntry a, FileEntry b) =>
        a.ModifiedUtc is { } x && b.ModifiedUtc is { } y && x - y > _options.TimeTolerance;

    private async Task ChooseKeepBothNameAsync(FileTask task, IFileStore destination, CancellationToken ct)
    {
        var parent = destination.GetParent(task.OriginalDestinationPath)!;
        var name = destination.GetName(task.OriginalDestinationPath);
        var job = task.Job;
        string? acquired = null;
        var newName = await KeepBothNamer.FindFreeNameAsync(name, async candidate =>
        {
            var path = destination.Combine(parent, candidate);
            if (await destination.GetEntryAsync(path, ct).ConfigureAwait(false) is not null)
            {
                return true;
            }

            if (!_locks.TryAcquire(job, path, task.Id))
            {
                return true;
            }

            acquired = path;
            return false;
        }).ConfigureAwait(false);

        var newPath = destination.Combine(parent, newName);
        if (!string.Equals(newPath, task.DestinationPath, StringComparison.Ordinal))
        {
            _locks.Release(job, task.DestinationPath, task.Id);
        }

        task.DestinationPath = acquired ?? newPath;
    }

    private async Task<FileOutcome> PromoteAsync(FileTask task, IFileStore destination, string staging, EntrySnapshot srcSnap, bool replace, long counted, string? warning)
    {
        var ct = CancellationToken.None;
        task.PendingPromotion = (staging, task.DestinationPath, srcSnap);
        if (!replace)
        {
            try
            {
                // Refuses to overwrite: if anything appeared at the destination meanwhile, it is kept.
                await destination.MoveNoReplaceAsync(staging, task.DestinationPath, ct).ConfigureAwait(false);
            }
            catch (StoreException ex) when (ex.Kind == FailureKind.AlreadyExists)
            {
                task.PendingPromotion = null;
                await DeleteStagingAsync(task, destination, staging).ConfigureAwait(false);
                task.Decision = null;
                task.FreshDestination = true;
                return new FileOutcome(FileOutcomeKind.Requeue, "The destination appeared while copying; checking again.", FailureKind.DestinationChanged);
            }
        }
        else
        {
            var current = await destination.GetEntryAsync(task.DestinationPath, ct).ConfigureAwait(false);
            var expected = task.Job.Kind == JobKind.Sync ? task.ExpectedDestination?.Snapshot : task.ReplaceTarget;
            if (current is null)
            {
                try
                {
                    await destination.MoveNoReplaceAsync(staging, task.DestinationPath, ct).ConfigureAwait(false);
                }
                catch (StoreException ex) when (ex.Kind == FailureKind.AlreadyExists)
                {
                    task.PendingPromotion = null;
                    await DeleteStagingAsync(task, destination, staging).ConfigureAwait(false);
                    task.Decision = null;
                    task.FreshDestination = true;
                    return new FileOutcome(FileOutcomeKind.Requeue, "The destination changed while copying; checking again.", FailureKind.DestinationChanged);
                }
            }
            else if (!current.IsFile || expected is null || !expected.SameAs(EntrySnapshot.From(current)))
            {
                task.PendingPromotion = null;
                await DeleteStagingAsync(task, destination, staging).ConfigureAwait(false);
                task.Decision = null;
                task.FreshDestination = true;
                if (task.Job.Kind == JobKind.Sync)
                {
                    return new FileOutcome(FileOutcomeKind.Skipped, "The destination changed during the transfer. It was left unchanged; compare again to review it.", FailureKind.DestinationChanged);
                }

                return new FileOutcome(FileOutcomeKind.Requeue, "The destination changed while copying; asking again.", FailureKind.DestinationChanged);
            }
            else
            {
                try
                {
                    await destination.MoveReplaceAsync(staging, task.DestinationPath, ct).ConfigureAwait(false);
                }
                catch (StoreException ex) when (ex.Kind == FailureKind.ReplaceNotSupported)
                {
                    task.PendingPromotion = null;
                    await DeleteStagingAsync(task, destination, staging).ConfigureAwait(false);
                    return Fail(FailureKind.ReplaceNotSupported, ex.Message);
                }
            }
        }

        // The staged file was verified (size) before the rename, and the rename is a single step.
        task.PendingPromotion = null;
        task.OwnedStagingPaths.Remove(staging);
        _staging.Unregister(staging);

        var renamed = !string.Equals(task.DestinationPath, task.OriginalDestinationPath, StringComparison.Ordinal);
        var note = renamed ? $"Saved as {destination.GetName(task.DestinationPath)} (kept both)." : null;
        return warning is null
            ? new FileOutcome(FileOutcomeKind.Completed, note)
            : new FileOutcome(FileOutcomeKind.CompletedWithWarning, note is null ? warning : note + " " + warning);
    }

    private sealed record Decision(ConflictChoice Action, FileOutcome? Outcome);

    /// <summary>
    /// Resolves what to do with an existing destination: an outcome (skip or ask) or an action
    /// (replace or keep both). A newer destination is never replaced without explicit approval.
    /// </summary>
    private Decision Decide(FileTask task, FileEntry src, FileEntry dst)
    {
        var kind = !dst.IsFile ? ConflictKind.TypeConflict : IsNewer(dst, src) ? ConflictKind.DestinationNewer : ConflictKind.FileExists;
        var choice = task.Decision ?? task.Job.ConflictPolicy.ChoiceFor(kind);
        string? skipMessage = null;
        if (choice.IsRule())
        {
            if (kind == ConflictKind.TypeConflict)
            {
                choice = ConflictChoice.Ask;
            }
            else
            {
                var srcNewer = IsNewer(src, dst);
                var sizeDiffers = src.Length != dst.Length;
                var timeUnknown = src.ModifiedUtc is null || dst.ModifiedUtc is null;
                var wanted = choice switch
                {
                    ConflictChoice.ReplaceIfNewer => srcNewer,
                    ConflictChoice.ReplaceIfSizeDiffers => sizeDiffers,
                    _ => srcNewer || sizeDiffers,
                };
                if (!wanted && timeUnknown && choice != ConflictChoice.ReplaceIfSizeDiffers)
                {
                    choice = ConflictChoice.Ask; // Cannot tell which file is newer.
                }
                else if (wanted)
                {
                    choice = ConflictChoice.Replace;
                }
                else
                {
                    skipMessage = choice switch
                    {
                        ConflictChoice.ReplaceIfNewer => "Skipped: the existing file is not older than this one.",
                        ConflictChoice.ReplaceIfSizeDiffers => "Skipped: the existing file has the same size.",
                        _ => "Skipped: the existing file has the same size and is not older.",
                    };
                    choice = ConflictChoice.Skip;
                }
            }
        }

        if (choice == ConflictChoice.Replace && kind == ConflictKind.TypeConflict)
        {
            choice = ConflictChoice.Ask;
        }

        if (choice == ConflictChoice.Replace && kind == ConflictKind.DestinationNewer && !task.NewerReplaceApproved)
        {
            choice = ConflictChoice.Ask;
        }

        return choice switch
        {
            ConflictChoice.Ask => AskOutcome(task, kind, src, dst),
            ConflictChoice.Skip => new Decision(choice, new FileOutcome(FileOutcomeKind.Skipped, skipMessage ?? "Skipped because the destination already exists.", FailureKind.AlreadyExists)),
            _ => new Decision(choice, null),
        };
    }

    private static Decision AskOutcome(FileTask task, ConflictKind kind, FileEntry src, FileEntry dst)
    {
        task.Decision = null;
        var message = kind switch
        {
            ConflictKind.TypeConflict => "A folder or link with this name already exists.",
            ConflictKind.DestinationNewer => "A newer file with this name already exists.",
            _ => "A file with this name already exists.",
        };
        return new Decision(ConflictChoice.Ask, new FileOutcome(
            FileOutcomeKind.NeedsDecision,
            message,
            Conflict: new ConflictRequest(task.Job.Id, task.Id, task.Job.Direction, task.RelativePath, task.DestinationPath, kind, src, dst)));
    }

    /// <summary>After a failure during rename, decides from the actual state whether the rename happened.</summary>
    private async Task<FileOutcome?> ReconcilePromotionAsync(FileTask task, IFileStore destination, (string Staging, string Final, EntrySnapshot Source) pending, CancellationToken ct)
    {
        var staged = await destination.GetEntryAsync(pending.Staging, ct).ConfigureAwait(false);
        var final = await destination.GetEntryAsync(pending.Final, ct).ConfigureAwait(false);
        task.PendingPromotion = null;
        if (staged is null && final is { IsFile: true } && final.Length == pending.Source.Length &&
            (!_options.PreserveTimestamps || pending.Source.ModifiedUtc is null || final.ModifiedUtc is null ||
             Math.Abs((final.ModifiedUtc.Value - pending.Source.ModifiedUtc.Value).TotalSeconds) < 1.0))
        {
            task.OwnedStagingPaths.Remove(pending.Staging);
            _staging.Unregister(pending.Staging);
            task.SetBytesDone(final.Length);
            return new FileOutcome(FileOutcomeKind.Completed, "Confirmed after reconnecting.");
        }

        // The rename did not happen (or the result is unclear): the staging file is still ours and
        // will be removed by the leftover cleanup; the file is copied again from the start.
        await CleanupLeftoverStagingAsync(task, destination, ct).ConfigureAwait(false);
        return null;
    }

    private async Task CleanupLeftoverStagingAsync(FileTask task, IFileStore destination, CancellationToken ct)
    {
        foreach (var path in task.OwnedStagingPaths.ToList())
        {
            if (task.PendingPromotion is { } p && p.Staging == path)
            {
                continue;
            }

            var entry = await destination.GetEntryAsync(path, ct).ConfigureAwait(false);
            if (entry is null)
            {
                task.OwnedStagingPaths.Remove(path);
                _staging.Unregister(path);
                continue;
            }

            if (entry.IsFile && entry.Name.StartsWith(AppInfo.StagingPrefix, StringComparison.Ordinal))
            {
                await destination.DeleteOwnedFileAsync(path, ct).ConfigureAwait(false);
                task.OwnedStagingPaths.Remove(path);
                _staging.Unregister(path);
            }
        }
    }

    private async Task DeleteStagingAsync(FileTask task, IFileStore destination, string staging)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var entry = await destination.GetEntryAsync(staging, cts.Token).ConfigureAwait(false);
            if (entry is { IsFile: true })
            {
                await destination.DeleteOwnedFileAsync(staging, cts.Token).ConfigureAwait(false);
            }

            task.OwnedStagingPaths.Remove(staging);
            _staging.Unregister(staging);
        }
        catch (Exception)
        {
            // Left registered; it is removed on the next attempt or offered for cleanup later.
        }
    }
}

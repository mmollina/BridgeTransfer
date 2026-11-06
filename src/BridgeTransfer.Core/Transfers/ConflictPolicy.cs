using BridgeTransfer.Core.Models;

namespace BridgeTransfer.Core.Transfers;

/// <summary>Kind of destination conflict.</summary>
public enum ConflictKind
{
    /// <summary>A file exists and is not newer than the source.</summary>
    FileExists,

    /// <summary>A file exists and is newer than the source.</summary>
    DestinationNewer,

    /// <summary>A folder, link or special file exists where a file should go.</summary>
    TypeConflict,
}

/// <summary>A question for the user about one existing destination.</summary>
public sealed record ConflictRequest(
    Guid JobId,
    Guid TaskId,
    TransferDirection Direction,
    string RelativePath,
    string DestinationPath,
    ConflictKind Kind,
    FileEntry Source,
    FileEntry Destination)
{
    public bool ReplaceAllowed => Kind != ConflictKind.TypeConflict;
}

/// <summary>
/// "Apply to remaining conflicts within this batch". Replacement of a newer destination is
/// never applied automatically, and type conflicts can never be replaced.
/// </summary>
public sealed class BatchConflictPolicy
{
    private readonly object _gate = new();
    private ConflictChoice _forExisting = ConflictChoice.Ask;
    private ConflictChoice _forNewer = ConflictChoice.Ask;
    private ConflictChoice _forType = ConflictChoice.Ask;

    public ConflictChoice ChoiceFor(ConflictKind kind)
    {
        lock (_gate)
        {
            return kind switch
            {
                ConflictKind.FileExists => _forExisting,
                ConflictKind.DestinationNewer => _forNewer,
                _ => _forType,
            };
        }
    }

    /// <summary>Records a batch-wide choice made for a conflict of <paramref name="kind"/>.</summary>
    public void ApplyToRemaining(ConflictKind kind, ConflictChoice choice)
    {
        lock (_gate)
        {
            if (choice.IsRule())
            {
                // Rules decide per file; a newer destination is still never replaced without asking.
                if (kind != ConflictKind.TypeConflict)
                {
                    _forExisting = choice;
                    _forNewer = choice;
                }

                return;
            }

            switch (kind)
            {
                case ConflictKind.FileExists:
                    _forExisting = choice;

                    // Skip and Keep both are safe for newer destinations too; Replace is not.
                    if (choice != ConflictChoice.Replace && _forNewer == ConflictChoice.Ask)
                    {
                        _forNewer = choice;
                    }

                    if (choice != ConflictChoice.Replace && _forType == ConflictChoice.Ask)
                    {
                        _forType = choice;
                    }

                    break;
                case ConflictKind.DestinationNewer:
                    // Never remember "replace newer" for the batch.
                    _forNewer = choice == ConflictChoice.Replace ? ConflictChoice.Ask : choice;
                    break;
                case ConflictKind.TypeConflict:
                    _forType = choice == ConflictChoice.Replace ? ConflictChoice.Ask : choice;
                    break;
            }
        }
    }

    /// <summary>The choice made before the transfer started (in the confirmation dialog or settings).</summary>
    public void SetInitial(ConflictChoice choice)
    {
        lock (_gate)
        {
            _forExisting = choice;
            _forNewer = choice == ConflictChoice.Replace ? ConflictChoice.Ask : choice;
            _forType = choice is ConflictChoice.Replace || choice.IsRule() ? ConflictChoice.Ask : choice;
        }
    }
}

/// <summary>Generates "name (2).ext" style names that do not collide.</summary>
public static class KeepBothNamer
{
    public static string Candidate(string name, int n)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return $"{name} ({n})";
        }

        return $"{name[..dot]} ({n}){name[dot..]}";
    }

    /// <summary>Returns the first candidate for which <paramref name="isTaken"/> is false.</summary>
    public static async Task<string> FindFreeNameAsync(string name, Func<string, Task<bool>> isTaken, int maxAttempts = 9999)
    {
        for (var n = 2; n < maxAttempts; n++)
        {
            var candidate = Candidate(name, n);
            if (!await isTaken(candidate).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a free name.");
    }
}

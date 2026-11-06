namespace BridgeTransfer.Core.Paths;

/// <summary>Helpers for local Windows paths. Comparison is case-insensitive like NTFS.</summary>
public static class LocalPath
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>Joins a root and a validated relative path ('/'-separated) and checks root containment.</summary>
    public static string CombineRelative(string root, string relativePath)
    {
        var result = root;
        foreach (var segment in RelativePath.Split(relativePath))
        {
            result = Combine(result, segment);
        }

        return result;
    }

    /// <summary>Joins one child name, refusing names that could escape the directory.</summary>
    public static string Combine(string directory, string name)
    {
        if (RemotePath.ValidateChildName(name) is { } problem)
        {
            throw new ArgumentException($"Invalid name '{name}': {problem}", nameof(name));
        }

        if (name.Contains('\\') || name.Contains(':') || Path.IsPathRooted(name))
        {
            throw new ArgumentException($"Name '{name}' cannot be used as a local file name.", nameof(name));
        }

        var combined = Path.Combine(directory, name);
        if (!IsWithin(directory, combined))
        {
            throw new ArgumentException($"Name '{name}' would leave the folder '{directory}'.", nameof(name));
        }

        return combined;
    }

    public static bool IsWithin(string root, string path)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(r, p, Comparison))
        {
            return true;
        }

        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, Comparison);
    }

    /// <summary>Key used to detect two writes to the same local file (case-insensitive).</summary>
    public static string LockKey(string path) => Path.GetFullPath(path).ToUpperInvariant();
}

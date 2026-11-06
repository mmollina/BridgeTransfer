namespace BridgeTransfer.Core.Paths;

/// <summary>
/// Relative paths inside a transfer or comparison, always '/'-separated, never rooted,
/// never containing '.' or '..' segments.
/// </summary>
public static class RelativePath
{
    public static string Join(string parent, string name)
    {
        if (RemotePath.ValidateChildName(name) is { } problem)
        {
            throw new ArgumentException($"Invalid name '{name}': {problem}", nameof(name));
        }

        return parent.Length == 0 ? name : parent + "/" + name;
    }

    /// <summary>Splits and validates a relative path. Throws for traversal or empty segments.</summary>
    public static IReadOnlyList<string> Split(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return Array.Empty<string>();
        }

        if (relativePath.StartsWith('/'))
        {
            throw new ArgumentException($"Relative path '{relativePath}' is rooted.", nameof(relativePath));
        }

        var segments = relativePath.Split('/');
        foreach (var s in segments)
        {
            if (RemotePath.ValidateChildName(s) is { } problem)
            {
                throw new ArgumentException($"Relative path '{relativePath}' has an invalid segment: {problem}", nameof(relativePath));
            }
        }

        return segments;
    }

    public static string? GetParent(string relativePath)
    {
        var idx = relativePath.LastIndexOf('/');
        return idx < 0 ? (relativePath.Length == 0 ? null : "") : relativePath[..idx];
    }

    public static string GetName(string relativePath)
    {
        var idx = relativePath.LastIndexOf('/');
        return idx < 0 ? relativePath : relativePath[(idx + 1)..];
    }

    public static int Depth(string relativePath) => relativePath.Length == 0 ? 0 : relativePath.Count(c => c == '/') + 1;

    /// <summary>True if <paramref name="path"/> is <paramref name="ancestor"/> or lies below it.</summary>
    public static bool IsSameOrBelow(string ancestor, string path, StringComparison comparison)
    {
        if (ancestor.Length == 0)
        {
            return true;
        }

        return string.Equals(path, ancestor, comparison) || path.StartsWith(ancestor + "/", comparison);
    }
}

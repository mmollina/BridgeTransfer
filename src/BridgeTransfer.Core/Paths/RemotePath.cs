using System.Text;

namespace BridgeTransfer.Core.Paths;

/// <summary>
/// POSIX path helpers for the Linux side. Paths are case-sensitive, use '/', and are never
/// passed through Windows path APIs.
/// </summary>
public static class RemotePath
{
    public const char Separator = '/';

    /// <summary>Returns null when <paramref name="name"/> is a legitimate single child name.</summary>
    public static string? ValidateChildName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "The name is empty.";
        }

        if (name is "." or "..")
        {
            return "'.' and '..' are not valid names.";
        }

        if (name.Contains('/'))
        {
            return "The name contains '/'.";
        }

        if (name.Contains('\0'))
        {
            return "The name contains a NUL character.";
        }

        return null;
    }

    public static bool IsValidChildName(string? name) => ValidateChildName(name) is null;

    /// <summary>Joins a directory and one validated child name.</summary>
    public static string Combine(string directory, string childName)
    {
        if (ValidateChildName(childName) is { } problem)
        {
            throw new ArgumentException($"Invalid remote name '{childName}': {problem}", nameof(childName));
        }

        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Directory is empty.", nameof(directory));
        }

        return directory.EndsWith(Separator) ? directory + childName : directory + Separator + childName;
    }

    /// <summary>Joins a root and a relative path made of '/'-separated validated segments.</summary>
    public static string CombineRelative(string root, string relativePath)
    {
        var result = root;
        foreach (var segment in RelativePath.Split(relativePath))
        {
            result = Combine(result, segment);
        }

        return result;
    }

    public static string GetName(string path)
    {
        var trimmed = TrimTrailingSeparators(path);
        var idx = trimmed.LastIndexOf(Separator);
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    /// <summary>Parent directory, or null for the root "/".</summary>
    public static string? GetParent(string path)
    {
        var trimmed = TrimTrailingSeparators(path);
        if (trimmed == "/" || trimmed.Length == 0)
        {
            return null;
        }

        var idx = trimmed.LastIndexOf(Separator);
        if (idx < 0)
        {
            return null;
        }

        return idx == 0 ? "/" : trimmed[..idx];
    }

    public static string TrimTrailingSeparators(string path)
    {
        if (path.Length <= 1)
        {
            return path;
        }

        var end = path.Length;
        while (end > 1 && path[end - 1] == Separator)
        {
            end--;
        }

        return path[..end];
    }

    /// <summary>
    /// Normalizes a path typed by the user: collapses repeated '/', removes '.' segments and
    /// resolves '..' lexically. Relative input is resolved against <paramref name="home"/>.
    /// Case is never changed.
    /// </summary>
    public static string NormalizeUserInput(string input, string home)
    {
        var text = input.Trim();
        if (text.Length == 0)
        {
            return home;
        }

        if (text == "~")
        {
            text = home;
        }
        else if (text.StartsWith("~/", StringComparison.Ordinal))
        {
            text = RemotePath.TrimTrailingSeparators(home).TrimEnd(Separator) + Separator + text[2..];
        }
        else if (!text.StartsWith(Separator))
        {
            text = (home.EndsWith(Separator) ? home : home + Separator) + text;
        }

        var stack = new List<string>();
        foreach (var segment in text.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(segment);
        }

        var sb = new StringBuilder();
        foreach (var s in stack)
        {
            sb.Append(Separator).Append(s);
        }

        return sb.Length == 0 ? "/" : sb.ToString();
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies below it (case-sensitive).</summary>
    public static bool IsWithin(string root, string path)
    {
        var r = TrimTrailingSeparators(root);
        var p = TrimTrailingSeparators(path);
        if (p == r)
        {
            return true;
        }

        var prefix = r == "/" ? "/" : r + Separator;
        return p.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Breadcrumb segments: ("/", "/"), ("home", "/home"), ...</summary>
    public static IReadOnlyList<(string Label, string Path)> Breadcrumbs(string path)
    {
        var list = new List<(string, string)> { ("/", "/") };
        var current = "";
        foreach (var segment in TrimTrailingSeparators(path).Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            current += Separator + segment;
            list.Add((segment, current));
        }

        return list;
    }
}

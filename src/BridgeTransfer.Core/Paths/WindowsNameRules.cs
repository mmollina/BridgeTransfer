namespace BridgeTransfer.Core.Paths;

/// <summary>
/// Rules for names that must be created on Windows. These are applied regardless of the OS the
/// code runs on, because the local side of this application is always Windows.
/// </summary>
public static class WindowsNameRules
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };

    private const string InvalidChars = "<>\"/\\|?*";

    public const int MaxNameLength = 255;

    /// <summary>Returns null when the name can be created on Windows, otherwise a plain-language reason.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "The name is empty.";
        }

        if (name is "." or "..")
        {
            return "'.' and '..' cannot be used as names.";
        }

        foreach (var c in name)
        {
            if (c < 0x20)
            {
                return "The name contains a control character, which Windows does not allow.";
            }
        }

        if (name.Contains(':'))
        {
            return "The name contains ':', which Windows does not allow in file names (it denotes an alternate data stream).";
        }

        var bad = name.Where(c => InvalidChars.Contains(c)).Distinct().ToArray();
        if (bad.Length > 0)
        {
            return $"The name contains characters Windows does not allow: {string.Join(" ", bad)}";
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            return "The name ends with a space or a period, which Windows removes silently.";
        }

        var stem = name;
        var dot = stem.IndexOf('.');
        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        if (ReservedNames.Contains(stem.TrimEnd(' ')))
        {
            return $"'{stem}' is a reserved device name on Windows.";
        }

        if (name.Length > MaxNameLength)
        {
            return $"The name is longer than {MaxNameLength} characters.";
        }

        return null;
    }

    public static bool IsValid(string name) => Validate(name) is null;

    /// <summary>
    /// Groups names that would collide on a case-insensitive Windows file system.
    /// Returns only groups with more than one member.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> FindCaseCollisions(IEnumerable<string> names) =>
        names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
             .Where(g => g.Count() > 1)
             .Select(g => (IReadOnlyList<string>)g.ToList())
             .ToList();
}

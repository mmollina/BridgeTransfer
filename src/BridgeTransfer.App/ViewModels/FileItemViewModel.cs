using System.IO;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Text;

namespace BridgeTransfer.App.ViewModels;

/// <summary>One row in a file list. Immutable; a refresh creates new rows.</summary>
public sealed class FileItemViewModel
{
    public FileItemViewModel(FileEntry entry, bool isDrive)
    {
        Entry = entry;
        IsDrive = isDrive;
        IconKey = isDrive ? "Icon.Drive" : entry.Kind switch
        {
            EntryKind.Directory => "Icon.Folder",
            EntryKind.SymbolicLink => "Icon.Link",
            EntryKind.Other => "Icon.Special",
            _ => "Icon.File",
        };
        SizeText = entry.Kind == EntryKind.File ? Format.Bytes(entry.Length) : "";
        TypeText = isDrive ? "Drive" : entry.Kind switch
        {
            EntryKind.Directory => "Folder",
            EntryKind.SymbolicLink => "Link",
            EntryKind.Other => "Special file",
            _ => FileType(entry.Name),
        };
        ModifiedText = Format.LocalTime(entry.ModifiedUtc);
        ToolTip = entry.Kind == EntryKind.SymbolicLink
            ? $"{entry.FullPath}\nLink{(entry.LinkTarget is null ? "" : " to " + entry.LinkTarget)}. Links are not followed when copying folders."
            : entry.FullPath;
    }

    public FileEntry Entry { get; }

    public bool IsDrive { get; }

    public string Name => Entry.Name;

    public string IconKey { get; }

    public string SizeText { get; }

    public string TypeText { get; }

    public string ModifiedText { get; }

    public string ToolTip { get; }

    public bool IsFolderLike => IsDrive || Entry.Kind == EntryKind.Directory;

    public bool IsLink => Entry.Kind == EntryKind.SymbolicLink;

    public override string ToString() => Name;

    private static string FileType(string name)
    {
        var ext = Path.GetExtension(name);
        return string.IsNullOrEmpty(ext) || ext.Length > 12 ? "File" : ext.TrimStart('.').ToUpperInvariant() + " file";
    }
}

/// <summary>Sorts like Explorer: digits compared by value ("run2" before "run10").</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length)
                {
                    return a.Length.CompareTo(b.Length);
                }

                var c = string.CompareOrdinal(a, b);
                if (c != 0)
                {
                    return c;
                }
            }
            else
            {
                var c = string.Compare(x, i, y, j, 1, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0)
                {
                    return c;
                }

                i++;
                j++;
            }
        }

        var lengthOrder = (x.Length - i).CompareTo(y.Length - j);
        return lengthOrder != 0 ? lengthOrder : string.CompareOrdinal(x, y);
    }
}

using System.Windows;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Text;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.App.Views;

/// <summary>Asks what to do with one existing destination item.</summary>
public partial class ConflictWindow
{
    private ConflictChoice _choice = ConflictChoice.Ask;

    public ConflictWindow()
    {
        InitializeComponent();
    }

    public static (ConflictChoice Choice, bool ApplyToRemaining)? Ask(ConflictRequest r, int otherWaiting)
    {
        var w = new ConflictWindow();
        var name = System.IO.Path.GetFileName(r.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        w.Heading.Text = r.Kind == ConflictKind.TypeConflict
            ? $"A folder or link named “{name}” is already at the destination"
            : $"“{name}” already exists at the destination";
        w.PathText.Text = r.DestinationPath;
        w.SourceHeader.Text = r.Direction == TransferDirection.Upload ? "New (this computer)" : "New (Linux VM)";
        w.DestHeader.Text = r.Direction == TransferDirection.Upload ? "Existing (Linux VM)" : "Existing (this computer)";
        w.SourceSize.Text = Format.Bytes(r.Source.Length);
        w.SourceTime.Text = Format.LocalTimeWithSeconds(r.Source.ModifiedUtc);
        w.DestSize.Text = r.Destination.Kind == EntryKind.File ? Format.Bytes(r.Destination.Length) : r.Destination.Kind == EntryKind.Directory ? "folder" : "link";
        w.DestTime.Text = Format.LocalTimeWithSeconds(r.Destination.ModifiedUtc);
        if (r.Kind == ConflictKind.DestinationNewer)
        {
            w.NewerBanner.Visibility = Visibility.Visible;
            w.NewerText.Text = "The existing file is newer than the one being copied. Replacing it loses the newer version.";
            w.ReplaceButton.Content = "Replace newer file";
            w.ReplaceButton.Style = (Style)w.FindResource(typeof(System.Windows.Controls.Button));
        }

        if (r.Kind == ConflictKind.TypeConflict)
        {
            w.NewerBanner.Visibility = Visibility.Visible;
            w.NewerText.Text = "A file cannot replace a folder or a link. Skip it, or keep both by copying the file under a new name.";
            w.ReplaceButton.Visibility = Visibility.Collapsed;
        }

        if (otherWaiting == 0)
        {
            w.ApplyAll.Content = "Apply to later conflicts in this transfer";
        }
        else
        {
            w.ApplyAll.Content = $"Apply to the remaining conflicts in this transfer ({otherWaiting:N0} waiting)";
        }

        w.ApplyNote.Visibility = r.Kind == ConflictKind.FileExists ? Visibility.Visible : Visibility.Collapsed;
        if (w.ShowDialog() != true || w._choice == ConflictChoice.Ask)
        {
            return null;
        }

        return (w._choice, w.ApplyAll.IsChecked == true);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Skip);

    private void OnKeepBoth(object sender, RoutedEventArgs e) => Finish(ConflictChoice.KeepBoth);

    private void OnReplace(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Replace);

    private void Finish(ConflictChoice choice)
    {
        _choice = choice;
        DialogResult = true;
    }
}

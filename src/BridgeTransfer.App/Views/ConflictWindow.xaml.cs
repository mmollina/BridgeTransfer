using System.Windows;
using System.Windows.Controls;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Text;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.App.Views;

/// <summary>Asks what to do with one existing destination item, and how widely to apply the answer.</summary>
public partial class ConflictWindow
{
    private ConflictChoice _choice = ConflictChoice.Ask;

    public ConflictWindow()
    {
        InitializeComponent();
    }

    public static (ConflictChoice Choice, ConflictScope Scope)? Ask(ConflictRequest r, int waitingInTransfer, int waitingInQueue)
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
            w.ReplaceButton.Style = (Style)w.FindResource(typeof(Button));
        }

        if (r.Kind == ConflictKind.TypeConflict)
        {
            w.NewerBanner.Visibility = Visibility.Visible;
            w.NewerText.Text = "A file cannot replace a folder or a link. Skip it, or keep both by copying the file under a new name.";
            w.ReplaceButton.Visibility = Visibility.Collapsed;
            w.RulePanel.Visibility = Visibility.Collapsed;
        }

        w.ScopeTransfer.Content = waitingInTransfer > 0
            ? $"All files in this transfer ({waitingInTransfer:N0} more waiting)"
            : "All files in this transfer";
        w.ScopeQueue.Content = waitingInQueue > waitingInTransfer
            ? $"All files in every transfer in the queue ({waitingInQueue:N0} more waiting)"
            : "All files in every transfer in the queue";
        if (w.ShowDialog() != true || w._choice == ConflictChoice.Ask)
        {
            return null;
        }

        var scope = w.ScopeBox.SelectedItem == w.ScopeQueue ? ConflictScope.WholeQueue
            : w.ScopeBox.SelectedItem == w.ScopeTransfer ? ConflictScope.ThisTransfer
            : ConflictScope.ThisFile;
        return (w._choice, scope);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Skip);

    private void OnKeepBoth(object sender, RoutedEventArgs e) => Finish(ConflictChoice.KeepBoth);

    private void OnReplace(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Replace);

    private void OnRule(object sender, RoutedEventArgs e)
    {
        if (RuleBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<ConflictChoice>(tag, out var rule))
        {
            Finish(rule);
        }
    }

    private void Finish(ConflictChoice choice)
    {
        _choice = choice;
        DialogResult = true;
    }
}

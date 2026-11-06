using System.Windows;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.App.Views;

/// <summary>Shows where a transfer will go and what to do with files that already exist.</summary>
public partial class TransferConfirmWindow
{
    public static readonly ConflictChoice[] Choices =
    {
        ConflictChoice.Ask,
        ConflictChoice.Skip,
        ConflictChoice.ReplaceIfNewer,
        ConflictChoice.ReplaceIfSizeDiffers,
        ConflictChoice.ReplaceIfNewerOrSizeDiffers,
        ConflictChoice.KeepBoth,
        ConflictChoice.Replace,
    };

    public TransferConfirmWindow()
    {
        InitializeComponent();
        ExistsBox.ItemsSource = Choices.Select(c => new ChoiceItem(c)).ToList();
    }

    public sealed record ChoiceItem(ConflictChoice Choice)
    {
        public override string ToString() => Choice switch
        {
            ConflictChoice.Replace => "Replace (asks first if the existing file is newer)",
            _ => Choice.Label(),
        };
    }

    /// <summary>Returns the chosen action for existing files, or null if cancelled.</summary>
    public static ConflictChoice? Ask(string title, string message, string startText, ConflictChoice preselected)
    {
        var w = new TransferConfirmWindow { Title = title };
        w.Heading.Text = title;
        w.Body.Text = message;
        w.StartButton.Content = startText;
        w.ExistsBox.SelectedIndex = Math.Max(0, Array.IndexOf(Choices, preselected));
        w.Loaded += (_, _) => w.StartButton.Focus();
        return w.ShowDialog() == true && w.ExistsBox.SelectedItem is ChoiceItem item ? item.Choice : null;
    }

    private void OnStart(object sender, RoutedEventArgs e) => DialogResult = true;
}

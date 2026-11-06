using System.Windows.Input;
using BridgeTransfer.App.ViewModels;

namespace BridgeTransfer.App.Views;

public partial class CompareWindow
{
    public CompareWindow(CompareViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closing += (_, _) => vm.CancelScanCommand.Execute(null);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    // This window is not modal, so a cancel button would not close it on its own.
    private void OnClose(object sender, System.Windows.RoutedEventArgs e) => Close();

    /// <summary>Space toggles the checkbox of the selected rows for keyboard users.</summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space)
        {
            return;
        }

        var rows = RowsGrid.SelectedItems.OfType<ComparisonRowViewModel>().Where(r => r.IsSelectable).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var value = !rows.All(r => r.IsSelected);
        foreach (var r in rows)
        {
            r.IsSelected = value;
        }

        e.Handled = true;
    }
}

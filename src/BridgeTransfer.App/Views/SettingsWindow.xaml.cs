using System.Diagnostics;
using System.Windows;
using BridgeTransfer.App.Services;
using BridgeTransfer.Core.Persistence;

namespace BridgeTransfer.App.Views;

public partial class SettingsWindow
{
    private readonly AppServices _services;

    private SettingsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        var s = services.Settings;
        LightRadio.IsChecked = s.Theme == AppTheme.Light;
        DarkRadio.IsChecked = s.Theme == AppTheme.Dark;
        ConcurrencyBox.ItemsSource = Enumerable.Range(1, BridgeTransfer.Core.Transfers.TransferQueue.MaxAllowedConcurrency).Select(n => n == 2 ? "2 (default)" : n.ToString()).ToList();
        ConcurrencyBox.SelectedIndex = Math.Clamp(s.MaxConcurrentTransfers, 1, BridgeTransfer.Core.Transfers.TransferQueue.MaxAllowedConcurrency) - 1;
        ExistsBox.ItemsSource = TransferConfirmWindow.Choices.Select(c => new TransferConfirmWindow.ChoiceItem(c)).ToList();
        ExistsBox.SelectedIndex = Math.Max(0, Array.IndexOf(TransferConfirmWindow.Choices, s.WhenFileExists));
        TimestampsBox.IsChecked = s.PreserveTimestamps;
        ConfirmBox.IsChecked = s.ConfirmBeforeTransfer;
        ConnectTimeoutBox.Text = s.ConnectTimeoutSeconds.ToString();
        OperationTimeoutBox.Text = s.OperationTimeoutSeconds.ToString();
        KeepAliveBox.Text = s.KeepAliveSeconds.ToString();
        DataPath.Text = "Settings, trusted host keys and logs are stored in " + services.Paths.DataDirectory;
    }

    public static bool Edit(AppServices services) => new SettingsWindow(services).ShowDialog() == true;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ConnectTimeoutBox.Text, out var ct) || ct is < 5 or > 300 ||
            !int.TryParse(OperationTimeoutBox.Text, out var ot) || ot is < 10 or > 600 ||
            !int.TryParse(KeepAliveBox.Text, out var ka) || ka is < 0 or > 600)
        {
            Problem.Text = "Timeouts: connection 5–300 s, operation 10–600 s, keepalive 0–600 s.";
            Problem.Visibility = Visibility.Visible;
            return;
        }

        var s = _services.Settings;
        s.Theme = DarkRadio.IsChecked == true ? AppTheme.Dark : AppTheme.Light;
        s.MaxConcurrentTransfers = ConcurrencyBox.SelectedIndex + 1;
        if (ExistsBox.SelectedItem is TransferConfirmWindow.ChoiceItem item)
        {
            s.WhenFileExists = item.Choice;
        }
        s.PreserveTimestamps = TimestampsBox.IsChecked == true;
        s.ConfirmBeforeTransfer = ConfirmBox.IsChecked == true;
        s.ConnectTimeoutSeconds = ct;
        s.OperationTimeoutSeconds = ot;
        s.KeepAliveSeconds = ka;
        _services.SaveSettings();
        DialogResult = true;
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.Paths.DataDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    private void OnExportLog(object sender, RoutedEventArgs e)
    {
        var proceed = MessageWindow.Show(
            "Export support log",
            "The log is saved as a zip file where you choose. Nothing is uploaded.\n\nPasswords and passphrases are removed from the log, and file contents and private keys are never written to it. File paths, folder names and host names may still appear, so review the file before sharing it.",
            MessageKind.Info,
            null,
            ("Cancel", false, false, false, true),
            ("Choose location…", true, true, true, false));
        if (proceed is not true)
        {
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"BridgeTransfer-log-{DateTime.Now:yyyyMMdd-HHmm}.zip", Filter = "Zip file (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _services.Log.Export(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageWindow.Show("Export failed", ex.Message, MessageKind.Error, null, ("Close", true, true, true, true));
            }
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow().ShowDialog();
}

using System.Windows;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Infrastructure.Sftp;

namespace BridgeTransfer.App.Views;

/// <summary>Shows a server's host key for first-time trust, or explains a blocked, changed key.</summary>
public partial class HostKeyWindow
{
    public HostKeyWindow()
    {
        InitializeComponent();
    }

    public static bool ConfirmUnknown(PresentedHostKey key)
    {
        var w = new HostKeyWindow();
        w.Intro.Text = "BridgeTransfer has not connected to this server before. Before any login details are sent, confirm that the fingerprint below belongs to your VM.";
        w.ServerText.Text = $"{key.Host}  (port {key.Port})";
        w.AlgorithmText.Text = key.Algorithm;
        w.FingerprintText.Text = key.DisplayFingerprint;
        w.Advice.Text = "To check, run this on the VM and compare the SHA256 value:\n    ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub\nIf they do not match, select Cancel.";
        return w.ShowDialog() == true;
    }

    public static void ShowChanged(HostKeyChangedException ex)
    {
        var w = new HostKeyWindow { Title = "Server identity changed" };
        var c = ex.Check;
        w.Heading.Text = "The server's identity has changed";
        w.Badge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.DangerSoft");
        w.BadgeGlyph.SetResourceReference(Controls.Glyph.ForegroundProperty, "Brush.Danger");
        w.BadgeGlyph.Data = (System.Windows.Media.Geometry)w.FindResource("Icon.Warning");
        w.Intro.Text = "The connection was blocked and no login details were sent. The key presented now differs from the one you trusted. This happens when the VM was reinstalled or its SSH keys were regenerated, but it can also mean the connection is being intercepted.";
        w.ServerText.Text = $"{c.Presented.Host}  (port {c.Presented.Port})";
        w.AlgorithmText.Text = c.Presented.Algorithm;
        w.FingerprintLabel.Text = "Presented now";
        w.FingerprintText.Text = c.Presented.DisplayFingerprint;
        w.OldLabel.Visibility = Visibility.Visible;
        w.OldText.Visibility = Visibility.Visible;
        w.OldText.Text = string.Join("\n", c.TrustedKeys.Select(k => $"{k.Algorithm}  {k.DisplayFingerprint}  (trusted {k.TrustedUtc.ToLocalTime():yyyy-MM-dd})"));
        w.Advice.Text = "If you know why the key changed, open the connection settings and select “Forget trusted key”, then connect again and verify the new fingerprint.";
        w.TrustButton.Visibility = Visibility.Collapsed;
        w.CancelButton.Content = "Close";
        w.ShowDialog();
    }

    private void OnTrust(object sender, RoutedEventArgs e) => DialogResult = true;
}

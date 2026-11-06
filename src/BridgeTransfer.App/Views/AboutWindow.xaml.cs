using BridgeTransfer.Core;

namespace BridgeTransfer.App.Views;

public partial class AboutWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        NameText.Text = $"{AppInfo.ProductName} {AppInfo.Version}";
        using var stream = typeof(AboutWindow).Assembly.GetManifestResourceStream("BridgeTransfer.THIRD-PARTY-NOTICES.txt");
        Notices.Text = stream is null ? "Notices are not available." : new System.IO.StreamReader(stream).ReadToEnd();
    }
}

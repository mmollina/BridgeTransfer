using System.Windows;
using System.Windows.Media;
using BridgeTransfer.App.Services;

namespace BridgeTransfer.App.Controls;

/// <summary>Base window: theme background, standard font, dark title bar in dark mode.</summary>
public class ThemedWindow : Window
{
    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        if (Application.Current?.MainWindow is { } main && !ReferenceEquals(main, this) && main.IsLoaded)
        {
            Owner = main;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        ShowInTaskbar = Owner is null;
    }
}

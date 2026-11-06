using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BridgeTransfer.Core.Persistence;

namespace BridgeTransfer.App.Services;

/// <summary>Switches between the light and dark resource dictionaries and matches the window title bar.</summary>
public static partial class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static event Action? Changed;

    public static void Apply(AppTheme mode)
    {
        Current = mode;
        var dict = new ResourceDictionary
        {
            Source = new Uri(mode == AppTheme.Dark ? "pack://application:,,,/BridgeTransfer;component/Themes/Dark.xaml" : "pack://application:,,,/BridgeTransfer;component/Themes/Light.xaml"),
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        merged[0] = dict;
        foreach (Window w in Application.Current.Windows)
        {
            ApplyTitleBar(w);
        }

        Changed?.Invoke();
    }

    /// <summary>Uses the dark window caption on Windows 10 (20H1+) and 11 when the dark theme is active.</summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985))
            {
                return;
            }

            var value = Current == AppTheme.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

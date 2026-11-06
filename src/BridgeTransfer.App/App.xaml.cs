using System.Windows;
using System.Windows.Threading;
using BridgeTransfer.App.Services;
using BridgeTransfer.App.ViewModels;
using BridgeTransfer.App.Views;
using BridgeTransfer.Core;

namespace BridgeTransfer.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance(AppInfo.ProductName);
        if (!_instance.IsFirstInstance)
        {
            // One instance per user, so only one process ever works on the persisted queue.
            _instance.SignalFirstInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _services?.Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            _services = new AppServices();
        }
        catch (Exception ex)
        {
            MessageBox.Show("BridgeTransfer could not start: " + ex.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ThemeManager.Apply(_services.Settings.Theme);
        var dialogs = new DialogService(m => _services.Log.Warn(m));
        var vm = new MainViewModel(_services, dialogs);
        var window = new MainWindow(vm);
        MainWindow = window;
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(() =>
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        }));
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Error("Unhandled UI exception", e.Exception);
        var (message, details) = ErrorText.Describe(e.Exception);
        try
        {
            MessageWindow.Show("Something went wrong", message + "\n\nYour transfers continue. If this keeps happening, export the support log from Settings.", MessageKind.Error, details, ("Close", true, true, true, true));
        }
        catch
        {
        }

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}

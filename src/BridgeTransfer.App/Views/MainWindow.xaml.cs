using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BridgeTransfer.App.Services;
using BridgeTransfer.App.ViewModels;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Persistence;

namespace BridgeTransfer.App.Views;

public partial class MainWindow
{
    private readonly MainViewModel _vm;
    private bool _closingConfirmed;
    private GridLength _queueHeight = new(200);
    private GridLength _sidebarWidth = new(212);

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        LocalView.Activated += () => vm.ActivePanel = vm.Local;
        RemoteView.Activated += () => vm.ActivePanel = vm.Remote;
        LocalView.TransferRequested += r => _ = vm.StartTransferAsync(r.Direction, r.Entries, r.SourceDirectory, r.DestinationDirectory);
        RemoteView.TransferRequested += r => _ = vm.StartTransferAsync(r.Direction, r.Entries, r.SourceDirectory, r.DestinationDirectory);
        LocalView.TransferSelectionRequested += () => vm.UploadCommand.Execute(null);
        RemoteView.TransferSelectionRequested += () => vm.DownloadCommand.Execute(null);
        RemoteView.ConnectRequested += () => vm.ConnectCommand.Execute(null);

        vm.PropertyChanged += OnVmChanged;
        ThemeManager.Changed += UpdateThemeGlyph;
        UpdateThemeGlyph();
        ApplySidebar();
        ApplyQueue();
        UpdateStatusPill();

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Closing += OnClosing;
        AdaptToScreen();
    }

    /// <summary>Starts at about 1280 x 820 but never larger than the work area (for example 1366 x 768 screens).</summary>
    private void AdaptToScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 24);
        Height = Math.Min(Height, area.Height - 24);
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        if (area.Height < 800)
        {
            // Short screens (for example 1366 x 768, or 1920 x 1080 at 150%): give the file lists more room.
            _queueHeight = new GridLength(area.Height < 740 ? 150 : 170);
            ApplyQueue();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeAsync();
        LocalView.FocusList();
        if (_vm.Services.Interrupted is { Jobs.Count: > 0 })
        {
            new InterruptedJobsWindow(_vm.Services, _vm).ShowDialog();
            _vm.RefreshInterrupted();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SidebarCollapsed):
                ApplySidebar();
                break;
            case nameof(MainViewModel.QueueCollapsed):
                ApplyQueue();
                break;
            case nameof(MainViewModel.StatusTone):
                UpdateStatusPill();
                break;
        }
    }

    private void ApplySidebar()
    {
        if (_vm.SidebarCollapsed)
        {
            if (SidebarColumn.ActualWidth > 0)
            {
                _sidebarWidth = SidebarColumn.Width;
            }

            SidebarColumn.Width = new GridLength(0);
            SidebarSplitColumn.Width = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarColumn.Width = _sidebarWidth.Value > 60 ? _sidebarWidth : new GridLength(212);
            SidebarSplitColumn.Width = new GridLength(12);
            Sidebar.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }
    }

    private void ApplyQueue()
    {
        if (_vm.QueueCollapsed)
        {
            if (QueueRow.ActualHeight > 60)
            {
                _queueHeight = new GridLength(QueueRow.ActualHeight);
            }

            QueueBody.Visibility = Visibility.Collapsed;
            QueueRow.Height = GridLength.Auto;
            QueueSplitter.IsEnabled = false;
            QueueChevron.Data = (Geometry)FindResource("Icon.ChevronUp");
        }
        else
        {
            QueueBody.Visibility = Visibility.Visible;
            QueueRow.Height = _queueHeight;
            QueueRow.MinHeight = 120;
            QueueSplitter.IsEnabled = true;
            QueueChevron.Data = (Geometry)FindResource("Icon.ChevronDown");
        }
    }

    private void UpdateStatusPill()
    {
        var (dot, bg) = _vm.StatusTone switch
        {
            "ok" => ("Brush.Success", "Brush.SuccessSoft"),
            "warn" => ("Brush.Warning", "Brush.WarningSoft"),
            "bad" => ("Brush.Danger", "Brush.DangerSoft"),
            _ => ("Brush.TextMuted", "Brush.SurfaceHover"),
        };
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
        StatusPill.SetResourceReference(Border.BackgroundProperty, bg);
    }

    private void UpdateThemeGlyph() =>
        ThemeGlyph.Data = (Geometry)FindResource(ThemeManager.Current == AppTheme.Dark ? "Icon.Sun" : "Icon.Moon");

    private FilePanelView ActiveView => ReferenceEquals(_vm.ActivePanel, _vm.Remote) ? RemoteView : LocalView;

    /// <summary>Keyboard shortcuts for the active panel. None of them deletes anything.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var panel = _vm.ActivePanel;
        var inTextBox = Keyboard.FocusedElement is TextBox;
        switch (key)
        {
            case Key.F5:
                panel.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2 when !inTextBox:
                panel.RenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.L when Keyboard.Modifiers == ModifierKeys.Control:
                ActiveView.FocusPath();
                e.Handled = true;
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                ActiveView.FocusFilter();
                e.Handled = true;
                break;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.Alt:
                panel.UpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Alt:
                panel.BackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Alt:
                panel.ForwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.Control:
                (ReferenceEquals(panel, _vm.Local) ? RemoteView : LocalView).FocusList();
                e.Handled = true;
                break;
        }
    }

    private void OnProfileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ProfileList, d) is ListBoxItem { DataContext: ConnectionProfile p })
        {
            _vm.ConnectCommand.Execute(p);
        }
    }

    private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.Queue.SelectedJobs = QueueGrid.SelectedItems.OfType<JobViewModel>().ToList();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed)
        {
            return;
        }

        if (_vm.Services.Queue.HasActiveWork)
        {
            var answer = MessageWindow.Show(
                "Transfers are still running",
                "If you exit now, running and queued transfers are cancelled. Partly copied files are removed from their temporary names, and existing files at the destination are left unchanged. You can start the transfers again later.",
                MessageKind.Warning,
                null,
                ("Keep open", false, false, true, true),
                ("Cancel transfers and exit", true, false, false, false));
            if (answer is not true)
            {
                e.Cancel = true;
                return;
            }
        }

        e.Cancel = true;
        _closingConfirmed = true;
        IsEnabled = false;
        Title = "Closing…";
        _vm.SaveWindowState();
        _vm.Services.Queue.CancelAll();
        try
        {
            await _vm.Services.DisposeAsync();
        }
        catch (Exception ex)
        {
            _vm.Services.Log.Error("Error while closing", ex);
        }

        Application.Current.Shutdown();
    }
}

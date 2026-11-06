using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BridgeTransfer.App.ViewModels;
using BridgeTransfer.Core.Models;

namespace BridgeTransfer.App.Views;

/// <summary>A dropped or dragged set of items to transfer into a folder.</summary>
public sealed record TransferRequest(TransferDirection Direction, IReadOnlyList<FileEntry> Entries, string SourceDirectory, string DestinationDirectory);

public partial class FilePanelView : UserControl
{
    private const string InternalFormat = "BridgeTransfer.PanelItems";
    private Point _dragStart;
    private bool _dragCandidate;

    public FilePanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewGotKeyboardFocus += (_, _) => Activated?.Invoke();
        PreviewMouseDown += (_, _) => Activated?.Invoke();
    }

    public event Action? Activated;

    public event Action<TransferRequest>? TransferRequested;

    public event Action? ConnectRequested;

    /// <summary>Raised for the context-menu "Transfer" item (upload from local, download from remote).</summary>
    public event Action? TransferSelectionRequested;

    private FilePanelViewModel? Vm => DataContext as FilePanelViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FilePanelViewModel old)
        {
            old.PropertyChanged -= OnVmChanged;
            old.SelectRequested -= SelectByName;
        }

        if (Vm is { } vm)
        {
            vm.PropertyChanged += OnVmChanged;
            vm.SelectRequested += SelectByName;
            var remote = vm.Side == EndpointSide.Remote;
            SideGlyph.Data = (Geometry)FindResource(remote ? "Icon.Server" : "Icon.Computer");
            OpenButton.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            TransferMenu.Header = remote ? "Download to this computer" : "Upload to the Linux VM";
            UnavailableConnect.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            UpdateActive();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FilePanelViewModel.IsActive):
                UpdateActive();
                break;
            case nameof(FilePanelViewModel.Crumbs):
                Dispatcher.BeginInvoke(() => CrumbScroller.ScrollToRightEnd());
                break;
            case nameof(FilePanelViewModel.PathText) when !PathBox.IsKeyboardFocused:
                // Show the end of long paths (the current folder), not the drive letter.
                Dispatcher.BeginInvoke(() =>
                {
                    PathBox.CaretIndex = PathBox.Text.Length;
                    PathBox.ScrollToHorizontalOffset(double.MaxValue);
                }, System.Windows.Threading.DispatcherPriority.Background);
                break;
            case nameof(FilePanelViewModel.Items):
                UpdateSortGlyphs();
                EmptyText.Text = Vm?.FilterText.Length > 0 ? "No items match the filter" : "This folder is empty";
                break;
        }
    }

    private void UpdateActive()
    {
        var active = Vm?.IsActive == true;
        ActiveBar.SetResourceReference(Border.BackgroundProperty, active ? "Brush.Accent" : "Brush.Surface");
        Frame.SetResourceReference(Border.BorderBrushProperty, active ? "Brush.Accent" : "Brush.Border");
        TitleText.SetResourceReference(TextBlock.ForegroundProperty, active ? "Brush.AccentForeground" : "Brush.Text");
        ActivePill.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    public void FocusList()
    {
        if (Grid.Items.Count > 0 && Grid.SelectedIndex < 0)
        {
            Grid.SelectedIndex = 0;
        }

        Grid.Focus();
        if (Grid.SelectedItem is not null)
        {
            Grid.ScrollIntoView(Grid.SelectedItem);
            if (Grid.ItemContainerGenerator.ContainerFromItem(Grid.SelectedItem) is DataGridRow row)
            {
                row.Focus();
            }
        }
    }

    public void FocusPath()
    {
        PathBox.Focus();
        PathBox.SelectAll();
    }

    public void FocusFilter() => FilterBox.Focus();

    private void SelectByName(string name)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var item = Grid.Items.OfType<FileItemViewModel>().FirstOrDefault(i => i.Name == name);
            if (item is not null)
            {
                Grid.SelectedItem = item;
                Grid.ScrollIntoView(item);
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm?.GoToPathTextCommand.Execute(null);
            FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Vm is { } vm)
        {
            vm.PathText = vm.Source.DisplayPath(vm.CurrentPath);
            FocusList();
            e.Handled = true;
        }
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (Vm is null)
        {
            return;
        }

        var column = e.Column.SortMemberPath switch
        {
            "Size" => SortColumn.Size,
            "Type" => SortColumn.Type,
            "Modified" => SortColumn.Modified,
            _ => SortColumn.Name,
        };
        Vm.ApplySort(column);
    }

    private void UpdateSortGlyphs()
    {
        if (Vm is null)
        {
            return;
        }

        var key = Vm.SortColumn.ToString();
        foreach (var c in Grid.Columns)
        {
            c.SortDirection = c.SortMemberPath == key ? (Vm.SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending) : null;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm?.UpdateSelection(Grid.SelectedItems.OfType<FileItemViewModel>().ToList());

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<DataGridRow>(d) is { Item: FileItemViewModel item })
        {
            Vm?.OpenItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (e.Key == Key.Enter && Grid.SelectedItems.Count == 1 && Grid.SelectedItem is FileItemViewModel item)
        {
            Vm.OpenItemCommand.Execute(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            Vm.UpCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnContextOpen(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is FileItemViewModel item)
        {
            Vm?.OpenItemCommand.Execute(item);
        }
    }

    private void OnContextTransfer(object sender, RoutedEventArgs e) => TransferSelectionRequested?.Invoke();

    private void OnUnavailableConnect(object sender, RoutedEventArgs e) => ConnectRequested?.Invoke();

    // ---------------- Drag and drop ----------------
    private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Grid);
        _dragCandidate = e.OriginalSource is DependencyObject d && FindAncestor<DataGridRow>(d) is { IsSelected: true } && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(d) is null;
    }

    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed || Vm is null)
        {
            return;
        }

        var pos = e.GetPosition(Grid);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragCandidate = false;
        var entries = Vm.SelectedEntries;
        if (entries.Count == 0)
        {
            return;
        }

        // Internal format only: remote items are never offered to Explorer as files.
        var data = new DataObject();
        data.SetData(InternalFormat, new PanelDragData(Vm.Side, entries, Vm.CurrentPath));
        DragDrop.DoDragDrop(Grid, data, DragDropEffects.Copy);
    }

    private sealed record PanelDragData(EndpointSide Side, IReadOnlyList<FileEntry> Entries, string SourceDirectory);

    private bool TryGetDrop(DragEventArgs e, out TransferDirection direction, out string text)
    {
        direction = TransferDirection.Upload;
        text = "";
        if (Vm is null || Vm.IsUnavailable || !Vm.CanWriteHere)
        {
            return false;
        }

        if (e.Data.GetDataPresent(InternalFormat) && e.Data.GetData(InternalFormat) is PanelDragData p)
        {
            if (p.Side == Vm.Side)
            {
                return false; // Moving within one side is not offered.
            }

            direction = Vm.Side == EndpointSide.Remote ? TransferDirection.Upload : TransferDirection.Download;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop) && Vm.Side == EndpointSide.Remote)
        {
            direction = TransferDirection.Upload;
        }
        else
        {
            return false;
        }

        var target = DropTarget(e) ?? Vm.CurrentPath;
        text = (direction == TransferDirection.Upload ? "Upload to the Linux VM\n" : "Download to this computer\n") + target;
        return true;
    }

    private string? DropTarget(DragEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<DataGridRow>(d) is { Item: FileItemViewModel { Entry.Kind: EntryKind.Directory } item })
        {
            return item.Entry.FullPath;
        }

        return null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (TryGetDrop(e, out _, out var text))
        {
            e.Effects = DragDropEffects.Copy;
            DropText.Text = text;
            DropHint.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropHint.Visibility = Visibility.Collapsed;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (Vm is null || !TryGetDrop(e, out var direction, out _))
        {
            return;
        }

        var destination = DropTarget(e) ?? Vm.CurrentPath;
        if (e.Data.GetData(InternalFormat) is PanelDragData p)
        {
            TransferRequested?.Invoke(new TransferRequest(direction, p.Entries, p.SourceDirectory, destination));
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            var store = new Infrastructure.Local.LocalFileStore();
            var entries = new List<FileEntry>();
            foreach (var path in paths)
            {
                if (await store.GetEntryAsync(path, CancellationToken.None) is { } entry)
                {
                    entries.Add(entry);
                }
            }

            var sourceDir = Path.GetDirectoryName(paths[0]) ?? paths[0];
            if (entries.Count > 0)
            {
                TransferRequested?.Invoke(new TransferRequest(TransferDirection.Upload, entries, sourceDir, destination));
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T t)
            {
                return t;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}

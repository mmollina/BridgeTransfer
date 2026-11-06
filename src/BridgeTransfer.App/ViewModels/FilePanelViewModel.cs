using System.Diagnostics;
using System.Windows;
using BridgeTransfer.App.Services;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BridgeTransfer.App.ViewModels;

public enum SortColumn
{
    Name,
    Size,
    Type,
    Modified,
}

/// <summary>State and commands for one file panel ("This computer" or "Linux VM").</summary>
public sealed partial class FilePanelViewModel : ObservableObject
{
    private readonly IPanelSource _source;
    private readonly IDialogService _dialogs;
    private readonly Action<string> _log;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private IReadOnlyList<FileEntry> _entries = Array.Empty<FileEntry>();
    private CancellationTokenSource? _loadCts;
    private long _navigationSequence;

    public FilePanelViewModel(IPanelSource source, IDialogService dialogs, string title, Action<string> log)
    {
        _source = source;
        _dialogs = dialogs;
        _log = log;
        Title = title;
    }

    public IPanelSource Source => _source;

    public EndpointSide Side => _source.Side;

    public string Title { get; }

    /// <summary>Raised when a remote operation shows the connection has dropped.</summary>
    public event Action<string>? ConnectionProblem;

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _currentPath = "";

    [ObservableProperty]
    private string _pathText = "";

    [ObservableProperty]
    private IReadOnlyList<Crumb> _crumbs = Array.Empty<Crumb>();

    [ObservableProperty]
    private IReadOnlyList<FileItemViewModel> _items = Array.Empty<FileItemViewModel>();

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorDetails;

    [ObservableProperty]
    private bool _isUnavailable;

    [ObservableProperty]
    private string _unavailableText = "";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private SortColumn _sortColumn = SortColumn.Name;

    [ObservableProperty]
    private bool _sortAscending = true;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _selectionText = "";

    [ObservableProperty]
    private IReadOnlyList<FileItemViewModel> _selectedItems = Array.Empty<FileItemViewModel>();

    public bool HasLoaded { get; private set; }

    public bool IsEmptyFolder => !IsLoading && ErrorMessage is null && !IsUnavailable && HasLoaded && Items.Count == 0;

    public bool CanWriteHere => !IsUnavailable && ErrorMessage is null && _source.CanWriteIn(CurrentPath) && HasLoaded;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp => _source.Parent(CurrentPath) is not null && !IsUnavailable;

    partial void OnFilterTextChanged(string value) => Rebuild();

    partial void OnShowHiddenChanged(bool value) => Rebuild();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmptyFolder));

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(IsEmptyFolder));
        OnPropertyChanged(nameof(CanWriteHere));
    }

    partial void OnIsUnavailableChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmptyFolder));
        OnPropertyChanged(nameof(CanWriteHere));
        OnPropertyChanged(nameof(CanGoUp));
    }

    partial void OnItemsChanged(IReadOnlyList<FileItemViewModel> value) => OnPropertyChanged(nameof(IsEmptyFolder));

    public void SetUnavailable(string text)
    {
        _loadCts?.Cancel();
        IsUnavailable = true;
        UnavailableText = text;
        IsLoading = false;
        HasLoaded = false;
        ErrorMessage = null;
        _entries = Array.Empty<FileEntry>();
        Items = Array.Empty<FileItemViewModel>();
        SelectedItems = Array.Empty<FileItemViewModel>();
        CountText = "";
        SelectionText = "";
        RaiseNavState();
    }

    public void ClearHistory()
    {
        _back.Clear();
        _forward.Clear();
        RaiseNavState();
    }

    /// <summary>
    /// Lists <paramref name="path"/>. Results from an older request never overwrite a newer one, and a
    /// failure shows an error state rather than an empty folder.
    /// </summary>
    public async Task NavigateAsync(string path, bool recordHistory = true)
    {
        if (!_source.IsAvailable)
        {
            return;
        }

        IsUnavailable = false;
        var sequence = Interlocked.Increment(ref _navigationSequence);
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var previous = CurrentPath;
        IsLoading = true;
        PathText = _source.DisplayPath(path);
        try
        {
            var entries = await _source.ListAsync(path, cts.Token);
            if (sequence != Interlocked.Read(ref _navigationSequence))
            {
                return;
            }

            if (recordHistory && HasLoaded && previous != path)
            {
                _back.Push(previous);
                _forward.Clear();
            }

            CurrentPath = path;
            _entries = entries;
            ErrorMessage = null;
            ErrorDetails = null;
            HasLoaded = true;
            await RebuildAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded by a newer navigation.
        }
        catch (Exception ex)
        {
            if (sequence != Interlocked.Read(ref _navigationSequence))
            {
                return;
            }

            if (recordHistory && HasLoaded && previous != path)
            {
                _back.Push(previous);
                _forward.Clear();
            }

            CurrentPath = path;
            _entries = Array.Empty<FileEntry>();
            Items = Array.Empty<FileItemViewModel>();
            HasLoaded = true;
            var (message, details) = ErrorText.Describe(ex);
            ErrorMessage = $"This folder could not be listed. {message}";
            ErrorDetails = details;
            CountText = "Folder not listed";
            _log($"Listing {path} failed: {details}");
            if (ex is StoreException { Kind: FailureKind.Transient } && Side == EndpointSide.Remote)
            {
                ConnectionProblem?.Invoke(message);
            }
        }
        finally
        {
            if (sequence == Interlocked.Read(ref _navigationSequence))
            {
                IsLoading = false;
                Crumbs = _source.Breadcrumbs(CurrentPath);
                PathText = _source.DisplayPath(CurrentPath);
                Subtitle = _source.DisplayPath(CurrentPath);
                RaiseNavState();
            }
        }
    }

    private void RaiseNavState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanWriteHere));
        OnPropertyChanged(nameof(IsEmptyFolder));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        UpCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
    }

    private void Rebuild() => _ = RebuildAsync();

    private async Task RebuildAsync()
    {
        var entries = _entries;
        var filter = FilterText.Trim();
        var showHidden = ShowHidden;
        var column = SortColumn;
        var ascending = SortAscending;
        var isDriveList = Side == EndpointSide.Local && CurrentPath == LocalPanelSource.ComputerRoot;
        var sequence = Interlocked.Read(ref _navigationSequence);

        // Sorting and filtering large folders happens off the UI thread.
        var items = await Task.Run(() =>
        {
            IEnumerable<FileEntry> query = entries;
            if (!showHidden)
            {
                query = query.Where(e => !e.IsHidden);
            }

            if (filter.Length > 0)
            {
                query = query.Where(e => e.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
            }

            var list = query.Select(e => new FileItemViewModel(e, isDriveList)).ToList();
            Comparison<FileItemViewModel> cmp = column switch
            {
                SortColumn.Size => (a, b) => a.Entry.Length.CompareTo(b.Entry.Length),
                SortColumn.Type => (a, b) => string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCultureIgnoreCase),
                SortColumn.Modified => (a, b) => Nullable.Compare(a.Entry.ModifiedUtc, b.Entry.ModifiedUtc),
                _ => (a, b) => 0,
            };
            list.Sort((a, b) =>
            {
                var folders = b.IsFolderLike.CompareTo(a.IsFolderLike);
                if (folders != 0)
                {
                    return folders;
                }

                var c = cmp(a, b);
                if (c == 0)
                {
                    c = NaturalStringComparer.Instance.Compare(a.Name, b.Name);
                }

                return ascending ? c : -c;
            });
            return list;
        });

        if (sequence != Interlocked.Read(ref _navigationSequence))
        {
            return;
        }

        Items = items;
        var hiddenCount = showHidden ? 0 : entries.Count(e => e.IsHidden);
        var text = Format.Count(items.Count, "item");
        if (filter.Length > 0)
        {
            text = $"{Format.Count(items.Count, "match", "matches")} for “{filter}” in this folder";
        }

        if (hiddenCount > 0)
        {
            text += $" ({hiddenCount:N0} hidden)";
        }

        CountText = text;
        UpdateSelection(Array.Empty<FileItemViewModel>());
    }

    public void ApplySort(SortColumn column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            SortAscending = true;
        }

        Rebuild();
    }

    public void UpdateSelection(IReadOnlyList<FileItemViewModel> selected)
    {
        SelectedItems = selected;
        if (selected.Count == 0)
        {
            SelectionText = "";
        }
        else
        {
            var files = selected.Where(s => s.Entry.Kind == EntryKind.File).ToList();
            var folders = selected.Count(s => s.IsFolderLike);
            var bytes = files.Sum(f => f.Entry.Length);
            var parts = new List<string> { $"{selected.Count:N0} selected" };
            if (files.Count > 0)
            {
                parts.Add(Format.Bytes(bytes) + (folders > 0 ? " in files" : ""));
            }

            if (folders > 0)
            {
                parts.Add(Format.Count(folders, "folder") + " (size not counted until transfer)");
            }

            SelectionText = string.Join(" · ", parts);
        }

        RenameCommand.NotifyCanExecuteChanged();
        CopyPathCommand.NotifyCanExecuteChanged();
        OpenSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Entries suitable for transfer (no drive roots).</summary>
    public IReadOnlyList<FileEntry> SelectedEntries => SelectedItems.Where(i => !i.IsDrive).Select(i => i.Entry).ToList();

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackAsync()
    {
        if (_back.Count == 0)
        {
            return;
        }

        var target = _back.Pop();
        _forward.Push(CurrentPath);
        await NavigateAsync(target, recordHistory: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private async Task ForwardAsync()
    {
        if (_forward.Count == 0)
        {
            return;
        }

        var target = _forward.Pop();
        _back.Push(CurrentPath);
        await NavigateAsync(target, recordHistory: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private async Task UpAsync()
    {
        if (_source.Parent(CurrentPath) is { } parent)
        {
            await NavigateAsync(parent);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_source.IsAvailable)
        {
            await NavigateAsync(CurrentPath, recordHistory: false);
        }
    }

    [RelayCommand]
    private async Task GoToPathTextAsync()
    {
        string target;
        try
        {
            target = _source.NormalizeInput(PathText, CurrentPath);
        }
        catch (ArgumentException ex)
        {
            _dialogs.ShowError("Cannot open this path", ex.Message);
            PathText = _source.DisplayPath(CurrentPath);
            return;
        }

        await NavigateAsync(target);
    }

    [RelayCommand]
    private async Task GoToCrumbAsync(Crumb? crumb)
    {
        if (crumb is not null)
        {
            await NavigateAsync(crumb.Path);
        }
    }

    [RelayCommand]
    private async Task GoHomeAsync() => await NavigateAsync(_source.HomePath);

    /// <summary>Double-click / Enter: folders are opened; local files open in their Windows application; remote files are never executed.</summary>
    [RelayCommand]
    private async Task OpenItemAsync(FileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsFolderLike || (item.IsLink && Side == EndpointSide.Remote))
        {
            await NavigateAsync(item.Entry.FullPath);
            return;
        }

        if (Side == EndpointSide.Local)
        {
            ShellOpen(item.Entry.FullPath);
        }
    }

    private bool HasSingleLocalSelection() => Side == EndpointSide.Local && SelectedItems.Count == 1;

    [RelayCommand(CanExecute = nameof(HasSingleLocalSelection))]
    private void OpenSelected()
    {
        if (SelectedItems.FirstOrDefault() is { } item)
        {
            ShellOpen(item.Entry.FullPath);
        }
    }

    private void ShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Could not open the item", ex.Message);
        }
    }

    private bool CanCreateFolder() => CanWriteHere;

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private async Task NewFolderAsync()
    {
        var name = _dialogs.PromptText("New folder", $"Name of the new folder in {_source.DisplayPath(CurrentPath)}", "New folder", ValidateNewName, "Create");
        if (name is null)
        {
            return;
        }

        try
        {
            await _source.CreateFolderAsync(_source.Combine(CurrentPath, name), CancellationToken.None);
            await NavigateAsync(CurrentPath, recordHistory: false);
            Select(name);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("The folder was not created", ex);
        }
    }

    private string? ValidateNewName(string name)
    {
        if (_source.ValidateName(name) is { } problem)
        {
            return problem;
        }

        var exists = Side == EndpointSide.Local
            ? _entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            : _entries.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        return exists ? "An item with this name already exists here." : null;
    }

    private bool HasSingleSelection() => SelectedItems.Count == 1 && !SelectedItems[0].IsDrive && !IsUnavailable;

    /// <summary>Renames without ever overwriting another item.</summary>
    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private async Task RenameAsync()
    {
        var item = SelectedItems.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var oldName = item.Name;
        var newName = _dialogs.PromptText("Rename", $"New name for “{oldName}”", oldName, n =>
        {
            if (n == oldName)
            {
                return "Enter a different name.";
            }

            if (_source.ValidateName(n) is { } problem)
            {
                return problem;
            }

            var caseOnly = Side == EndpointSide.Local && string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase);
            var clash = !caseOnly && _entries.Any(e => Side == EndpointSide.Local
                ? string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase)
                : string.Equals(e.Name, n, StringComparison.Ordinal));
            return clash ? "Another item already has this name. Choose a different name; nothing is overwritten." : null;
        }, "Rename");
        if (newName is null)
        {
            return;
        }

        try
        {
            await _source.RenameAsync(item.Entry.FullPath, _source.Combine(CurrentPath, newName), CancellationToken.None);
            await NavigateAsync(CurrentPath, recordHistory: false);
            Select(newName);
        }
        catch (StoreException ex) when (ex.Kind == FailureKind.AlreadyExists)
        {
            _dialogs.ShowError("Not renamed", "Another item with that name appeared in the meantime. Nothing was overwritten.");
            await NavigateAsync(CurrentPath, recordHistory: false);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Not renamed", ex);
        }
    }

    private bool HasSelection() => SelectedItems.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath()
    {
        var text = string.Join(Environment.NewLine, SelectedItems.Select(s => s.Entry.FullPath));
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Could not copy to the clipboard", ex.Message);
        }
    }

    [RelayCommand]
    private void CopyCurrentPath()
    {
        try
        {
            Clipboard.SetText(_source.DisplayPath(CurrentPath));
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    private void ShowErrorDetails()
    {
        if (ErrorMessage is not null)
        {
            _dialogs.ShowError("Folder could not be listed", ErrorMessage, ErrorDetails);
        }
    }

    /// <summary>Asks the view to select a row by name after a refresh.</summary>
    public event Action<string>? SelectRequested;

    private void Select(string name) => SelectRequested?.Invoke(name);
}

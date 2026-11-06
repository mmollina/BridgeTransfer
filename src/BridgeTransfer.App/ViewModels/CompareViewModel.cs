using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using BridgeTransfer.App.Services;
using BridgeTransfer.Core.Comparison;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Text;
using BridgeTransfer.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BridgeTransfer.App.ViewModels;

public sealed partial class ComparisonRowViewModel : ObservableObject
{
    private readonly CompareViewModel _owner;

    public ComparisonRowViewModel(ComparisonRow row, CompareViewModel owner)
    {
        Row = row;
        _owner = owner;
    }

    public ComparisonRow Row { get; }

    public string Path => Row.RelativePath;

    public string CategoryText => Row.NeedsReview ? "Needs review" : Row.Category.Label();

    public ComparisonCategory Category => Row.Category;

    public string ActionText => Row.IsSelectable ? (Row.IsSelected ? Row.Action.Label() : "Not selected") : "No action";

    public string SourceSize => Describe(Row.Source);

    public string DestinationSize => Describe(Row.Destination);

    private static string Describe(ScannedItem? item) => item?.Kind switch
    {
        null => "\u2014",
        EntryKind.File => Format.Bytes(item.Length),
        EntryKind.Directory => "folder",
        EntryKind.SymbolicLink => "link",
        _ => "special",
    };

    public string SourceTime => Row.Source is null ? "—" : Format.LocalTime(Row.Source.ModifiedUtc);

    public string DestinationTime => Row.Destination is null ? "—" : Format.LocalTime(Row.Destination.ModifiedUtc);

    public string Reason => Row.Reason;

    public bool IsSelectable => Row.IsSelectable && !Row.NeedsReview;

    public string Tone => Row.NeedsReview ? "warn" : Row.Category switch
    {
        ComparisonCategory.OnlyOnSource or ComparisonCategory.SourceNewer => "active",
        ComparisonCategory.DestinationNewer or ComparisonCategory.AmbiguousTime => "warn",
        ComparisonCategory.TypeConflict or ComparisonCategory.UnsupportedName or ComparisonCategory.Incomplete => "bad",
        _ => "idle",
    };

    public bool IsSelected
    {
        get => Row.IsSelected && Row.IsSelectable;
        set
        {
            if (!IsSelectable || Row.IsSelected == value)
            {
                return;
            }

            Row.IsSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionText));
            _owner.SelectionChanged();
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed partial class CategoryFilter : ObservableObject
{
    private readonly Action _changed;

    public CategoryFilter(ComparisonCategory category, int count, bool include, Action changed)
    {
        Category = category;
        Count = count;
        _include = include;
        _changed = changed;
    }

    public ComparisonCategory Category { get; }

    public int Count { get; }

    public string Label => $"{Category.Label()} ({Count:N0})";

    [ObservableProperty]
    private bool _include;

    partial void OnIncludeChanged(bool value) => _changed();
}

/// <summary>Compare folders: scan both trees, review a one-way plan, apply only the reviewed selection.</summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _scanCts;
    private ComparisonResult? _result;
    private TransferJob? _job;
    private bool _applied;

    public CompareViewModel(AppServices services, IDialogService dialogs, string localRoot, string remoteRoot)
    {
        _services = services;
        _dialogs = dialogs;
        _localRoot = localRoot;
        _remoteRoot = remoteRoot;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is ComparisonRowViewModel r && PassesFilter(r);
        services.Queue.JobFinished += j =>
        {
            if (j == _job)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ShowResult(j));
            }
        };
    }

    public ObservableCollection<ComparisonRowViewModel> Rows { get; } = new();

    public ICollectionView RowsView { get; }

    public ObservableCollection<CategoryFilter> Filters { get; } = new();

    [ObservableProperty]
    private string _localRoot;

    [ObservableProperty]
    private string _remoteRoot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionLabel), nameof(SourceLabel), nameof(DestinationLabel))]
    private TransferDirection _direction = TransferDirection.Upload;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _progressText = "Choose the two folders and the direction, then select Compare. Comparing never changes any file.";

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _textFilter = "";

    [ObservableProperty]
    private string _applyText = "Nothing selected";

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private bool _isApplying;

    public bool IsUpload
    {
        get => Direction == TransferDirection.Upload;
        set
        {
            if (value)
            {
                Direction = TransferDirection.Upload;
                ClearResult();
            }
        }
    }

    public bool IsDownload
    {
        get => Direction == TransferDirection.Download;
        set
        {
            if (value)
            {
                Direction = TransferDirection.Download;
                ClearResult();
            }
        }
    }

    public string DirectionLabel => Direction.Arrow();

    public string SourceLabel => Direction == TransferDirection.Upload ? "This computer" : "Linux VM";

    public string DestinationLabel => Direction == TransferDirection.Upload ? "Linux VM" : "This computer";

    partial void OnDirectionChanged(TransferDirection value)
    {
        OnPropertyChanged(nameof(IsUpload));
        OnPropertyChanged(nameof(IsDownload));
    }

    partial void OnTextFilterChanged(string value) => RowsView.Refresh();

    partial void OnIsScanningChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    partial void OnIsApplyingChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    partial void OnLocalRootChanged(string value) => ClearResult();

    partial void OnRemoteRootChanged(string value) => ClearResult();

    private void ClearResult()
    {
        if (IsScanning || IsApplying)
        {
            return;
        }

        _result = null;
        _applied = false;
        Rows.Clear();
        Filters.Clear();
        HasResult = false;
        ResultText = null;
        WarningText = null;
        SelectionChanged();
    }

    private bool PassesFilter(ComparisonRowViewModel r)
    {
        var f = Filters.FirstOrDefault(x => x.Category == r.Category);
        if (f is not null && !f.Include && !r.Row.NeedsReview)
        {
            return false;
        }

        return TextFilter.Length == 0 || r.Path.Contains(TextFilter, StringComparison.CurrentCultureIgnoreCase);
    }

    [RelayCommand]
    private void BrowseLocal()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder on this computer", InitialDirectory = System.IO.Directory.Exists(LocalRoot) ? LocalRoot : null };
        if (dlg.ShowDialog() == true)
        {
            LocalRoot = dlg.FolderName;
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!_services.Connection.IsConnected || _services.Connection.Key is not { } key)
        {
            _dialogs.ShowInfo("Not connected", "Connect to the Linux VM first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(LocalRoot) || !System.IO.Path.IsPathFullyQualified(LocalRoot))
        {
            _dialogs.ShowInfo("Choose a local folder", "Enter or browse to a folder on this computer.");
            return;
        }

        if (!RemoteRoot.StartsWith('/'))
        {
            _dialogs.ShowInfo("Choose a Linux folder", "Enter an absolute Linux path such as /home/you/data.");
            return;
        }

        ClearResult();
        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        ITransferSession? session = null;
        try
        {
            ProgressText = "Connecting a separate session for scanning…";
            session = await _services.SessionFactory.OpenAsync(key, ct);
            var local = _services.Local;
            var remote = session.Remote;
            var localProgress = new ScanProgress(0, 0, LocalRoot);
            var remoteProgress = new ScanProgress(0, 0, RemoteRoot);
            void Report() => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                ProgressText = $"Scanning… This computer: {localProgress.Folders:N0} folders, {localProgress.Items:N0} items · Linux VM: {remoteProgress.Folders:N0} folders, {remoteProgress.Items:N0} items");

            var localTask = Task.Run(() => TreeScanner.ScanAsync(local, LocalRoot.TrimEnd(), p => { localProgress = p; Report(); }, ct), ct);
            var remoteTask = TreeScanner.ScanAsync(remote, RemoteRoot.Trim(), p => { remoteProgress = p; Report(); }, ct);
            await Task.WhenAll(localTask, remoteTask);
            var localTree = await localTask;
            var remoteTree = await remoteTask;
            var (src, dst) = Direction == TransferDirection.Upload ? (localTree, remoteTree) : (remoteTree, localTree);
            var result = await Task.Run(() => FolderComparer.Compare(src, dst, Direction), ct);
            Show(result, localTree, remoteTree);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Comparison cancelled. Nothing was changed.";
        }
        catch (StoreException ex) when (ex.Kind == FailureKind.Cancelled)
        {
            ProgressText = "Comparison cancelled. Nothing was changed.";
        }
        catch (Exception ex)
        {
            ProgressText = "The comparison could not be completed.";
            _dialogs.ShowError("Comparison failed", ex);
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private void Show(ComparisonResult result, ScanTree localTree, ScanTree remoteTree)
    {
        _result = result;
        Rows.Clear();
        foreach (var row in result.Rows)
        {
            Rows.Add(new ComparisonRowViewModel(row, this));
        }

        Filters.Clear();
        foreach (var g in result.Rows.GroupBy(r => r.Category).OrderBy(g => g.Key))
        {
            Filters.Add(new CategoryFilter(g.Key, g.Count(), g.Key != ComparisonCategory.SameMetadata, () => RowsView.Refresh()));
        }

        RowsView.Refresh();
        HasResult = true;
        var warnings = new List<string>();
        if (!localTree.IsComplete)
        {
            warnings.Add($"{localTree.Unreadable.Count:N0} folder(s) on this computer could not be read.");
        }

        if (!remoteTree.IsComplete)
        {
            warnings.Add($"{remoteTree.Unreadable.Count:N0} folder(s) on the Linux VM could not be read.");
        }

        if (warnings.Count > 0)
        {
            warnings.Add("Items inside unreadable folders are marked “Unreadable or incomplete scan” and cannot be synchronized; they are not treated as missing.");
        }

        WarningText = warnings.Count == 0 ? null : string.Join(" ", warnings);
        ProgressText = $"Compared {localTree.Items.Count:N0} items on this computer with {remoteTree.Items.Count:N0} items on the Linux VM. Nothing has been copied yet.";
        SelectionChanged();
    }

    public void SelectionChanged()
    {
        if (_applied)
        {
            return;
        }

        if (_result is null)
        {
            ApplyText = "Nothing selected";
            SummaryText = "";
            ApplyCommand.NotifyCanExecuteChanged();
            return;
        }

        var selected = _result.Selected.ToList();
        var files = selected.Count(r => r.Action is SyncAction.Copy or SyncAction.Replace);
        var folders = selected.Count(r => r.Action == SyncAction.CreateFolder);
        var replace = selected.Count(r => r.Action == SyncAction.Replace);
        var newer = selected.Count(r => r.ReplacesNewer);
        var bytes = selected.Where(r => r.Source is { Kind: EntryKind.File }).Sum(r => r.Source!.Length);
        var verb = Direction == TransferDirection.Upload ? "Upload" : "Download";
        var target = Direction == TransferDirection.Upload ? "to Linux VM" : "to this computer";
        ApplyText = files + folders == 0
            ? "Nothing selected"
            : $"{verb} {files:N0} file{(files == 1 ? "" : "s")}{(folders > 0 ? $" + {folders:N0} folder{(folders == 1 ? "" : "s")}" : "")} {target}";
        var parts = new List<string> { $"{Format.Bytes(bytes)} selected" };
        if (replace > 0)
        {
            parts.Add($"{replace:N0} existing file{(replace == 1 ? "" : "s")} will be replaced");
        }

        if (newer > 0)
        {
            parts.Add($"{newer:N0} of them {(newer == 1 ? "is" : "are")} newer on the destination");
        }

        SummaryText = string.Join(" · ", parts);
        ApplyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectProposed()
    {
        if (_result is null)
        {
            return;
        }

        foreach (var r in Rows)
        {
            r.Row.IsSelected = r.IsSelectable && r.Category is ComparisonCategory.OnlyOnSource or ComparisonCategory.SourceNewer;
            r.Refresh();
        }

        SelectionChanged();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Rows)
        {
            r.Row.IsSelected = false;
            r.Refresh();
        }

        SelectionChanged();
    }

    /// <summary>Selects visible rows that are safe defaults; destination-newer rows must be ticked one by one.</summary>
    [RelayCommand]
    private void SelectVisible()
    {
        foreach (var r in RowsView.Cast<ComparisonRowViewModel>())
        {
            if (r.IsSelectable && r.Category is not (ComparisonCategory.DestinationNewer or ComparisonCategory.AmbiguousTime))
            {
                r.Row.IsSelected = true;
                r.Refresh();
            }
        }

        SelectionChanged();
    }

    private bool CanApply() => _result is not null && !_applied && _result.Selected.Any() && !IsApplying && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_result is null || _services.Connection.Key is not { } key)
        {
            return;
        }

        var problems = FolderComparer.ValidateSelection(_result);
        if (problems.Count > 0)
        {
            _dialogs.ShowError("The selection cannot be applied", string.Join("\n", problems.Take(10)));
            return;
        }

        var newer = _result.Selected.Count(r => r.ReplacesNewer);
        if (newer > 0 && !_dialogs.Confirm(
                "Replace newer files?",
                $"{newer:N0} selected file{(newer == 1 ? " is" : "s are")} newer on the destination than on the source. Replacing loses the newer version on the destination. Replace {(newer == 1 ? "it" : "them")} anyway?",
                $"Replace {newer:N0} newer file{(newer == 1 ? "" : "s")}",
                "Go back",
                emphasizeCancel: true))
        {
            return;
        }

        IsApplying = true;
        ITransferSession? session = null;
        try
        {
            ProgressText = "Checking that nothing changed since the comparison…";
            session = await _services.SessionFactory.OpenAsync(key, CancellationToken.None);
            var (source, destination) = Direction == TransferDirection.Upload ? ((IFileStore)_services.Local, session.Remote) : (session.Remote, (IFileStore)_services.Local);
            var changed = await SyncPlanBuilder.RevalidateAsync(_result, source, destination, CancellationToken.None);
            foreach (var r in Rows)
            {
                r.Refresh();
            }

            SelectionChanged();
            RowsView.Refresh();
            if (changed > 0)
            {
                ProgressText = $"{changed:N0} item(s) changed after the comparison and were moved to “Needs review”. Nothing was copied. Review the selection and select Apply again, or compare again.";
                _dialogs.ShowInfo("Some items changed", ProgressText);
                return;
            }

            var items = SyncPlanBuilder.Build(_result, source, destination);
            _job = _services.Queue.EnqueueSync(key, Direction, _result.SourceRoot, _result.DestinationRoot, items, destination.CombineRelative);
            ProgressText = $"Applying through the transfer queue: {ApplyText.ToLowerInvariant()}. You can follow progress in the queue.";
            ResultText = null;
            _applied = true;
            ApplyText = "Applied \u2014 compare again for further changes";
            ApplyCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Could not apply the plan", ex);
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            IsApplying = false;
        }
    }

    private void ShowResult(TransferJob job)
    {
        var copied = job.Files.Count(f => f.State is TransferState.Completed or TransferState.CompletedWithWarning);
        var skipped = job.Files.Count(f => f.State == TransferState.Skipped && f.Failure is not (FailureKind.SourceChanged or FailureKind.DestinationChanged));
        var conflicted = job.Files.Count(f => f.Failure is FailureKind.SourceChanged or FailureKind.DestinationChanged);
        var failed = job.Files.Count(f => f.State == TransferState.Failed) + job.Directories.Count(d => d.State == TransferState.Failed);
        var cancelled = job.Files.Count(f => f.State == TransferState.Cancelled);
        var folders = job.Directories.Count(d => d.State == TransferState.Completed && d.ExplicitlySelected);
        ResultText = $"Finished: {copied:N0} copied, {folders:N0} folders ensured, {skipped:N0} skipped, {conflicted:N0} changed since the comparison (left untouched), {failed:N0} failed" + (cancelled > 0 ? $", {cancelled:N0} cancelled." : ".") +
                     " Compare again to confirm the result.";
        ProgressText = ResultText;
    }
}

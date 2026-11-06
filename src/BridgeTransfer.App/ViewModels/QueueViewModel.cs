using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Text;
using BridgeTransfer.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BridgeTransfer.App.ViewModels;

/// <summary>One row of the transfer queue, refreshed from <see cref="TransferJob.Snapshot"/>.</summary>
public sealed partial class JobViewModel : ObservableObject
{
    public JobViewModel(TransferJob job)
    {
        Job = job;
        Update();
    }

    public TransferJob Job { get; }

    [ObservableProperty]
    private JobSnapshot _snap = null!;

    public string Name => Snap.DisplayName;

    public string DirectionText => Snap.Direction == TransferDirection.Upload ? "Upload" : "Download";

    public string DirectionIcon => Snap.Direction == TransferDirection.Upload ? "Icon.ArrowRight" : "Icon.ArrowLeft";

    public string DirectionDetail => Snap.Direction.Arrow();

    public string Source => Snap.Source;

    public string Destination => Snap.Destination;

    public TransferState State => Snap.State;

    public string StateText
    {
        get
        {
            if (Snap.IsScanning)
            {
                return Snap.ScannedFolders > 0 ? $"Scanning ({Snap.ScannedFolders:N0} folders)" : "Scanning";
            }

            if (Snap.State == TransferState.Transferring && Snap.WaitingFiles > 0)
            {
                return $"Transferring · {Snap.WaitingFiles} waiting";
            }

            return Snap.State.Label();
        }
    }

    public string StateIcon => Snap.State switch
    {
        TransferState.Completed => "Icon.Check",
        TransferState.CompletedWithWarning => "Icon.Warning",
        TransferState.Failed => "Icon.Error",
        TransferState.Cancelled => "Icon.Cancel",
        TransferState.Skipped => "Icon.Skip",
        TransferState.WaitingForDecision => "Icon.Question",
        TransferState.Scanning => "Icon.Search",
        TransferState.Transferring => "Icon.Transfer",
        _ => "Icon.Clock",
    };

    /// <summary>"ok", "warn", "bad", "active", "idle" — used by the view for color (always paired with icon and text).</summary>
    public string StateTone => Snap.State switch
    {
        TransferState.Completed => "ok",
        TransferState.CompletedWithWarning or TransferState.WaitingForDecision or TransferState.Skipped => "warn",
        TransferState.Failed => "bad",
        TransferState.Transferring or TransferState.Scanning => "active",
        _ => "idle",
    };

    public double Percent => Snap.Fraction * 100;

    public bool IsIndeterminate => Snap.IsScanning || (Snap.State == TransferState.Queued && Snap.TotalFiles == 0);

    public string BytesText => Snap.IsScanning || Snap.TotalFiles == 0 && !Snap.State.IsFinal()
        ? "size not known yet"
        : $"{Format.Bytes(Snap.TransferredBytes)} of {Format.Bytes(Snap.TotalBytes)}";

    public string FilesText
    {
        get
        {
            if (Snap.IsScanning)
            {
                return "";
            }

            var parts = new List<string> { $"{Snap.CompletedFiles:N0} of {Snap.TotalFiles:N0} files" };
            if (Snap.FailedItems > 0)
            {
                parts.Add($"{Snap.FailedItems:N0} failed");
            }

            if (Snap.SkippedItems > 0)
            {
                parts.Add($"{Snap.SkippedItems:N0} skipped");
            }

            return string.Join(" · ", parts);
        }
    }

    public string SpeedText => Snap.State == TransferState.Transferring ? Format.Speed(Snap.BytesPerSecond) : "";

    public string RemainingText => Snap.Remaining is { } r && Snap.State == TransferState.Transferring ? Format.Duration(r) + " left" : "";

    public string? CurrentFile => Snap.State == TransferState.Transferring ? Snap.CurrentFile : null;

    public string? Message => Snap.Message;

    public bool HasProblems => Snap.FailedItems > 0 || Snap.SkippedItems > 0 || Snap.Warnings > 0 || Snap.Message is not null;

    public bool IsWaiting => Snap.WaitingFiles > 0;

    public bool IsActive => !Snap.State.IsFinal();

    public void Update()
    {
        Snap = Job.Snapshot();
    }

    partial void OnSnapChanged(JobSnapshot value) => OnPropertyChanged(string.Empty);
}

public sealed partial class QueueViewModel : ObservableObject
{
    private readonly TransferQueue _queue;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _timer;
    private readonly Queue<ConflictRequest> _conflicts = new();
    private bool _showingConflict;

    public QueueViewModel(TransferQueue queue, IDialogService dialogs)
    {
        _queue = queue;
        _dialogs = dialogs;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        queue.ConflictRaised += r => Application.Current.Dispatcher.BeginInvoke(() => EnqueueConflict(r));
        queue.JobFinished += j => Application.Current.Dispatcher.BeginInvoke(() => JobFinished?.Invoke(j));
    }

    public ObservableCollection<JobViewModel> Jobs { get; } = new();

    public event Action<TransferJob>? JobFinished;

    [ObservableProperty]
    private string _summary = "No transfers yet";

    [ObservableProperty]
    private bool _stopStartingNew;

    [ObservableProperty]
    private IReadOnlyList<JobViewModel> _selectedJobs = Array.Empty<JobViewModel>();

    [ObservableProperty]
    private string? _activeOperation;

    public bool HasJobs => Jobs.Count > 0;

    partial void OnStopStartingNewChanged(bool value) => _queue.StopStartingNew = value;

    partial void OnSelectedJobsChanged(IReadOnlyList<JobViewModel> value) => CancelSelectedCommand.NotifyCanExecuteChanged();

    public void Refresh()
    {
        var jobs = _queue.Jobs;
        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            if (!jobs.Contains(Jobs[i].Job))
            {
                Jobs.RemoveAt(i);
            }
        }

        foreach (var job in jobs)
        {
            var vm = Jobs.FirstOrDefault(j => j.Job == job);
            if (vm is null)
            {
                Jobs.Insert(0, new JobViewModel(job));
            }
            else if (vm.IsActive || vm.State != job.State)
            {
                vm.Update();
            }
        }

        var active = Jobs.Count(j => j.State is TransferState.Transferring or TransferState.Scanning);
        var queued = Jobs.Count(j => j.State == TransferState.Queued);
        var waiting = Jobs.Count(j => j.IsWaiting);
        var failed = Jobs.Count(j => j.State == TransferState.Failed);
        var done = Jobs.Count(j => j.State is TransferState.Completed or TransferState.CompletedWithWarning);
        var parts = new List<string>();
        if (active > 0)
        {
            parts.Add($"{active} in progress");
        }

        if (queued > 0)
        {
            parts.Add($"{queued} queued");
        }

        if (waiting > 0)
        {
            parts.Add($"{waiting} waiting for your decision");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} failed");
        }

        if (done > 0)
        {
            parts.Add($"{done} completed");
        }

        if (StopStartingNew && (active > 0 || queued > 0))
        {
            parts.Add("not starting new files");
        }

        Summary = parts.Count == 0 ? (Jobs.Count == 0 ? "No transfers yet" : "All transfers finished") : string.Join(" · ", parts);
        var running = Jobs.FirstOrDefault(j => j.State is TransferState.Transferring or TransferState.Scanning);
        ActiveOperation = running is null ? null : running.State == TransferState.Scanning
            ? $"Scanning {running.Name}…"
            : $"{running.DirectionText}ing {running.CurrentFile ?? running.Name} {running.SpeedText}".Trim();
        OnPropertyChanged(nameof(HasJobs));
        RetryFailedCommand.NotifyCanExecuteChanged();
        ClearFinishedCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancelSelected() => SelectedJobs.Any(j => j.IsActive);

    [RelayCommand(CanExecute = nameof(CanCancelSelected))]
    private void CancelSelected()
    {
        foreach (var j in SelectedJobs)
        {
            _queue.Cancel(j.Job);
        }

        Refresh();
    }

    private bool CanCancelAll() => Jobs.Any(j => j.IsActive);

    [RelayCommand(CanExecute = nameof(CanCancelAll))]
    private void CancelAll()
    {
        if (_dialogs.Confirm("Cancel all transfers?", "Files that are being copied are stopped. Destinations are left as they were; partially copied files are removed.", "Cancel all transfers", "Keep running"))
        {
            _queue.CancelAll();
            Refresh();
        }
    }

    private bool CanRetry() => Jobs.Any(j => j.State == TransferState.Failed || (j.Snap.FailedItems > 0 && !j.Job.CancelRequested));

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void RetryFailed()
    {
        _queue.RetryFailed();
        Refresh();
    }

    private bool CanClear() => Jobs.Any(j => !j.IsActive);

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void ClearFinished()
    {
        _queue.ClearFinished();
        Refresh();
    }

    [RelayCommand]
    private void ResolveWaiting(JobViewModel? job)
    {
        foreach (var c in _queue.PendingConflicts.Where(c => job is null || c.JobId == job.Job.Id))
        {
            if (!_conflicts.Contains(c))
            {
                _conflicts.Enqueue(c);
            }
        }

        ShowNextConflict();
    }

    private void EnqueueConflict(ConflictRequest request)
    {
        _conflicts.Enqueue(request);
        ShowNextConflict();
    }

    private void ShowNextConflict()
    {
        if (_showingConflict)
        {
            return;
        }

        _showingConflict = true;
        try
        {
            while (_conflicts.Count > 0)
            {
                var request = _conflicts.Dequeue();
                var stillWaiting = _queue.PendingConflicts.Any(c => c.TaskId == request.TaskId);
                if (!stillWaiting)
                {
                    continue;
                }

                var others = _queue.PendingConflicts.Count(c => c.JobId == request.JobId && c.TaskId != request.TaskId);
                var answer = _dialogs.AskConflict(request, others);
                if (answer is null)
                {
                    // "Decide later": the file stays waiting; the queue row offers to resolve it.
                    _conflicts.Clear();
                    break;
                }

                _queue.ResolveConflict(request, answer.Value.Choice, answer.Value.ApplyToRemaining);
            }
        }
        finally
        {
            _showingConflict = false;
        }

        Refresh();
    }

    [RelayCommand]
    private void ShowDetails(JobViewModel? job)
    {
        if (job is null)
        {
            return;
        }

        new Views.JobDetailsWindow(job.Job).ShowDialog();
    }
}

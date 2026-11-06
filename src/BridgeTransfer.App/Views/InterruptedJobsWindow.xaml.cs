using System.Windows;
using BridgeTransfer.App.Services;
using BridgeTransfer.App.ViewModels;
using BridgeTransfer.Core;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.App.Views;

/// <summary>Review of jobs that were unfinished when the app last closed. Nothing resumes automatically.</summary>
public partial class InterruptedJobsWindow
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public sealed record Row(JournalJob Job)
    {
        public string Name => Job.DisplayName + (Job.Kind == JobKind.Sync ? " (folder sync)" : "");

        public string Server => Job.Host.Length == 0 ? "" : $"{Job.Username}@{Job.Host}:{Job.Port}";

        public string Destination => Job.DestinationDirectory;

        public string State => $"{Job.LastState} ({Job.FinishedFiles:N0} of {Job.TotalFiles:N0} files finished)";

        public int StagingCount => Job.StagingFiles.Count;
    }

    public InterruptedJobsWindow(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        Grid.ItemsSource = _services.Interrupted?.Jobs.Select(j => new Row(j)).ToList() ?? new List<Row>();
        if (Grid.Items.Count > 0)
        {
            Grid.SelectAll();
        }
    }

    private List<JournalJob> Selected() => Grid.SelectedItems.Cast<Row>().Select(r => r.Job).ToList();

    private bool ConnectedTo(JournalJob j) =>
        _services.Connection.IsConnected && _services.Connection.Key is { } k &&
        string.Equals(k.Host, j.Host, StringComparison.OrdinalIgnoreCase) && k.Port == j.Port && k.Username == j.Username;

    private void Remove(IEnumerable<JournalJob> jobs)
    {
        if (_services.Interrupted is null)
        {
            return;
        }

        foreach (var j in jobs)
        {
            _services.Interrupted.Jobs.Remove(j);
        }

        _services.SaveInterrupted();
        Reload();
        _main.RefreshInterrupted();
    }

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Any(j => j.StagingFiles.Count > 0) &&
            MessageWindow.Show("Dismiss without cleaning up?", "Some of these transfers left temporary files (named .bt-staging-…part) next to their destinations. If you dismiss them now, the app will no longer offer to remove those files.", MessageKind.Warning, null,
                ("Go back", false, false, true, true), ("Dismiss anyway", true, false, false, false)) is not true)
        {
            return;
        }

        Remove(selected);
    }

    private async void OnCleanup(object sender, RoutedEventArgs e)
    {
        var removed = 0;
        var skipped = 0;
        foreach (var job in Selected())
        {
            foreach (var staging in job.StagingFiles.ToList())
            {
                IFileStore? store = staging.Side == EndpointSide.Local ? _services.Local : ConnectedTo(job) ? _services.Connection.Browse : null;
                if (store is null)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var entry = await store.GetEntryAsync(staging.Path, CancellationToken.None);

                    // Only the exact recorded path, only a regular file, only with this app's staging prefix.
                    if (entry is { IsFile: true } && entry.Name.StartsWith(AppInfo.StagingPrefix, StringComparison.Ordinal))
                    {
                        await store.DeleteOwnedFileAsync(staging.Path, CancellationToken.None);
                        removed++;
                    }

                    job.StagingFiles.Remove(staging);
                }
                catch (Exception ex)
                {
                    _services.Log.Warn($"Cleanup of {staging.Path} failed: {ex.Message}");
                    skipped++;
                }
            }
        }

        _services.SaveInterrupted();
        Reload();
        _main.RefreshInterrupted();
        Status.Text = $"Removed {removed:N0} temporary file(s)." + (skipped > 0 ? $" {skipped:N0} could not be checked now (connect to the same server to remove files on the VM)." : "");
    }

    private async void OnRequeue(object sender, RoutedEventArgs e)
    {
        var queued = new List<JournalJob>();
        var notes = new List<string>();
        foreach (var job in Selected())
        {
            if (job.Kind == JobKind.Sync)
            {
                notes.Add($"“{job.DisplayName}” was a folder synchronization. Run Compare folders again to review it.");
                continue;
            }

            if (!ConnectedTo(job) || _services.Connection.Key is not { } key)
            {
                notes.Add($"“{job.DisplayName}” needs a connection to {job.Username}@{job.Host}:{job.Port}.");
                continue;
            }

            IFileStore source = job.Direction == TransferDirection.Upload ? _services.Local : _services.Connection.Browse!;
            var entries = new List<FileEntry>();
            foreach (var path in job.SourcePaths)
            {
                if (await source.GetEntryAsync(path, CancellationToken.None) is { } entry)
                {
                    entries.Add(entry);
                }
                else
                {
                    notes.Add($"{path} no longer exists and was left out.");
                }
            }

            if (entries.Count > 0)
            {
                _services.Queue.EnqueueItems(key, job.Direction, entries, job.SourceDescription, job.DestinationDirectory);
                queued.Add(job);
            }
        }

        // Re-queued jobs keep their staging records until removed explicitly.
        Remove(queued.Where(j => j.StagingFiles.Count == 0));
        _main.QueueCollapsed = false;
        Status.Text = (queued.Count > 0 ? $"Queued {queued.Count} transfer(s) again. " : "") + string.Join(" ", notes);
    }
}

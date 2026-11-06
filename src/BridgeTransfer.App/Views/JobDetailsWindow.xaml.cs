using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.App.Views;

/// <summary>Per-item outcome of a job, including items skipped while scanning.</summary>
public partial class JobDetailsWindow
{
    private List<Line>? _all;

    public sealed record Line(string Path, string State, string Message, bool IsProblem);

    public JobDetailsWindow(TransferJob job)
    {
        InitializeComponent();
        var snap = job.Snapshot();
        Heading.Text = $"{(job.Direction == TransferDirection.Upload ? "Upload" : "Download")}: {job.DisplayName}";
        Summary.Text = $"{job.Direction.Arrow()} · from {job.SourceDescription} into {job.DestinationDirectory} · {snap.State.Label()} · {snap.CompletedFiles:N0} of {snap.TotalFiles:N0} files" +
                       (job.JobMessage is null ? "" : " · " + job.JobMessage);
        var lines = new List<Line>();
        foreach (var s in job.ScanSkipped)
        {
            lines.Add(new Line(s.RelativePath, "Skipped", s.Message, true));
        }

        foreach (var d in job.Directories.Where(d => d.State is TransferState.Failed or TransferState.Skipped))
        {
            lines.Add(new Line(d.RelativePath + "/", d.State.Label(), d.Message ?? "", true));
        }

        foreach (var f in job.Files.ToList())
        {
            var problem = f.State is not TransferState.Completed || f.HasWarning || f.Attempts > 0;
            lines.Add(new Line(f.RelativePath, f.State.Label(), f.Message ?? (f.State == TransferState.Completed ? "Copied; size verified." : ""), problem));
        }

        _all = lines;
        OnFilter(null, null);
    }

    private void OnFilter(object? sender, System.Windows.RoutedEventArgs? e)
    {
        if (_all is not null)
        {
            Grid.ItemsSource = OnlyProblems.IsChecked == true ? _all.Where(l => l.IsProblem).ToList() : _all;
        }
    }
}

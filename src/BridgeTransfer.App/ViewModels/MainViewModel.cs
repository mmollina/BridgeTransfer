using System.Collections.ObjectModel;
using System.Windows;
using BridgeTransfer.App.Services;
using BridgeTransfer.Core;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Persistence;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Transfers;
using BridgeTransfer.Infrastructure.Sftp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BridgeTransfer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private CancellationTokenSource? _connectCts;

    public MainViewModel(AppServices services, IDialogService dialogs)
    {
        Services = services;
        Dialogs = dialogs;
        Connection = services.Connection;
        Local = new FilePanelViewModel(new LocalPanelSource(services.Local), dialogs, "This computer", m => services.Log.Warn(m));
        Remote = new FilePanelViewModel(new RemotePanelSource(services.Connection), dialogs, "Linux VM", m => services.Log.Warn(m));
        Queue = new QueueViewModel(services.Queue, dialogs);
        Profiles = new ObservableCollection<ConnectionProfile>(services.Settings.Profiles);
        Bookmarks = new ObservableCollection<FolderBookmark>(services.Settings.Bookmarks);
        SidebarCollapsed = services.Settings.SidebarCollapsed;
        QueueCollapsed = services.Settings.QueueCollapsed;
        ShowHidden = services.Settings.ShowHiddenFiles;
        Local.ShowHidden = ShowHidden;
        Remote.ShowHidden = ShowHidden;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == services.Settings.LastProfileId) ?? Profiles.FirstOrDefault();
        ActivePanel = Local;
        Local.IsActive = true;

        Connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionManager.Status))
            {
                OnConnectionStatusChanged();
            }
        };
        Remote.ConnectionProblem += reason => Connection.MarkLost(reason);
        Local.PropertyChanged += (_, e) => PanelChanged(e.PropertyName);
        Remote.PropertyChanged += (_, e) => PanelChanged(e.PropertyName);
        Queue.JobFinished += OnJobFinished;
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueViewModel.ActiveOperation))
            {
                OnPropertyChanged(nameof(OperationText));
            }
        };
        OnConnectionStatusChanged();
    }

    public AppServices Services { get; }

    public IDialogService Dialogs { get; }

    public ConnectionManager Connection { get; }

    public FilePanelViewModel Local { get; }

    public FilePanelViewModel Remote { get; }

    public QueueViewModel Queue { get; }

    public ObservableCollection<ConnectionProfile> Profiles { get; }

    public ObservableCollection<FolderBookmark> Bookmarks { get; }

    public string AppTitle => AppInfo.ProductName;

    [ObservableProperty]
    private ConnectionProfile? _selectedProfile;

    [ObservableProperty]
    private FilePanelViewModel _activePanel;

    [ObservableProperty]
    private bool _sidebarCollapsed;

    [ObservableProperty]
    private bool _queueCollapsed;

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private string? _notice;

    public bool IsDark => ThemeManager.Current == AppTheme.Dark;

    public bool IsConnecting => Connection.Status == ConnectionStatus.Connecting;

    public bool IsConnected => Connection.Status == ConnectionStatus.Connected;

    public bool IsLost => Connection.Status == ConnectionStatus.Lost;

    public string ConnectionTitle => Connection.Profile?.DisplayName ?? SelectedProfile?.DisplayName ?? "No connection selected";

    public string ConnectionSubtitle => Connection.Profile?.Summary ?? SelectedProfile?.Summary ?? "Add a connection to get started";

    public string StatusLabel => Connection.Status switch
    {
        ConnectionStatus.Connected => "Connected",
        ConnectionStatus.Connecting => "Connecting…",
        ConnectionStatus.Lost => "Connection lost",
        _ => "Not connected",
    };

    public string StatusTone => Connection.Status switch
    {
        ConnectionStatus.Connected => "ok",
        ConnectionStatus.Connecting => "warn",
        ConnectionStatus.Lost => "bad",
        _ => "idle",
    };

    public string OperationText =>
        Queue.ActiveOperation ?? (Local.IsLoading ? "Listing this computer…" : Remote.IsLoading ? "Listing the Linux VM…" : IsConnecting ? Connection.StatusMessage : "Ready");

    public string SelectionStatus => ActivePanel.SelectionText.Length > 0 ? $"{ActivePanel.Title}: {ActivePanel.SelectionText}" : $"{ActivePanel.Title}: {ActivePanel.CountText}";

    public string ConnectionStatusText => Connection.Status == ConnectionStatus.Connected
        ? $"{Connection.Profile?.Summary} · {Connection.ServerIdentity}"
        : Connection.StatusMessage;

    public string UploadToolTip => IsConnected
        ? $"Copy the items selected on This computer into {Remote.CurrentPath} on the Linux VM"
        : "Connect to the Linux VM first";

    public string DownloadToolTip => IsConnected
        ? $"Copy the items selected on the Linux VM into {Local.Source.DisplayPath(Local.CurrentPath)} on this computer"
        : "Connect to the Linux VM first";

    partial void OnActivePanelChanged(FilePanelViewModel value)
    {
        Local.IsActive = ReferenceEquals(value, Local);
        Remote.IsActive = ReferenceEquals(value, Remote);
        OnPropertyChanged(nameof(SelectionStatus));
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        OnPropertyChanged(nameof(ConnectionTitle));
        OnPropertyChanged(nameof(ConnectionSubtitle));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnSidebarCollapsedChanged(bool value)
    {
        Services.Settings.SidebarCollapsed = value;
        Services.SaveSettings();
    }

    partial void OnQueueCollapsedChanged(bool value)
    {
        Services.Settings.QueueCollapsed = value;
        Services.SaveSettings();
    }

    partial void OnShowHiddenChanged(bool value)
    {
        Local.ShowHidden = value;
        Remote.ShowHidden = value;
        Services.Settings.ShowHiddenFiles = value;
        Services.SaveSettings();
    }

    private void PanelChanged(string? property)
    {
        if (property is nameof(FilePanelViewModel.ShowHidden) && (Local.ShowHidden != ShowHidden || Remote.ShowHidden != ShowHidden))
        {
            ShowHidden = Local.ShowHidden != ShowHidden ? Local.ShowHidden : Remote.ShowHidden;
        }

        if (property is nameof(FilePanelViewModel.SelectionText) or nameof(FilePanelViewModel.CountText))
        {
            OnPropertyChanged(nameof(SelectionStatus));
            UploadCommand.NotifyCanExecuteChanged();
            DownloadCommand.NotifyCanExecuteChanged();
        }

        if (property is nameof(FilePanelViewModel.IsLoading))
        {
            OnPropertyChanged(nameof(OperationText));
        }

        if (property is nameof(FilePanelViewModel.CurrentPath) or nameof(FilePanelViewModel.HasLoaded) or nameof(FilePanelViewModel.ErrorMessage))
        {
            OnPropertyChanged(nameof(UploadToolTip));
            OnPropertyChanged(nameof(DownloadToolTip));
            UploadCommand.NotifyCanExecuteChanged();
            DownloadCommand.NotifyCanExecuteChanged();
            AddBookmarkCommand.NotifyCanExecuteChanged();
            CompareCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnConnectionStatusChanged()
    {
        foreach (var p in new[] { nameof(IsConnecting), nameof(IsConnected), nameof(IsLost), nameof(StatusLabel), nameof(StatusTone), nameof(ConnectionTitle), nameof(ConnectionSubtitle), nameof(ConnectionStatusText), nameof(OperationText), nameof(UploadToolTip), nameof(DownloadToolTip) })
        {
            OnPropertyChanged(p);
        }

        switch (Connection.Status)
        {
            case ConnectionStatus.Disconnected:
                Remote.SetUnavailable("Not connected. Choose a connection on the left and select Connect.");
                break;
            case ConnectionStatus.Connecting:
                Remote.SetUnavailable(Connection.StatusMessage);
                break;
            case ConnectionStatus.Lost:
                Remote.SetUnavailable("The connection to the Linux VM was lost. Select Reconnect to continue. Queued transfers retry automatically for a short time.");
                break;
        }

        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        UploadCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        AddBookmarkCommand.NotifyCanExecuteChanged();
    }

    public async Task InitializeAsync()
    {
        var startLocal = Services.Settings.LastLocalFolder is { } last && System.IO.Directory.Exists(last) ? last : Local.Source.HomePath;
        await Local.NavigateAsync(startLocal);
        if (Services.StartupWarning is { } warning)
        {
            Dialogs.ShowInfo("Settings", warning);
        }
    }

    // ---------------- Connection ----------------
    private bool CanConnect() => !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(ConnectionProfile? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            await NewProfileAsync();
            return;
        }

        SelectedProfile = profile;
        if (Connection.Status == ConnectionStatus.Connected && Connection.Profile?.Id == profile.Id)
        {
            return;
        }

        if (!await ConfirmLeaveConnectionAsync())
        {
            return;
        }

        string? password = null;
        string? passphrase = null;
        if (profile.Authentication == AuthenticationKind.Password)
        {
            password = Dialogs.PromptSecret("Password", $"Enter the password for {profile.Summary}. It is kept in memory for this session only and never saved.", "Password");
            if (password is null)
            {
                return;
            }
        }
        else
        {
            try
            {
                SftpConnector.LoadKey(profile.PrivateKeyPath ?? "", null).Dispose();
            }
            catch (KeyFileException k) when (k.Problem == KeyFileProblem.PassphraseRequired)
            {
                passphrase = Dialogs.PromptSecret("Key passphrase", $"The key {System.IO.Path.GetFileName(profile.PrivateKeyPath)} is protected by a passphrase. It is kept in memory for this session only.", "Passphrase");
                if (passphrase is null)
                {
                    return;
                }
            }
            catch (KeyFileException k)
            {
                Dialogs.ShowError("The private key cannot be used", k);
                return;
            }
        }

        var autoRetried = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();
            var prompted = false;
            HostKeyPrompt prompt = (key, ct) => Application.Current.Dispatcher.InvokeAsync(() =>
            {
                prompted = true;
                return Dialogs.ConfirmUnknownHostKey(key);
            }).Task;

            try
            {
                await Connection.ConnectAsync(profile, new SessionCredentials(password, passphrase), prompt, _connectCts.Token);
                await AfterConnectedAsync(profile);
                return;
            }
            catch (KeyFileException k) when (k.Problem == KeyFileProblem.WrongPassphraseOrCorrupt)
            {
                passphrase = Dialogs.PromptSecret("Key passphrase", "That passphrase did not unlock the key. Try again.", "Passphrase");
                if (passphrase is null)
                {
                    return;
                }
            }
            catch (StoreException s) when (s.Kind == FailureKind.Authentication && profile.Authentication == AuthenticationKind.Password && s is not KeyFileException)
            {
                password = Dialogs.PromptSecret("Password", "The VM rejected the user name or password. Enter the password again.", "Password");
                if (password is null)
                {
                    return;
                }
            }
            catch (HostKeyChangedException h)
            {
                Dialogs.ShowHostKeyChanged(h);
                return;
            }
            catch (HostKeyRejectedException)
            {
                Notice = "Not connected: the server's identity was not confirmed, so no login details were sent.";
                return;
            }
            catch (StoreException s) when (s.Kind == FailureKind.Cancelled)
            {
                Notice = "Connecting was cancelled.";
                return;
            }
            catch (StoreException s) when (s.Kind == FailureKind.Transient && prompted && !autoRetried)
            {
                // The handshake can time out while the identity dialog is open; the key is trusted now, so try again once.
                autoRetried = true;
            }
            catch (Exception ex)
            {
                Dialogs.ShowError("Could not connect", ex);
                return;
            }
        }
    }

    private async Task AfterConnectedAsync(ConnectionProfile profile)
    {
        Notice = null;
        Services.Settings.LastProfileId = profile.Id;
        Services.SaveSettings();
        Remote.ClearHistory();
        var start = string.IsNullOrWhiteSpace(profile.DefaultRemoteFolder)
            ? Remote.Source.HomePath
            : Remote.Source.NormalizeInput(profile.DefaultRemoteFolder, Remote.Source.HomePath);
        await Remote.NavigateAsync(start);
        if (!string.IsNullOrWhiteSpace(profile.DefaultLocalFolder) && System.IO.Directory.Exists(profile.DefaultLocalFolder))
        {
            await Local.NavigateAsync(profile.DefaultLocalFolder);
        }
    }

    [RelayCommand]
    private void CancelConnect() => _connectCts?.Cancel();

    private bool CanDisconnect() => Connection.Status is ConnectionStatus.Connected or ConnectionStatus.Lost;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (await ConfirmLeaveConnectionAsync())
        {
            Connection.Disconnect();
        }
    }

    /// <summary>Queued work is bound to a server; it is cancelled explicitly before leaving that server.</summary>
    private async Task<bool> ConfirmLeaveConnectionAsync()
    {
        if (Connection.Key is not { } key || !Services.Queue.HasActiveWorkFor(key))
        {
            return true;
        }

        if (!Dialogs.Confirm(
                "Transfers are still running",
                $"Transfers to or from {key.Host} are queued or in progress. Disconnecting cancels them. Files that were being copied are removed from their temporary names and destinations are left unchanged.",
                "Cancel transfers and disconnect",
                "Stay connected",
                emphasizeCancel: true))
        {
            return false;
        }

        await Services.Queue.CancelForConnectionAsync(key);
        return true;
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        try
        {
            HostKeyPrompt prompt = (key, _) => Application.Current.Dispatcher.InvokeAsync(() => Dialogs.ConfirmUnknownHostKey(key)).Task;
            await Connection.ReconnectAsync(prompt, CancellationToken.None);
            await Remote.NavigateAsync(Remote.CurrentPath.Length > 0 ? Remote.CurrentPath : Remote.Source.HomePath, recordHistory: false);
        }
        catch (HostKeyChangedException h)
        {
            Dialogs.ShowHostKeyChanged(h);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Could not reconnect", ex);
        }
    }

    // ---------------- Profiles ----------------
    [RelayCommand]
    private async Task NewProfileAsync()
    {
        var profile = new ConnectionProfile { DisplayName = "Linux VM", Port = 22, Authentication = AuthenticationKind.PrivateKey };
        var defaultKey = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
        if (System.IO.File.Exists(defaultKey))
        {
            profile.PrivateKeyPath = defaultKey;
        }

        if (Views.ConnectionEditorWindow.Edit(profile, isNew: true, Services, Dialogs) is { } saved)
        {
            Profiles.Add(saved);
            Services.Settings.Profiles.Add(saved);
            Services.SaveSettings();
            SelectedProfile = saved;
            if (Dialogs.Confirm("Connection saved", $"Connect to {saved.DisplayName} now?", "Connect", "Not now"))
            {
                await ConnectAsync(saved);
            }
        }
    }

    [RelayCommand]
    private void EditProfile(ConnectionProfile? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            return;
        }

        var isActive = Connection.Profile?.Id == profile.Id && Connection.Status != ConnectionStatus.Disconnected;
        if (Views.ConnectionEditorWindow.Edit(profile.Clone(), isNew: false, Services, Dialogs, readOnlyIdentity: isActive) is { } saved)
        {
            var i = Profiles.IndexOf(profile);
            Profiles[i] = saved;
            var j = Services.Settings.Profiles.FindIndex(p => p.Id == saved.Id);
            Services.Settings.Profiles[j] = saved;
            Services.SaveSettings();
            SelectedProfile = saved;
            if (isActive)
            {
                Notice = "Changes take effect the next time you connect.";
            }
        }
    }

    [RelayCommand]
    private void DuplicateProfile(ConnectionProfile? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            return;
        }

        var copy = profile.Clone();
        copy.Id = Guid.NewGuid();
        copy.DisplayName = profile.DisplayName + " (copy)";
        Profiles.Add(copy);
        Services.Settings.Profiles.Add(copy);
        Services.SaveSettings();
        SelectedProfile = copy;
    }

    [RelayCommand]
    private void RemoveProfile(ConnectionProfile? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            return;
        }

        if (Connection.Profile?.Id == profile.Id && Connection.Status != ConnectionStatus.Disconnected)
        {
            Dialogs.ShowInfo("Connection in use", "Disconnect before removing this saved connection.");
            return;
        }

        if (!Dialogs.Confirm("Remove saved connection?", $"“{profile.DisplayName}” is removed from the list. No files are touched and the key file is not deleted. Folder pairs that use it are removed too.", "Remove", "Keep"))
        {
            return;
        }

        Profiles.Remove(profile);
        Services.Settings.Profiles.RemoveAll(p => p.Id == profile.Id);
        foreach (var b in Bookmarks.Where(b => b.ProfileId == profile.Id).ToList())
        {
            Bookmarks.Remove(b);
        }

        Services.Settings.Bookmarks.RemoveAll(b => b.ProfileId == profile.Id);
        Services.SaveSettings();
        SelectedProfile = Profiles.FirstOrDefault();
    }

    // ---------------- Transfers ----------------
    private bool CanUpload() => IsConnected && Local.SelectedEntries.Count > 0 && Remote.CanWriteHere;

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private Task UploadAsync() => StartTransferAsync(TransferDirection.Upload, Local.SelectedEntries, Local.CurrentPath, Remote.CurrentPath);

    private bool CanDownload() => IsConnected && Remote.SelectedEntries.Count > 0 && Local.CanWriteHere;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadAsync() => StartTransferAsync(TransferDirection.Download, Remote.SelectedEntries, Remote.CurrentPath, Local.CurrentPath);

    /// <summary>Starts a transfer after showing exactly where the items will go.</summary>
    public Task StartTransferAsync(TransferDirection direction, IReadOnlyList<FileEntry> entries, string sourceDirectory, string destinationDirectory)
    {
        if (!IsConnected || Connection.Key is not { } key)
        {
            Dialogs.ShowInfo("Not connected", "Connect to the Linux VM first.");
            return Task.CompletedTask;
        }

        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        var destPanel = direction == TransferDirection.Upload ? Remote : Local;
        if (!destPanel.Source.CanWriteIn(destinationDirectory))
        {
            Dialogs.ShowInfo("Choose a destination folder", "Open a folder on the destination side first.");
            return Task.CompletedTask;
        }

        var links = entries.Count(e => e.Kind == EntryKind.SymbolicLink);
        if (Services.Settings.ConfirmBeforeTransfer)
        {
            var lines = entries.Take(6).Select(e => "    " + destPanel.Source.Combine(destinationDirectory, e.Name) + (e.IsDirectory ? "  (folder)" : "")).ToList();
            if (entries.Count > 6)
            {
                lines.Add($"    … and {entries.Count - 6:N0} more");
            }

            var where = direction == TransferDirection.Upload ? $"the Linux VM ({key.Host})" : "this computer";
            var message =
                $"{direction.Arrow()}\n\nDestination folder:\n    {destPanel.Source.DisplayPath(destinationDirectory)}\n\nResult:\n{string.Join("\n", lines)}\n\n" +
                "Existing files are never replaced without asking. Folders are copied with their structure, including empty folders." +
                (links > 0 ? $"\n\n{links} selected link(s) will be skipped." : "");
            var verb = direction == TransferDirection.Upload ? "Upload" : "Download";
            if (!Dialogs.Confirm($"{verb} {entries.Count:N0} item{(entries.Count == 1 ? "" : "s")} to {where}?", message, verb))
            {
                return Task.CompletedTask;
            }
        }

        Services.Queue.EnqueueItems(key, direction, entries, sourceDirectory, destinationDirectory);
        Services.Log.Info($"{direction} queued: {entries.Count} item(s) from {sourceDirectory} to {destinationDirectory}");
        QueueCollapsed = false;
        Queue.Refresh();
        return Task.CompletedTask;
    }

    private void OnJobFinished(TransferJob job)
    {
        var panel = job.Direction == TransferDirection.Upload ? Remote : Local;
        if (panel.HasLoaded && !panel.IsUnavailable && (panel.CurrentPath == job.DestinationDirectory || job.Kind == JobKind.Sync))
        {
            _ = panel.RefreshCommand.ExecuteAsync(null);
        }
    }

    // ---------------- Folder pairs ----------------
    private bool CanAddBookmark() => IsConnected && Local.CanWriteHere && Remote.HasLoaded && Remote.ErrorMessage is null;

    [RelayCommand(CanExecute = nameof(CanAddBookmark))]
    private void AddBookmark()
    {
        var suggestion = $"{Local.Source.DisplayPath(Local.CurrentPath).TrimEnd('\\').Split('\\').Last()} ↔ {BridgeTransfer.Core.Paths.RemotePath.GetName(Remote.CurrentPath)}";
        var name = Dialogs.PromptText("Save folder pair", $"Name for the pair\n{Local.CurrentPath}\n↔ {Connection.Profile!.Summary}:{Remote.CurrentPath}", suggestion, n => string.IsNullOrWhiteSpace(n) ? "Enter a name." : null, "Save");
        if (name is null)
        {
            return;
        }

        var b = new FolderBookmark { Name = name.Trim(), ProfileId = Connection.Profile.Id, LocalRoot = Local.CurrentPath, RemoteRoot = Remote.CurrentPath };
        Bookmarks.Add(b);
        Services.Settings.Bookmarks.Add(b);
        Services.SaveSettings();
    }

    [RelayCommand]
    private async Task OpenBookmarkAsync(FolderBookmark? bookmark)
    {
        if (bookmark is null)
        {
            return;
        }

        var profile = Profiles.FirstOrDefault(p => p.Id == bookmark.ProfileId);
        if (profile is null)
        {
            Dialogs.ShowError("Folder pair cannot be opened", "The saved connection for this folder pair no longer exists.");
            return;
        }

        if (!(IsConnected && Connection.Profile?.Id == profile.Id))
        {
            await ConnectAsync(profile);
            if (!(IsConnected && Connection.Profile?.Id == profile.Id))
            {
                return;
            }
        }

        await Local.NavigateAsync(bookmark.LocalRoot);
        await Remote.NavigateAsync(bookmark.RemoteRoot);
    }

    [RelayCommand]
    private void RenameBookmark(FolderBookmark? bookmark)
    {
        if (bookmark is null)
        {
            return;
        }

        var name = Dialogs.PromptText("Rename folder pair", "New name", bookmark.Name, n => string.IsNullOrWhiteSpace(n) ? "Enter a name." : null, "Rename");
        if (name is null)
        {
            return;
        }

        bookmark.Name = name.Trim();
        var i = Bookmarks.IndexOf(bookmark);
        Bookmarks.RemoveAt(i);
        Bookmarks.Insert(i, bookmark);
        Services.SaveSettings();
    }

    [RelayCommand]
    private void RemoveBookmark(FolderBookmark? bookmark)
    {
        if (bookmark is null || !Dialogs.Confirm("Remove folder pair?", $"“{bookmark.Name}” is removed from the list. No files are touched.", "Remove", "Keep"))
        {
            return;
        }

        Bookmarks.Remove(bookmark);
        Services.Settings.Bookmarks.RemoveAll(b => b.Id == bookmark.Id);
        Services.SaveSettings();
    }

    // ---------------- Other windows ----------------
    private bool CanCompare() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private void Compare()
    {
        var local = Local.CanWriteHere ? Local.CurrentPath : "";
        var remote = Remote.HasLoaded && Remote.ErrorMessage is null ? Remote.CurrentPath : Remote.Source.HomePath;
        var vm = new CompareViewModel(Services, Dialogs, local, remote);
        var w = new Views.CompareWindow(vm);
        w.Show();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (Views.SettingsWindow.Edit(Services))
        {
            ThemeManager.Apply(Services.Settings.Theme);
            OnPropertyChanged(nameof(IsDark));
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        Services.Settings.Theme = ThemeManager.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.Apply(Services.Settings.Theme);
        Services.SaveSettings();
        OnPropertyChanged(nameof(IsDark));
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    [RelayCommand]
    private void ToggleQueue() => QueueCollapsed = !QueueCollapsed;

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    public int InterruptedCount => Services.Interrupted?.Jobs.Count ?? 0;

    public bool HasInterrupted => InterruptedCount > 0;

    public void RefreshInterrupted()
    {
        OnPropertyChanged(nameof(InterruptedCount));
        OnPropertyChanged(nameof(HasInterrupted));
    }

    [RelayCommand]
    private void ReviewInterrupted()
    {
        new Views.InterruptedJobsWindow(Services, this).ShowDialog();
        RefreshInterrupted();
    }

    public void SaveWindowState()
    {
        Services.Settings.LastLocalFolder = Local.CanWriteHere ? Local.CurrentPath : null;
        Services.SaveSettings();
    }
}

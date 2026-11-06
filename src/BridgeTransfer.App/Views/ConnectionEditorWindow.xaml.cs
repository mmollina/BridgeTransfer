using System.Windows;
using BridgeTransfer.App.Services;
using BridgeTransfer.App.ViewModels;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Infrastructure.Sftp;

namespace BridgeTransfer.App.Views;

/// <summary>Create or edit a saved connection; test it; manage the trusted host key explicitly.</summary>
public partial class ConnectionEditorWindow
{
    private readonly AppServices _services;
    private readonly IDialogService _dialogs;
    private ConnectionProfile _profile = new();
    private CancellationTokenSource? _testCts;

    private ConnectionEditorWindow(AppServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        InitializeComponent();
        KeyFormats.Text = "Supported: " + SftpConnector.SupportedKeyFormats + " PuTTY .ppk keys are not supported; export them to OpenSSH format with PuTTYgen. The key file is only read, never changed or copied.";
    }

    public static ConnectionProfile? Edit(ConnectionProfile profile, bool isNew, AppServices services, IDialogService dialogs, bool readOnlyIdentity = false)
    {
        var w = new ConnectionEditorWindow(services, dialogs) { _profile = profile };
        w.Heading.Text = isNew ? "New connection" : $"Edit \u201C{profile.DisplayName}\u201D";
        w.Title = isNew ? "New connection" : "Edit connection";
        w.NameBox.Text = profile.DisplayName;
        w.HostBox.Text = profile.Host;
        w.PortBox.Text = profile.Port.ToString();
        w.UserBox.Text = profile.Username;
        w.KeyBox.Text = profile.PrivateKeyPath ?? "";
        w.LocalBox.Text = profile.DefaultLocalFolder ?? "";
        w.RemoteBox.Text = profile.DefaultRemoteFolder ?? "";
        w.KeyRadio.IsChecked = profile.Authentication == AuthenticationKind.PrivateKey;
        w.PasswordRadio.IsChecked = profile.Authentication == AuthenticationKind.Password;
        w.ForgetButton.IsEnabled = !readOnlyIdentity;
        w.UpdateIdentity();
        w.Loaded += (_, _) => (isNew ? w.HostBox : w.NameBox).Focus();
        w.Closed += (_, _) => w._testCts?.Cancel();
        return w.ShowDialog() == true ? w._profile : null;
    }

    private ConnectionProfile Collect()
    {
        var p = _profile.Clone();
        p.DisplayName = NameBox.Text.Trim();
        p.Host = HostBox.Text.Trim();
        p.Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : -1;
        p.Username = UserBox.Text.Trim();
        p.Authentication = PasswordRadio.IsChecked == true ? AuthenticationKind.Password : AuthenticationKind.PrivateKey;
        p.PrivateKeyPath = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim().Trim('"');
        p.DefaultLocalFolder = string.IsNullOrWhiteSpace(LocalBox.Text) ? null : LocalBox.Text.Trim();
        p.DefaultRemoteFolder = string.IsNullOrWhiteSpace(RemoteBox.Text) ? null : RemoteBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(p.DisplayName) && !string.IsNullOrWhiteSpace(p.Host))
        {
            p.DisplayName = p.Host;
        }

        return p;
    }

    private bool Validate(ConnectionProfile p)
    {
        var problems = p.Validate().ToList();
        if (p.Authentication == AuthenticationKind.PrivateKey && p.PrivateKeyPath is { } k && !System.IO.File.Exists(k))
        {
            problems.Add("The private key file does not exist.");
        }

        if (p.DefaultLocalFolder is { } l && !System.IO.Path.IsPathFullyQualified(l))
        {
            problems.Add("The local start folder must be a full path such as C:\\Users\\you\\Documents.");
        }

        Problems.Text = string.Join("\n", problems);
        Problems.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return problems.Count == 0;
    }

    private void UpdateIdentity()
    {
        var host = HostBox.Text.Trim();
        var port = int.TryParse(PortBox.Text, out var p) ? p : 22;
        var trusted = string.IsNullOrEmpty(host) ? new List<KnownHostRecord>() : _services.Verifier.GetTrusted(host, port).ToList();
        IdentityText.Text = trusted.Count == 0
            ? "No key trusted yet. You will be asked to verify the server's fingerprint on the first connection."
            : string.Join("\n", trusted.Select(t => $"{t.Algorithm}  {t.DisplayFingerprint}"));
        ForgetButton.Visibility = trusted.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IdentityText.FontFamily = (System.Windows.Media.FontFamily)FindResource(trusted.Count == 0 ? "Font.UI" : "Font.Mono");
    }

    private void OnAuthChanged(object sender, RoutedEventArgs e)
    {
        if (KeyPanel is not null)
        {
            KeyPanel.Visibility = KeyRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var sshDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose your private key file",
            Filter = "All files (*.*)|*.*",
            InitialDirectory = System.IO.Directory.Exists(sshDir) ? sshDir : null,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
        {
            KeyBox.Text = dlg.FileName;
        }
    }

    private void OnBrowseLocal(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder to start in" };
        if (dlg.ShowDialog(this) == true)
        {
            LocalBox.Text = dlg.FolderName;
        }
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        var port = int.TryParse(PortBox.Text, out var p) ? p : 22;
        if (_dialogs.Confirm("Forget the trusted key?", $"The trusted key for {host} (port {port}) is removed. On the next connection you must verify the server's fingerprint again. Only do this if you know why the key changed.", "Forget key", "Keep it", emphasizeCancel: true))
        {
            _services.Verifier.Forget(host, port);
            _services.Log.Info($"Trusted host key removed for {host}:{port}");
            UpdateIdentity();
        }
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var p = Collect();
        if (!Validate(p))
        {
            return;
        }

        string? password = null;
        string? passphrase = null;
        if (p.Authentication == AuthenticationKind.Password)
        {
            password = _dialogs.PromptSecret("Password", $"Password for {p.Summary} (used for this test only)", "Password");
            if (password is null)
            {
                return;
            }
        }
        else
        {
            try
            {
                SftpConnector.LoadKey(p.PrivateKeyPath!, null).Dispose();
            }
            catch (KeyFileException k) when (k.Problem == KeyFileProblem.PassphraseRequired)
            {
                passphrase = _dialogs.PromptSecret("Key passphrase", "This key is protected by a passphrase (used for this test only).", "Passphrase");
                if (passphrase is null)
                {
                    return;
                }
            }
            catch (KeyFileException k)
            {
                ShowTest(false, k.Message);
                return;
            }
        }

        TestButton.IsEnabled = false;
        TestButton.Content = "Testing…";
        _testCts = new CancellationTokenSource();
        using var creds = new SessionCredentials(password, passphrase);
        try
        {
            HostKeyPrompt prompt = (key, _) => Dispatcher.InvokeAsync(() => _dialogs.ConfirmUnknownHostKey(key)).Task;
            var client = await SftpConnector.ConnectAsync(p, creds, _services.Verifier, prompt, _services.ConnectOptions, _testCts.Token);
            using var store = new SftpFileStore(client);
            var home = store.HomeDirectory;
            var listed = await store.ListAsync(home, _testCts.Token);
            ShowTest(true, $"Connected and signed in as {p.Username}. Home folder {home} has {listed.Count:N0} items. SFTP works.");
        }
        catch (HostKeyChangedException h)
        {
            ShowTest(false, h.Message);
            _dialogs.ShowHostKeyChanged(h);
        }
        catch (StoreException ex)
        {
            ShowTest(false, ex.Message);
        }
        catch (Exception ex)
        {
            ShowTest(false, ErrorText.Describe(ex).Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestButton.Content = "Test connection";
            UpdateIdentity();
        }
    }

    private void ShowTest(bool ok, string text)
    {
        TestResult.Visibility = Visibility.Visible;
        TestResult.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, ok ? "Brush.SuccessSoft" : "Brush.DangerSoft");
        TestResult.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, ok ? "Brush.Success" : "Brush.Danger");
        TestGlyph.Data = (System.Windows.Media.Geometry)FindResource(ok ? "Icon.Check" : "Icon.Error");
        TestGlyph.SetResourceReference(Controls.Glyph.ForegroundProperty, ok ? "Brush.Success" : "Brush.Danger");
        TestText.Text = (ok ? "Test passed. " : "Test failed. ") + text;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var p = Collect();
        if (!Validate(p))
        {
            return;
        }

        _profile = p;
        DialogResult = true;
    }
}

using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Infrastructure.Sftp;

namespace BridgeTransfer.IntegrationTests;

public class AuthAndHostKeyTests
{
    [FixtureFact]
    public async Task Key_authentication_with_ed25519_rsa_pem_and_ecdsa()
    {
        await using var ws = new Workspace();
        foreach (var key in new[] { "BT_IT_KEY", "BT_IT_KEY_RSA_PEM", "BT_IT_KEY_ECDSA" })
        {
            ws.Profile.PrivateKeyPath = Env.Get(key);
            var store = await ws.ConnectAsync();
            Assert.True(store.IsConnected);
            Assert.NotNull(await store.GetEntryAsync(ws.RemoteDir, default));
        }
    }

    [FixtureFact]
    public async Task Encrypted_key_requires_correct_passphrase_and_is_not_modified()
    {
        await using var ws = new Workspace();
        var keyPath = Env.Get("BT_IT_KEY_ENC");
        var before = Workspace.Sha256(keyPath);
        ws.Profile.PrivateKeyPath = keyPath;

        var missing = await Assert.ThrowsAsync<KeyFileException>(() => ws.ConnectAsync(creds: new SessionCredentials(null, null)));
        Assert.Equal(KeyFileProblem.PassphraseRequired, missing.Problem);

        var wrong = await Assert.ThrowsAsync<KeyFileException>(() => ws.ConnectAsync(creds: new SessionCredentials(null, "wrong passphrase")));
        Assert.Equal(KeyFileProblem.WrongPassphraseOrCorrupt, wrong.Problem);
        Assert.DoesNotContain("wrong passphrase", wrong.ToString());

        var ok = await ws.ConnectAsync(creds: new SessionCredentials(null, Env.Get("BT_IT_KEY_ENC_PASSPHRASE")));
        Assert.True(ok.IsConnected);
        Assert.Equal(before, Workspace.Sha256(keyPath));
    }

    [FixtureFact]
    public async Task Unsupported_key_file_is_explained_and_left_unchanged()
    {
        await using var ws = new Workspace();
        var fake = Path.Combine(ws.LocalDir, "key.ppk");
        File.WriteAllText(fake, "PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none\nComment: x\nPublic-Lines: 1\nAAAA\n");
        var before = Workspace.Sha256(fake);
        ws.Profile.PrivateKeyPath = fake;
        var ex = await Assert.ThrowsAsync<KeyFileException>(() => ws.ConnectAsync());
        Assert.True(ex.Problem is KeyFileProblem.UnsupportedFormat or KeyFileProblem.WrongPassphraseOrCorrupt);
        Assert.Equal(before, Workspace.Sha256(fake));
    }

    [FixtureFact]
    public async Task Password_authentication_and_rejected_credentials()
    {
        await using var ws = new Workspace(auth: AuthenticationKind.Password);
        var ok = await ws.ConnectAsync();
        Assert.True(ok.IsConnected);

        var bad = await Assert.ThrowsAsync<StoreException>(() => ws.ConnectAsync(creds: new SessionCredentials("not-the-password", null)));
        Assert.Equal(FailureKind.Authentication, bad.Kind);
        Assert.DoesNotContain("not-the-password", bad.ToString());

        ws.Profile.Authentication = AuthenticationKind.PrivateKey;
        ws.Profile.PrivateKeyPath = Env.Get("BT_IT_KEY_UNAUTHORIZED");
        var unauthorized = await Assert.ThrowsAsync<StoreException>(() => ws.ConnectAsync(creds: new SessionCredentials(null, null)));
        Assert.Equal(FailureKind.Authentication, unauthorized.Kind);
    }

    [FixtureFact]
    public async Task Unknown_host_key_declined_sends_nothing_and_stores_nothing()
    {
        await using var ws = new Workspace();
        var logPath = Path.Combine(Path.GetDirectoryName(Env.Get("BT_IT_KEY"))!, "sshd.log");
        static int AuthLines(string path) => File.ReadAllLines(path).Count(l => l.Contains("Accepted ") || l.Contains("Failed ") || l.Contains("Postponed "));
        var before = AuthLines(logPath);
        PresentedHostKey? shown = null;
        var ex = await Assert.ThrowsAsync<HostKeyRejectedException>(() => ws.ConnectAsync((k, _) =>
        {
            shown = k;
            return Task.FromResult(false);
        }));
        Assert.NotNull(shown);
        Assert.Equal("ssh-ed25519", shown!.Algorithm);
        Assert.Equal(Env.Get("BT_IT_HOST_FINGERPRINT"), shown.DisplayFingerprint);
        Assert.Empty(ws.KnownHosts.GetAll());

        // No authentication attempt reached the server for this connection.
        await Task.Delay(300);
        Assert.Equal(before, AuthLines(logPath));
    }

    [FixtureFact]
    public async Task Accepted_key_reconnects_silently_and_changed_key_blocks()
    {
        await using var ws = new Workspace();
        var prompts = 0;
        await ws.ConnectAsync((_, _) =>
        {
            prompts++;
            return Task.FromResult(true);
        });
        Assert.Equal(1, prompts);
        Assert.Single(ws.KnownHosts.GetAll());

        await ws.ConnectAsync((_, _) =>
        {
            prompts++;
            return Task.FromResult(true);
        });
        Assert.Equal(1, prompts);

        // Simulate the server key changing by trusting a different key for the same host and port.
        var records = ws.KnownHosts.GetAll().ToList();
        records[0].PublicKeyBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        records[0].FingerprintSha256 = "old-fingerprint";
        ws.KnownHosts.Save(records);

        var changed = await Assert.ThrowsAsync<HostKeyChangedException>(() => ws.ConnectAsync((_, _) =>
        {
            prompts++;
            return Task.FromResult(true);
        }));
        Assert.Equal(1, prompts);
        Assert.Equal("old-fingerprint", changed.Check.TrustedKeys.Single().FingerprintSha256);
        Assert.Equal(Env.Get("BT_IT_HOST_FINGERPRINT"), changed.Check.Presented.DisplayFingerprint);
    }

    [FixtureFact]
    public async Task Transfer_sessions_never_accept_unknown_keys()
    {
        await using var ws = new Workspace();
        var factory = new SftpTransferSessionFactory(k => new ConnectionTicket(ws.Key, ws.Profile, ws.Credentials, ws.Options), ws.Verifier, ws.Local);
        var ex = await Assert.ThrowsAsync<HostKeyRejectedException>(() => factory.OpenAsync(ws.Key, default));
        Assert.Empty(ws.KnownHosts.GetAll());

        var otherServer = ws.Key with { Port = 9999 };
        var refused = await Assert.ThrowsAsync<StoreException>(() => new SftpTransferSessionFactory(k => k == ws.Key ? new ConnectionTicket(ws.Key, ws.Profile, ws.Credentials, ws.Options) : null, ws.Verifier, ws.Local).OpenAsync(otherServer, default));
        Assert.Contains("no longer active", refused.Message);
    }

    [FixtureFact]
    public async Task Connecting_can_be_cancelled_while_waiting_for_host_key_decision()
    {
        await using var ws = new Workspace();
        using var cts = new CancellationTokenSource();
        var promptShown = new TaskCompletionSource();
        var connect = SftpConnector.ConnectAsync(ws.Profile, ws.Credentials, ws.Verifier, async (_, ct) =>
        {
            promptShown.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }, ws.Options, cts.Token);
        await promptShown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<StoreException>(() => connect.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(ex.Kind is FailureKind.Cancelled or FailureKind.HostKeyRejected);
        Assert.Empty(ws.KnownHosts.GetAll());
    }

    [FixtureFact]
    public async Task Unreachable_port_is_a_clear_network_error()
    {
        await using var ws = new Workspace();
        ws.Profile.Port = 1;
        var ex = await Assert.ThrowsAsync<StoreException>(() => ws.ConnectAsync());
        Assert.Equal(FailureKind.Transient, ex.Kind);
        Assert.Contains("refused", ex.Message);
    }
}

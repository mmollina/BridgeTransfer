using BridgeTransfer.Core.Security;

namespace BridgeTransfer.UnitTests;

public class SecurityTests
{
    private sealed class MemoryKnownHosts : IKnownHostsStore
    {
        private List<KnownHostRecord> _records = new();

        public IReadOnlyList<KnownHostRecord> GetAll() => _records.ToList();

        public void Save(IReadOnlyList<KnownHostRecord> records) => _records = records.ToList();
    }

    private static PresentedHostKey Key(string host, string blob, int port = 22, string alg = "ssh-ed25519") => new(host, port, alg, blob, "fp-" + blob);

    [Fact]
    public void Unknown_then_trusted_then_changed()
    {
        var v = new HostKeyVerifier(new MemoryKnownHosts());
        Assert.Equal(HostKeyStatus.Unknown, v.Check(Key("VM.local", "AAA")).Status);
        v.TrustNew(Key("VM.local", "AAA"));
        Assert.Equal(HostKeyStatus.Trusted, v.Check(Key("vm.local", "AAA")).Status);
        var changed = v.Check(Key("vm.local", "BBB"));
        Assert.Equal(HostKeyStatus.Changed, changed.Status);
        Assert.Equal("fp-AAA", changed.TrustedKeys.Single().FingerprintSha256);
    }

    [Fact]
    public void Trust_is_per_port_and_algorithm_must_match()
    {
        var v = new HostKeyVerifier(new MemoryKnownHosts());
        v.TrustNew(Key("h", "AAA", 22));
        Assert.Equal(HostKeyStatus.Unknown, v.Check(Key("h", "AAA", 2222)).Status);
        Assert.Equal(HostKeyStatus.Changed, v.Check(Key("h", "AAA", 22, "ssh-rsa")).Status);
    }

    [Fact]
    public void Changed_key_cannot_be_trusted_without_explicit_forget()
    {
        var v = new HostKeyVerifier(new MemoryKnownHosts());
        v.TrustNew(Key("h", "AAA"));
        Assert.Throws<InvalidOperationException>(() => v.TrustNew(Key("h", "BBB")));
        Assert.Equal(1, v.Forget("H", 22));
        v.TrustNew(Key("h", "BBB"));
        Assert.Equal(HostKeyStatus.Trusted, v.Check(Key("h", "BBB")).Status);
    }

    [Fact]
    public void Registered_secrets_and_key_material_are_redacted()
    {
        using (SecretRedactor.Register("hunter2!"))
        {
            var text = SecretRedactor.Redact("auth failed with hunter2! for user");
            Assert.DoesNotContain("hunter2!", text);
        }

        Assert.Contains("hunter2!", SecretRedactor.Redact("hunter2!"));
        var pem = "x -----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY----- y";
        Assert.DoesNotContain("abc", SecretRedactor.Redact(pem));
        Assert.DoesNotContain("s3cret", SecretRedactor.Redact("password=s3cret other"));
        Assert.DoesNotContain("s3cret", SecretRedactor.Redact("Passphrase: \"s3cret\""));
    }
}

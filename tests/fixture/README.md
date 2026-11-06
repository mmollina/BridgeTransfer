# SFTP test fixture

The integration tests need a real OpenSSH SFTP server. `start-sftp-fixture.sh` starts an
isolated one on `127.0.0.1` without touching any existing SSH configuration:

- two private `sshd` instances on ports 2222 and 2223 (2223 refuses `posix-rename`, to test
  servers that cannot replace a file in one step),
- a dedicated account `bttest` with no login shell (`/usr/sbin/nologin`, SFTP only),
- throwaway host keys, client keys (Ed25519, encrypted Ed25519, RSA PEM, ECDSA, PuTTY .ppk if
  `puttygen` is installed, plus one unauthorized key) and a random password.

Everything is written to `tests/fixture/.run/` (git-ignored), including `fixture.env`, which the
tests read. Run it only on a disposable Linux machine, container or WSL instance, as root:

```bash
sudo tests/fixture/start-sftp-fixture.sh
```

```bash
dotnet test tests/BridgeTransfer.IntegrationTests
```

```bash
sudo tests/fixture/stop-sftp-fixture.sh
```

On Windows, run the fixture inside WSL (Ubuntu with `openssh-server`), then point the tests at it
with `BT_IT_FIXTURE=\\wsl$\Ubuntu\path\to\tests\fixture\.run\fixture.env`; WSL forwards
`127.0.0.1` to the Windows host. Without a fixture the integration tests report as skipped.

Set `BT_IT_LARGE=1` to include the file-larger-than-4-GB round trip (needs about 13 GB of disk).

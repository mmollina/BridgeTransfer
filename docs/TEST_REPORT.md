# Test report — BridgeTransfer 1.0.0

Date: 2026-11-06. This report separates what was **executed** from what is **prepared or blocked**.

## Deliverable

| | |
|---|---|
| File | `dist/BridgeTransfer.exe` |
| Size | 137,627,660 bytes |
| SHA-256 | `ed531f6bf7b0dd863c2c19a62df93cc283cfa628b2371e6fa6d2425439d7c787` |
| Version | 1.0.0 (file version 1.0.0.0) |
| Build | Release, win-x64, self-contained, single file, not trimmed, .NET 10.0.14 runtime bundled |
| Produced by | `scripts/publish.ps1` (PowerShell 7.6 on Linux), which ran all tests first |
| Signed | No |

Static checks of the exe (executed): PE32+ x64, subsystem 2 (Windows GUI, so no console window),
8 embedded icon sizes, version resource (ProductName BridgeTransfer, FileVersion 1.0.0.0), embedded
manifest with `asInvoker`, `PerMonitorV2` DPI awareness, `longPathAware` and Windows 10/11
compatibility. The publish script also checks that the output is a single file and that it contains
no OpenSSH private-key material or test-fixture variable names.

## Environment

- Ubuntu 24.04.5 LTS, x86_64 cloud container (4 vCPU, 15 GB RAM). **No Windows machine.**
- .NET SDK 10.0.114 (Ubuntu package), runtime 10.0.14; `EnableWindowsTargeting` for the WPF project.
- SSH.NET 2026.0.0, CommunityToolkit.Mvvm 8.4.2, xUnit 2.9.3.
- Test SFTP server: OpenSSH 9.6p1 (`internal-sftp`), started by `tests/fixture/start-sftp-fixture.sh`
  on 127.0.0.1:2222 and :2223 (the second refuses `posix-rename`), account without a login shell.
- UI checks: Wine 9.0 with Xvfb, the same application code published as a folder (Wine cannot start
  .NET single-file bundles). Open-source fonts were registered under the names Segoe UI, Arial and
  Consolas because Wine has no Microsoft fonts.

## Executed: automated tests

All runs below passed on the final code.

### Unit tests — 97 passed, 0 failed

`tests/BridgeTransfer.UnitTests` (in-memory file system with fault injection; no network):

- Paths: Linux path joining, parent, normalization, case-sensitive containment, traversal rejection,
  backslash as an ordinary Linux character, local root containment and stream syntax, Windows
  reserved names, invalid characters, trailing dot/space, case collisions, breadcrumbs.
- Transfer engine and queue: folder semantics (`project` into `/data` → `/data/project`, empty
  folders), partial file never visible under the final name, Ask/Skip/Replace/Keep both, batch
  "apply to remaining" never replacing a newer destination, file/folder type conflict, unsupported
  replace keeps the original, transient failure retried with a new session, bounded retries,
  authentication not retried, cancellation leaves the destination intact, source changing during
  transfer, overlapping jobs never writing the same file at once, unknown staging-like files never
  deleted, uncertain rename reconciled without copying again, Windows-invalid names / case
  collisions / links skipped on download, unreadable sub-folder reported, destination folder link
  refused, local disk full detected, retry of failed items, staging file journaled before creation,
  sync item skipped when the destination appeared after preview, 64-bit byte counters.
- Comparison: all categories and default selections, 2-second tolerance, "Same metadata" wording,
  case collisions and invalid names on download, unreadable subtrees on either side, missing time,
  case-variant destination names, stale preview put back into review, plan expectations,
  10,000-item comparison.
- Security: unknown / trusted / changed host keys, per-port trust, explicit forget, redaction of
  registered secrets, PEM blocks and `password=` values.
- Persistence: settings round trip without secrets, corrupt settings restored from backup (corrupt
  copy kept), defaults without backup, newer schema not overwritten, value clamping, known hosts,
  queue journal round trip, rotating redacted log.

### SFTP integration tests — 24 passed, 0 failed

`tests/BridgeTransfer.IntegrationTests` (real SSH.NET against the real OpenSSH fixture). 23 run by
default; the >4 GB test runs with `BT_IT_LARGE=1` and was executed separately on the final code.

| Area | What was verified |
|---|---|
| Key authentication | Ed25519, RSA (PEM), ECDSA keys sign in |
| Encrypted keys | OpenSSH encrypted key: passphrase required, wrong passphrase reported, correct one works; key file unchanged (SHA-256) |
| PuTTY keys | `.ppk` v3 Ed25519, v2 RSA, and encrypted v3 (missing/wrong/correct passphrase); key file unchanged |
| Unsupported key | Malformed key file explained; file unchanged |
| Password | Correct password works; wrong password and an unauthorized key are rejected; the password does not appear in error text |
| Unknown host key | Declining sends no login data (no new authentication line in the server log) and stores nothing; fingerprint matches `ssh-keygen -l` |
| Trusted / changed key | Second connection does not ask again; a changed key blocks the connection and shows both fingerprints |
| Transfer connections | Never accept an unknown key; refuse work for a server that is no longer the active one |
| Cancel while connecting | Cancelling while the identity question is open stops the connection; nothing trusted |
| Network errors | Refused port reported as a network problem |
| Byte equality | Upload and download of a tree with an empty file, empty folders, nested folders, spaces, Unicode names (`Ünïcödé 数据 ñ.csv`), mixed line endings, a 3.5 MB binary and 400 small files: SHA-256 of every file identical both ways; modification times preserved; no staging files left |
| Cancellation | 400 MB upload and download cancelled midway: existing destination files unchanged, no staging files left |
| Interruption | All TCP connections cut during a 150 MB upload: retried on a new connection, SHA-256 identical, staging file of the failed attempt removed |
| Existing files | Replace (via posix-rename), Keep both (`k (2).txt`) and Skip behave as chosen |
| No safe replace | On the server that refuses posix-rename, Replace fails with an explanation and the original is kept; new files still work |
| Permissions | Writing into a read-only folder fails as "permission denied"; a folder that cannot be listed is reported, not treated as empty |
| Source changes | A file appended to during upload fails as "source changed"; nothing written under the final name |
| Large folders | 10,000 remote entries listed in 127 ms, 10,000 local entries in 29 ms (loopback) |
| Compare and apply | Categories on real data, scan does not modify anything, selected plan applied through the queue, re-comparison shows "Same metadata" for everything copied, destination-only and destination-newer files untouched |
| Stale plan | Source and destination changes after comparison put items into review; a destination created after revalidation is skipped at execution |
| Links | Remote links listed as links (also links to folders, to files outside the tree and loops), skipped during download, never written through (folder link refused, file link cannot be replaced) |
| Windows names | `a:b.txt`, `CON`, `aux.log`, trailing dot/space, `q?.txt`, `back\slash.txt` skipped with reasons; `Data.csv`/`data.csv` reported as a case collision; nothing renamed |
| Incomplete comparison | Permission-denied folder makes the comparison incomplete and blocks its items |
| > 4 GB | 4,295,090,753-byte file uploaded and downloaded with identical SHA-256 (about 12 s up, 32 s down over loopback; not a speed claim for real networks) |

## Executed: interface checks under Wine (folder build, not the single-file exe)

Driven with mouse and keyboard automation against the fixture; screenshots in `docs/screenshots/`.

- Disconnected state, connection editor, first-connection fingerprint dialog, Trust and connect,
  remote browsing (link shown distinctly), error state for a missing remote folder, empty-folder
  wording.
- Upload of a 3,000,000,000-byte file through the UI: confirmation with destination paths, queue
  with progress/speed/time left; SHA-256 on the VM identical to the source.
- Conflict dialog (light and dark): Keep both created `notes (2).txt`; Replace during a transfer
  followed by *Cancel transfers and exit*: original file kept (same inode, size and time), staging
  file removed, journal empty, clean shutdown logged.
- Unexpected termination (the Wine process was killed mid-upload): next start listed the
  unfinished transfers and the recorded staging file; nothing restarted; *Remove temporary files*
  deleted exactly that one file after connecting.
- Compare folders: scan, preview, default selection, apply, result summary, re-comparison.
- Show-hidden toggle (both panels, persisted), light and dark themes, settings window.
- Window sizes: fits a 1366×768 screen at 100%; renders sharply at 150% (1920×1080) and 200%
  (2560×1600) system scaling.

Defects found this way and fixed: Compare window's Close button had no effect (non-modal window);
Apply button stayed disabled after scanning; a link shown as "folder" in the comparison; clipped
button captions and date column; missing show-hidden toggle; cramped interrupted-transfers dialog;
misleading text in the close dialog; Curve25519/ECDH key exchange failing on providers without
support (added a logged fallback to finite-field Diffie-Hellman).

Wine-only problems (not application defects): Wine cannot start .NET single-file bundles
(`Incorrect alignment`, reproduced with a 10-line app); WPF crashes on missing system fonts until
substitutes are registered; Wine's crypto provider lacks ECDH; occasional X11 `BadMatch` crashes
when WPF opens transparent tooltip popups on the virtual display.

## Not executed (blocked or prepared)

| Check | Status |
|---|---|
| Launch `dist/BridgeTransfer.exe` on Windows 10/11 | **Blocked**: no Windows machine available; Wine cannot run single-file bundles |
| Run without a .NET runtime installed, as a non-administrator, from a folder with spaces, from another working directory | **Blocked** (same reason). The CI workflow below performs the folder-with-spaces / other-working-directory launch |
| No console window appears | Verified statically (GUI subsystem); not observed on Windows |
| Real Windows rendering with Segoe UI, Windows title bar, DPI changes between monitors | **Blocked** (Wine with substitute fonts only) |
| Drag from Windows Explorer onto the Linux panel | **Not tested** |
| NTFS behaviour: rename with replace, case-insensitive collisions, reserved names, UNC paths, long paths, OneDrive placeholder files | Logic unit-tested; **not executed on NTFS** |
| Windows crypto provider (CNG) key exchange | **Not tested**; Linux tests used SSH.NET's default path and Wine used the fallback |
| Screen reader / UI Automation tools | **Not tested**; accessible names are set on controls |
| Connection to your own VM | **Not tested** (no access; intentionally) |
| `.github/workflows/windows-build.yml` | **Prepared, not run** (would build, unit-test, publish and smoke-launch on `windows-latest`) |

The smallest step to close the main gap: on any Windows 10/11 x64 PC, copy `dist\BridgeTransfer.exe`
to a new folder and double-click it (or run the prepared workflow in a private GitHub repository).
If it starts, connect to the VM and do one upload, one download and one comparison.

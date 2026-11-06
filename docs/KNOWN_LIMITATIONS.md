# Known limitations

This page states exactly what BridgeTransfer 1.0.0 does and does not guarantee.

## Verification status

- **The single-file `BridgeTransfer.exe` has not been launched on Windows.** It was built on Linux
  (cross-compiled with the official .NET 10 SDK). Its PE header, icon, version information and
  manifest were inspected and are correct, but a real Windows run is still required. Wine cannot
  start any .NET single-file bundle (it fails with `Incorrect alignment`, also for a 10-line test
  app), so the interface was exercised with the same code published as a folder under Wine.
- The prepared GitHub Actions workflow (`.github/workflows/windows-build.yml`) builds the exe on a
  real Windows runner and checks that it starts and closes cleanly. It has not been run.
- The executable is not code-signed. Windows SmartScreen may show "Windows protected your PC" the
  first time; choose *More info → Run anyway* only if you built or obtained the file yourself.

## Private keys and sign-in

Supported key files (read only, never modified or copied):

| Format | Key types | Passphrase |
|---|---|---|
| OpenSSH (`-----BEGIN OPENSSH PRIVATE KEY-----`, ssh-keygen default) | Ed25519, ECDSA 256/384/521, RSA | yes: aes*-ctr, aes*-cbc, aes*-gcm, chacha20-poly1305, 3des-cbc |
| PuTTY `.ppk` version 2 and 3 | Ed25519, ECDSA, RSA | yes |
| PEM / PKCS#8 (`BEGIN RSA/EC PRIVATE KEY`, `BEGIN PRIVATE KEY`) | RSA, ECDSA, Ed25519 | yes (traditional PEM: DES-EDE3, AES-128/192/256-CBC) |

Tested against the fixture: OpenSSH Ed25519 (plain and encrypted), RSA PEM, ECDSA, PuTTY v3
Ed25519 (plain and encrypted) and PuTTY v2 RSA.

Not supported: DSA keys, FIDO/security keys (`sk-ssh-ed25519@openssh.com`), certificates,
Pageant/ssh-agent, Windows OpenSSH agent, `~/.ssh/config` import, jump hosts and proxies.
Password sign-in supports `password` and `keyboard-interactive` prompts that ask for a password;
two-factor prompts are not supported.

Passwords and passphrases are never saved. They are held in memory for the current connection
(transfer workers need them to open their own connections) and discarded on disconnect. .NET
strings cannot be reliably wiped from memory.

## Server identity (host keys)

- A host key is trusted only after you confirm its SHA-256 fingerprint. Trust is stored per host
  name and port in `%LOCALAPPDATA%\BridgeTransfer\known_hosts.json`.
- If the server later presents any other key, the connection is blocked. Use *Forget trusted key*
  in the connection settings to accept a new key deliberately.
- `localhost`, `127.0.0.1` and a DNS name for the same machine are different entries.

## Algorithms

- Transport compression is turned off, so data is sent exactly as stored.
- SHA-1 based key exchanges (`diffie-hellman-group1-sha1`, `group14-sha1`, `group-exchange-sha1`) are
  not offered. Very old SSH servers that only support those cannot be used.

## Key exchange fallback

SSH.NET uses Windows' built-in crypto provider for elliptic-curve key exchange. If that provider
fails (seen under Wine; possible on locked-down systems), BridgeTransfer retries the connection
once with finite-field Diffie-Hellman (group-exchange-sha256, group16-sha512, group14-sha256;
2048 bits or more) and writes a warning to the log. Host-key verification is unchanged.

## Transfers

- Every file is written to an application-owned temporary name in the destination folder
  (`.bt-staging-<job>-<file>-<random>.part`), closed, checked (byte count equals the source size;
  temporary file size equals the byte count; source size and time unchanged) and only then renamed.
- **New files** are renamed without replacing anything. **Replacing** an existing file uses one
  rename that swaps the old file out: `posix-rename@openssh.com` on the VM, and a same-folder
  `MoveFileEx(REPLACE_EXISTING)` rename on Windows. The old file stays until that rename.
- If the server cannot do that rename (no `posix-rename` extension, or it refuses), the existing
  file is **kept**, the temporary file is removed, and the transfer is reported as failed. The
  original is never deleted first to make room.
- There is a short window between the final check and the rename in which another program could
  create or change the destination; the rename then fails or the result is reported. No stronger
  atomicity than the file system or SFTP server provides is claimed, and no durability guarantee
  beyond flushing local files to disk before the rename.
- The size check detects incomplete writes. It is **not** a content checksum. BridgeTransfer does
  not offer a user-facing hash verification in version 1 (the integration tests verify content with
  SHA-256).
- Interrupted files **restart from byte 0**. There is no resume.
- "Replace if newer / if the size differs / if newer or the size differs" compare the size and the
  modification time (2-second tolerance). If a rule depends on "newer" and a time is unknown, the
  file is asked about. A newer destination is never replaced by a batch-wide or preset choice.
- Transient network errors are retried up to 3 attempts per file (delays 2 s, 6 s), each time on a
  new connection with the same host-key checks. Login failures, changed host keys, permission
  errors, full disks, a source that changed, and unresolved conflicts are not retried.
- If the connection drops during the final rename, the next attempt checks what actually happened
  (temporary file gone and final file of the right size and time → completed; otherwise the file is
  copied again) instead of repeating the rename blindly.
- Free space is checked before writing to this computer. On the VM it is not checked unless the
  server supports `statvfs@openssh.com`; a full VM disk is reported as a failure.
- Modification times are preserved (whole seconds on the VM). Failure to set the time is a
  warning, not a failed transfer.
- Permissions, owners, ACLs, extended attributes and alternate data streams are not copied.
- File contents are copied byte for byte; there is no line-ending or encoding conversion and SSH
  compression is not enabled.
- One to ten files at a time (default 2), each on its own SFTP connection. Folder scanning uses one
  connection per transfer.
- Throughput for many small files is limited by round trips: with SSH.NET each file needs roughly a
  dozen request/response exchanges (SSH.NET resolves the remote path before most operations, and the
  temporary-file-then-rename procedure adds a few more). Measured through 30 ms of added network
  delay: 200 files of 50 KB took 37 s with 2 at a time, 19 s with 4 and 14.5 s with 8; a single
  200 MB file ran at 46 MB/s up and 62 MB/s down. Tools that write directly into the final file name
  (for example FileZilla) need fewer exchanges per small file.
- Existing files at the destination are found from one listing per destination folder, made while the
  folders are prepared. Skip/ask decisions use that listing; any file that is actually written is
  still protected by the exclusive create and the no-replace rename, so a stale listing costs time,
  never data.
- On OpenSSH servers (detected from the server's version string) the extra existence check before
  the final rename is omitted, because OpenSSH's SFTP rename refuses to overwrite an existing name.
  Other servers keep the check.
- The queue journal (unfinished transfers and temporary files) survives an application crash but is
  not forced to disk on every write, so a power failure can lose its last entries. Settings and
  trusted host keys are always flushed to disk.

## Links, special files and names

- Remote symbolic links and local symbolic links/junctions are shown as links and **skipped** when
  copying folders or comparing; they are never followed. A link's own name is listed with a reason.
- Writing through a link at the destination is refused: a destination folder that is a link stops
  that folder; a file whose destination is a link cannot be replaced (Skip or Keep both only).
- On the VM, link detection relies on the server's `realpath`. Protection against a malicious or
  concurrently changing server file system is not complete.
- Special files (devices, sockets, pipes) are skipped.
- Downloads skip names Windows cannot store (reserved names such as `CON` or `aux.txt`, `< > : " / \
  | ? *`, control characters, trailing space or period) and names that differ only by upper/lower
  case in the same folder. Nothing is renamed automatically.

## One-way synchronization

- Compares relative path, type, size and modification time. A tolerance of **2 seconds** is used
  because SFTP v3 transfers whole seconds and some file systems store 2-second times.
- "Same metadata" means equal size and time within the tolerance. It does not prove identical content.
- Only copies and replaces, in one direction. **Nothing is ever deleted.** No mirror mode,
  bidirectional sync, background watching or scheduling.
- Destination-newer, unclear-time, conflict, unsupported-name, link and unreadable entries are never
  selected automatically. Replacing a newer destination needs an extra confirmation.
- Folders that cannot be read make the comparison incomplete for that subtree; its items are
  blocked, not treated as missing.
- Selected items are re-checked just before applying and again before each file is written; items
  that changed are skipped and marked for review.
- The whole comparison is held in memory (roughly a few hundred bytes per item).

## Application

- One instance per Windows user. A second launch brings the first window to the front.
- Unfinished transfers are listed at the next start and can be queued again (with fresh checks) or
  their recorded temporary files removed. Nothing restarts automatically.
- Deleting files or folders is not part of version 1.
- Dragging from the Linux panel to Windows Explorer is not supported (drag between the two panels).
- No telemetry, accounts, cloud services or auto-update.

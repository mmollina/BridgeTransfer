# Implementation plan (as executed)

1. Environment: Ubuntu 24.04 cloud container, no Windows. Installed .NET SDK 10.0.114 (Ubuntu
   package) and confirmed it can cross-publish a self-contained single-file WPF `win-x64` exe
   (`EnableWindowsTargeting`). Wine 9.0 used only for rendering and smoke checks of a folder build.
2. Core library (UI-free): path rules, Windows name rules, transfer planner, job model, transfer
   executor (staging → verify → promote), queue/scheduler, comparison engine, host-key policy,
   redaction. Unit tests against an in-memory store with fault injection.
3. Infrastructure: SSH.NET SFTP store, local file store, atomic JSON settings, known hosts,
   journal, rotating redacted log.
4. Integration tests against an isolated OpenSSH fixture (real SSH.NET, real sshd).
5. WPF application: shell, panels, queue, dialogs, compare window, light/dark themes.
6. Visual inspection under Wine, fixes, packaging scripts, single-file publish, documentation.

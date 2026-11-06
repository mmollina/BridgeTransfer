using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Paths;
using BridgeTransfer.Core.Storage;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace BridgeTransfer.Infrastructure.Sftp;

/// <summary>
/// <see cref="IFileStore"/> over one <see cref="SftpClient"/>. Calls are serialized because concurrent
/// use of one client is not assumed to be safe. Uses only SFTP requests; never runs shell commands.
/// </summary>
public sealed class SftpFileStore : IFileStore, IDisposable
{
    private readonly SftpClient _client;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly bool _ownsClient;

    public SftpFileStore(SftpClient client, bool ownsClient = true)
    {
        _client = client;
        _ownsClient = ownsClient;
    }

    public EndpointSide Side => EndpointSide.Remote;

    public bool IsConnected => _client.IsConnected;

    /// <summary>The login directory reported by the server.</summary>
    public string HomeDirectory => _client.WorkingDirectory;

    public string Combine(string directory, string name) => RemotePath.Combine(directory, name);

    public string CombineRelative(string root, string relativePath) => RemotePath.CombineRelative(root, relativePath);

    public string? GetParent(string path) => RemotePath.GetParent(path);

    public string GetName(string path) => RemotePath.GetName(path);

    public string LockKey(string path) => path;

    private async Task<T> RunAsync<T>(string? path, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not StoreException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            throw SftpErrors.Translate(ex, path);
        }
        finally
        {
            _lock.Release();
        }
    }

    private Task RunAsync(string? path, Func<CancellationToken, Task> action, CancellationToken ct) =>
        RunAsync<bool>(path, async c =>
        {
            await action(c).ConfigureAwait(false);
            return true;
        }, ct);

    private static FileEntry ToEntry(string fullPath, string name, SftpFileAttributes a)
    {
        var kind = a.IsRegularFile ? EntryKind.File : a.IsDirectory ? EntryKind.Directory : a.IsSymbolicLink ? EntryKind.SymbolicLink : EntryKind.Other;
        return new FileEntry(name, fullPath, kind, kind == EntryKind.File ? a.Size : 0, a.LastWriteTimeUtc, name.StartsWith('.'));
    }

    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken) =>
        RunAsync<FileEntry?>(path, async ct =>
        {
            ISftpFile file;
            try
            {
                file = await _client.GetAsync(path, ct).ConfigureAwait(false);
            }
            catch (Renci.SshNet.Common.SftpPathNotFoundException)
            {
                return null;
            }

            var name = RemotePath.GetName(path);
            var normalized = RemotePath.TrimTrailingSeparators(path);

            // SSH.NET resolves the path with realpath before lstat, which follows links. If the
            // resolved path differs from parent-resolved + name, the last component is itself a link.
            if (!string.Equals(file.FullName, normalized, StringComparison.Ordinal) && normalized != "/")
            {
                var parent = RemotePath.GetParent(normalized)!;
                var parentFile = await _client.GetAsync(parent, ct).ConfigureAwait(false);
                var expected = RemotePath.Combine(parentFile.FullName, name);
                if (!string.Equals(file.FullName, expected, StringComparison.Ordinal))
                {
                    return new FileEntry(name, normalized, EntryKind.SymbolicLink, 0, file.LastWriteTimeUtc, name.StartsWith('.'), file.FullName);
                }
            }

            return ToEntry(normalized, name, file.Attributes);
        }, cancellationToken);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<FileEntry>>(directory, async ct =>
        {
            var list = new List<FileEntry>();
            var dir = RemotePath.TrimTrailingSeparators(directory);
            await foreach (var f in _client.ListDirectoryAsync(dir, ct).ConfigureAwait(false))
            {
                if (f.Name is "." or "..")
                {
                    continue;
                }

                if (RemotePath.ValidateChildName(f.Name) is not null)
                {
                    continue; // A server must never return names containing '/'. Ignore them defensively.
                }

                // Build the path from the requested folder so browsing through a linked folder keeps the user's path.
                list.Add(ToEntry(RemotePath.Combine(dir, f.Name), f.Name, f.Attributes));
            }

            return list;
        }, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(path, async ct =>
        {
            try
            {
                await _client.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
            }
            catch (Renci.SshNet.Common.SftpException)
            {
                if (await ExistsUnlockedAsync(path, ct).ConfigureAwait(false))
                {
                    throw new StoreException(FailureKind.AlreadyExists, $"{path} already exists on the VM.");
                }

                throw;
            }
        }, cancellationToken);

    private async Task<bool> ExistsUnlockedAsync(string path, CancellationToken ct)
    {
        try
        {
            await _client.GetAttributesAsync(path, ct).ConfigureAwait(false);
            return true;
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return false;
        }
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        RunAsync<Stream>(path, async ct => await _client.OpenAsync(path, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false), cancellationToken);

    public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken) =>
        RunAsync<Stream>(path, async ct => await _client.OpenAsync(path, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false), cancellationToken);

    public Task WriteNewFileAsync(string path, Stream content, Action<long> progress, CancellationToken cancellationToken) =>
        RunAsync(path, async ct =>
        {
            // canOverride: false opens with CREATE|EXCL, so an existing file is never truncated.
            var reporter = new SyncProgress<Renci.SshNet.UploadFileProgressReport>(r => progress((long)r.TotalBytesUploaded));
            await _client.UploadFileAsync(content, path, canOverride: false, reporter, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task ReadFileAsync(string path, Stream output, Action<long> progress, CancellationToken cancellationToken) =>
        RunAsync(path, async ct =>
        {
            var reporter = new SyncProgress<Renci.SshNet.DownloadFileProgressReport>(r => progress((long)r.TotalBytesDownloaded));
            await _client.DownloadFileAsync(path, output, reporter, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task MoveNoReplaceAsync(string from, string to, CancellationToken cancellationToken) =>
        RunAsync(to, async ct =>
        {
            if (await ExistsUnlockedAsync(to, ct).ConfigureAwait(false))
            {
                throw new StoreException(FailureKind.AlreadyExists, $"{RemotePath.GetName(to)} already exists on the VM.");
            }

            try
            {
                // Plain SFTP rename. OpenSSH refuses to overwrite an existing target.
                await _client.RenameFileAsync(from, to, ct).ConfigureAwait(false);
            }
            catch (Renci.SshNet.Common.SftpException)
            {
                if (await ExistsUnlockedAsync(to, ct).ConfigureAwait(false))
                {
                    throw new StoreException(FailureKind.AlreadyExists, $"{RemotePath.GetName(to)} already exists on the VM.");
                }

                throw;
            }
        }, cancellationToken);

    public Task MoveReplaceAsync(string from, string to, CancellationToken cancellationToken) =>
        RunAsync(to, ct => Task.Run(() =>
        {
            try
            {
                // posix-rename@openssh.com replaces the target in one step; the old file stays until then.
                _client.RenameFile(from, to, isPosix: true);
            }
            catch (NotSupportedException ex)
            {
                throw new StoreException(FailureKind.ReplaceNotSupported, "The VM's SFTP server does not support replacing a file in one step (posix-rename). The existing file was kept unchanged.", ex);
            }
            catch (Renci.SshNet.Common.SftpPermissionDeniedException ex)
            {
                throw new StoreException(FailureKind.PermissionDenied, "The VM refused to replace the existing file. It was kept unchanged.", ex);
            }
        }, ct), cancellationToken);

    public Task DeleteOwnedFileAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(path, ct => _client.DeleteFileAsync(path, ct), cancellationToken);

    public Task SetModifiedTimeAsync(string path, DateTime modifiedUtc, CancellationToken cancellationToken) =>
        RunAsync(path, async ct =>
        {
            var attrs = await _client.GetAttributesAsync(path, ct).ConfigureAwait(false);
            attrs.LastWriteTimeUtc = DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc);
            await Task.Run(() => _client.SetAttributes(path, attrs), ct).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<long?> GetAvailableSpaceAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync<long?>(path, async ct =>
            {
                var info = await _client.GetStatusAsync(path, ct).ConfigureAwait(false);
                return (long)Math.Min((ulong)long.MaxValue, info.AvailableBlocks * info.BlockSize);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException ex) when (ex.Kind is not (FailureKind.Transient or FailureKind.Cancelled))
        {
            return null;
        }
    }

    /// <summary>Renames a file or folder chosen by the user, never overwriting an existing item.</summary>
    public Task RenameAsync(string from, string to, CancellationToken ct) => MoveNoReplaceAsync(from, to, ct);

    public void Dispose()
    {
        if (_ownsClient)
        {
            try
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
            }
            catch
            {
                // Ignore errors while closing.
            }

            _client.Dispose();
        }
    }

    /// <summary>IProgress that reports synchronously on the calling thread (no SynchronizationContext).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;

        public SyncProgress(Action<T> action) => _action = action;

        public void Report(T value) => _action(value);
    }
}

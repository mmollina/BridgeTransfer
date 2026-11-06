using System.Runtime.InteropServices;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Paths;
using BridgeTransfer.Core.Storage;

namespace BridgeTransfer.Infrastructure.Local;

/// <summary>
/// <see cref="IFileStore"/> for this computer. Stateless and safe to share between workers.
/// Symbolic links and junctions are reported as links; other reparse points such as OneDrive
/// placeholders are treated as ordinary files and folders.
/// </summary>
public sealed partial class LocalFileStore : IFileStore
{
    private const int BufferSize = 1024 * 1024;

    public EndpointSide Side => EndpointSide.Local;

    public string Combine(string directory, string name) => LocalPath.Combine(directory, name);

    public string CombineRelative(string root, string relativePath) => LocalPath.CombineRelative(root, relativePath);

    public string? GetParent(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return Path.GetDirectoryName(trimmed);
    }

    public string GetName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    public string LockKey(string path) => LocalPath.LockKey(path);

    internal static FileEntry? ToEntry(FileSystemInfo info)
    {
        FileAttributes attrs;
        try
        {
            attrs = info.Attributes;
        }
        catch (IOException)
        {
            return null;
        }

        if ((int)attrs == -1)
        {
            return null;
        }

        var isDir = (attrs & FileAttributes.Directory) != 0;
        var kind = isDir ? EntryKind.Directory : EntryKind.File;
        string? target = null;
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            try
            {
                target = info.LinkTarget;
            }
            catch (IOException)
            {
                target = null;
            }

            if (target is not null)
            {
                kind = EntryKind.SymbolicLink;
            }
        }

        var hidden = (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        if (!OperatingSystem.IsWindows() && info.Name.StartsWith('.'))
        {
            hidden = true;
        }

        var length = kind == EntryKind.File ? ((FileInfo)info).Length : 0;
        return new FileEntry(info.Name, info.FullName, kind, length, info.LastWriteTimeUtc, hidden, target);
    }

    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Guard<FileEntry?>(path, () =>
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            if (trimmed.Length == 0)
            {
                trimmed = path;
            }

            FileSystemInfo info = new FileInfo(trimmed);
            if (!info.Exists)
            {
                var di = new DirectoryInfo(path);
                if (!di.Exists)
                {
                    // A broken link exists but neither FileInfo nor DirectoryInfo report it as existing.
                    if (info.LinkTarget is not null)
                    {
                        return new FileEntry(info.Name, info.FullName, EntryKind.SymbolicLink, 0, null, false, info.LinkTarget);
                    }

                    return null;
                }

                info = di;
            }

            return ToEntry(info);
        }));
    }

    public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken cancellationToken) =>
        Task.Run(() => Guard<IReadOnlyList<FileEntry>>(directory, () =>
        {
            var di = new DirectoryInfo(directory);
            if (!di.Exists)
            {
                throw new StoreException(FailureKind.NotFound, $"The folder {directory} does not exist.");
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };
            var list = new List<FileEntry>();
            foreach (var info in di.EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ToEntry(info) is { } e)
                {
                    list.Add(e);
                }
            }

            return list;
        }), cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Guard(path, () =>
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new StoreException(FailureKind.AlreadyExists, $"{GetName(path)} already exists.");
            }

            var parent = GetParent(path);
            if (parent is null || !Directory.Exists(parent))
            {
                throw new StoreException(FailureKind.NotFound, "The parent folder does not exist.");
            }

            Directory.CreateDirectory(path);
            return Task.CompletedTask;
        });

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        Guard<Task<Stream>>(path, () => Task.FromResult<Stream>(new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        })));

    public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken) =>
        Guard<Task<Stream>>(path, () =>
        {
            try
            {
                return Task.FromResult<Stream>(new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous,
                }));
            }
            catch (IOException ex) when (File.Exists(path) || Directory.Exists(path))
            {
                throw new StoreException(FailureKind.AlreadyExists, $"{GetName(path)} already exists.", ex);
            }
        });

    public async Task WriteNewFileAsync(string path, Stream content, Action<long> progress, CancellationToken cancellationToken)
    {
        var output = await CreateNewAsync(path, cancellationToken).ConfigureAwait(false);
        await using (output.ConfigureAwait(false))
        {
            await GuardAsync(path, () => CopyAsync(content, output, progress, cancellationToken)).ConfigureAwait(false);
            await GuardAsync(path, async () =>
            {
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                ((FileStream)output).Flush(flushToDisk: true);
            }).ConfigureAwait(false);
        }
    }

    public async Task ReadFileAsync(string path, Stream output, Action<long> progress, CancellationToken cancellationToken)
    {
        var input = await OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            await GuardAsync(path, () => CopyAsync(input, output, progress, cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task CopyAsync(Stream input, Stream output, Action<long> progress, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            int n;
            while ((n = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
                progress(total);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task MoveNoReplaceAsync(string from, string to, CancellationToken cancellationToken) =>
        Guard(to, () =>
        {
            var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && !string.Equals(from, to, StringComparison.Ordinal);
            if (!caseOnly && (File.Exists(to) || Directory.Exists(to) || new FileInfo(to).LinkTarget is not null))
            {
                throw new StoreException(FailureKind.AlreadyExists, $"{GetName(to)} already exists.");
            }

            try
            {
                if (Directory.Exists(from) && !File.Exists(from))
                {
                    Directory.Move(from, to);
                }
                else
                {
                    File.Move(from, to, overwrite: false);
                }
            }
            catch (IOException ex) when (!caseOnly && (File.Exists(to) || Directory.Exists(to)))
            {
                throw new StoreException(FailureKind.AlreadyExists, $"{GetName(to)} already exists.", ex);
            }

            return Task.CompletedTask;
        });

    public Task MoveReplaceAsync(string from, string to, CancellationToken cancellationToken) =>
        Guard(to, () =>
        {
            // Same-folder rename with replace (MoveFileEx MOVEFILE_REPLACE_EXISTING on Windows,
            // rename(2) elsewhere). The existing file stays in place until it is replaced.
            File.Move(from, to, overwrite: true);
            return Task.CompletedTask;
        });

    public Task DeleteOwnedFileAsync(string path, CancellationToken cancellationToken) =>
        Guard(path, () =>
        {
            File.Delete(path);
            return Task.CompletedTask;
        });

    public Task SetModifiedTimeAsync(string path, DateTime modifiedUtc, CancellationToken cancellationToken) =>
        Guard(path, () =>
        {
            File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc));
            return Task.CompletedTask;
        });

    public Task<long?> GetAvailableSpaceAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dir = Directory.Exists(path) ? path : GetParent(path) ?? path;
                if (!dir.EndsWith('\\'))
                {
                    dir += "\\";
                }

                if (GetDiskFreeSpaceEx(dir, out var free, out _, out _))
                {
                    return Task.FromResult<long?>((long)Math.Min(free, long.MaxValue));
                }

                return Task.FromResult<long?>(null);
            }

            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return Task.FromResult<long?>(root is null ? null : new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception)
        {
            return Task.FromResult<long?>(null);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    /// <summary>Lists drives for the "This PC" view.</summary>
    public static IReadOnlyList<FileEntry> ListDrives()
    {
        var list = new List<FileEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            string label;
            try
            {
                label = d.IsReady && !string.IsNullOrWhiteSpace(d.VolumeLabel) ? $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})" : d.Name;
            }
            catch (IOException)
            {
                label = d.Name;
            }

            list.Add(new FileEntry(label, d.Name, EntryKind.Directory, 0, null));
        }

        return list;
    }

    // ---- error translation ----
    private static T Guard<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not StoreException && ex is not OperationCanceledException)
        {
            throw Translate(ex, path);
        }
    }

    private static Task Guard(string path, Func<Task> action) => Guard<Task>(path, action);

    private static async Task GuardAsync(string path, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not StoreException && ex is not OperationCanceledException)
        {
            throw Translate(ex, path);
        }
    }

    internal static StoreException Translate(Exception ex, string path)
    {
        const int ErrorDiskFull = unchecked((int)0x80070070);
        const int ErrorHandleDiskFull = unchecked((int)0x80070027);
        const int ErrorSharingViolation = unchecked((int)0x80070020);
        const int ErrorLockViolation = unchecked((int)0x80070021);
        const int ENOSPC = 28;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return ex switch
        {
            UnauthorizedAccessException => new StoreException(FailureKind.PermissionDenied, $"Windows denied access to {name}.", ex),
            FileNotFoundException or DirectoryNotFoundException => new StoreException(FailureKind.NotFound, $"{name} was not found.", ex),
            PathTooLongException => new StoreException(FailureKind.InvalidName, $"The path for {name} is too long.", ex),
            IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull or ENOSPC || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase) =>
                new StoreException(FailureKind.DiskFull, "There is not enough free space on this computer.", ex),
            IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation =>
                new StoreException(FailureKind.PermissionDenied, $"{name} is being used by another program.", ex),
            IOException => new StoreException(FailureKind.Unknown, ex.Message, ex),
            ArgumentException => new StoreException(FailureKind.InvalidName, $"'{path}' is not a valid path.", ex),
            NotSupportedException => new StoreException(FailureKind.InvalidName, $"'{path}' is not a valid path.", ex),
            _ => new StoreException(FailureKind.Unknown, ex.Message, ex),
        };
    }

    /// <summary>Renames a file or folder chosen by the user, never overwriting an existing item.</summary>
    public Task RenameAsync(string from, string to, CancellationToken ct) => MoveNoReplaceAsync(from, to, ct);
}

using System.IO;
using BridgeTransfer.App.Services;
using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Paths;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Infrastructure.Local;

namespace BridgeTransfer.App.ViewModels;

public sealed record Crumb(string Label, string Path);

/// <summary>What a file panel browses: this computer or the Linux VM.</summary>
public interface IPanelSource
{
    EndpointSide Side { get; }

    bool IsAvailable { get; }

    string HomePath { get; }

    Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct);

    string? Parent(string path);

    string Combine(string directory, string name);

    /// <summary>Turns typed text into a path. Throws <see cref="ArgumentException"/> with a readable message.</summary>
    string NormalizeInput(string input, string current);

    IReadOnlyList<Crumb> Breadcrumbs(string path);

    string DisplayPath(string path);

    Task CreateFolderAsync(string path, CancellationToken ct);

    Task RenameAsync(string from, string to, CancellationToken ct);

    Task<FileEntry?> StatAsync(string path, CancellationToken ct);

    string? ValidateName(string name);

    /// <summary>Whether files can be created in this folder (false for the "This PC" list).</summary>
    bool CanWriteIn(string path);
}

public sealed class LocalPanelSource : IPanelSource
{
    /// <summary>The "This PC" view listing drives.</summary>
    public const string ComputerRoot = "";

    private readonly LocalFileStore _store;

    public LocalPanelSource(LocalFileStore store) => _store = store;

    public EndpointSide Side => EndpointSide.Local;

    public bool IsAvailable => true;

    public string HomePath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) =>
        path == ComputerRoot ? Task.Run(() => LocalFileStore.ListDrives(), ct) : _store.ListAsync(path, ct);

    public string? Parent(string path)
    {
        if (path == ComputerRoot)
        {
            return null;
        }

        return _store.GetParent(path) ?? ComputerRoot;
    }

    public string Combine(string directory, string name) => _store.Combine(directory, name);

    public string NormalizeInput(string input, string current)
    {
        var text = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        if (text.Length == 0 || string.Equals(text, "This PC", StringComparison.OrdinalIgnoreCase))
        {
            return ComputerRoot;
        }

        if (!Path.IsPathFullyQualified(text))
        {
            if (current == ComputerRoot)
            {
                throw new ArgumentException("Enter a full path such as C:\\Users\\you\\Documents.");
            }

            text = Path.Combine(current, text);
        }

        var full = Path.GetFullPath(text);
        if (full.Length > 3 && !full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            full = Path.TrimEndingDirectorySeparator(full);
        }

        return full;
    }

    public IReadOnlyList<Crumb> Breadcrumbs(string path)
    {
        var list = new List<Crumb> { new("This PC", ComputerRoot) };
        if (path == ComputerRoot)
        {
            return list;
        }

        var root = Path.GetPathRoot(path) ?? path;
        list.Add(new Crumb(root.TrimEnd('\\', '/'), root));
        var rest = path[root.Length..];
        var current = root;
        foreach (var segment in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            list.Add(new Crumb(segment, current));
        }

        return list;
    }

    public string DisplayPath(string path) => path == ComputerRoot ? "This PC" : path;

    public Task CreateFolderAsync(string path, CancellationToken ct) => _store.CreateDirectoryAsync(path, ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => _store.RenameAsync(from, to, ct);

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct) => _store.GetEntryAsync(path, ct);

    public string? ValidateName(string name) => WindowsNameRules.Validate(name);

    public bool CanWriteIn(string path) => path != ComputerRoot;
}

public sealed class RemotePanelSource : IPanelSource
{
    private readonly ConnectionManager _connection;

    public RemotePanelSource(ConnectionManager connection) => _connection = connection;

    public EndpointSide Side => EndpointSide.Remote;

    public bool IsAvailable => _connection.IsConnected;

    public string HomePath => _connection.Browse?.HomeDirectory ?? "/";

    private Infrastructure.Sftp.SftpFileStore Store => _connection.Browse ?? throw new StoreException(FailureKind.Transient, "Not connected to the Linux VM.");

    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) => Store.ListAsync(path, ct);

    public string? Parent(string path) => RemotePath.GetParent(path);

    public string Combine(string directory, string name) => RemotePath.Combine(directory, name);

    public string NormalizeInput(string input, string current)
    {
        var text = input.Trim();
        if (text.Contains('\\') && !text.Contains('/'))
        {
            throw new ArgumentException("Linux paths use '/' (for example /home/you/data).");
        }

        return RemotePath.NormalizeUserInput(text, text.StartsWith('~') ? HomePath : current);
    }

    public IReadOnlyList<Crumb> Breadcrumbs(string path) => RemotePath.Breadcrumbs(path).Select(c => new Crumb(c.Label, c.Path)).ToList();

    public string DisplayPath(string path) => path;

    public Task CreateFolderAsync(string path, CancellationToken ct) => Store.CreateDirectoryAsync(path, ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => Store.RenameAsync(from, to, ct);

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct) => Store.GetEntryAsync(path, ct);

    public string? ValidateName(string name) => RemotePath.ValidateChildName(name);

    public bool CanWriteIn(string path) => true;
}

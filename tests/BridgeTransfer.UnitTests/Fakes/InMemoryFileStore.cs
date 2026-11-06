using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Paths;
using BridgeTransfer.Core.Storage;

namespace BridgeTransfer.UnitTests.Fakes;

/// <summary>
/// In-memory file system with POSIX-style paths and fault injection. When <see cref="CaseInsensitive"/>
/// is true it behaves like NTFS for name lookups.
/// </summary>
public sealed class InMemoryFileStore : IFileStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _nodes;

    public sealed class Node
    {
        public EntryKind Kind;
        public byte[] Data = Array.Empty<byte>();
        public DateTime ModifiedUtc;
        public string ActualPath = "";
        public bool Unreadable;
    }

    public InMemoryFileStore(EndpointSide side, bool caseInsensitive = false)
    {
        Side = side;
        CaseInsensitive = caseInsensitive;
        _nodes = new Dictionary<string, Node>(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["/"] = new Node { Kind = EntryKind.Directory, ActualPath = "/", ModifiedUtc = DateTime.UtcNow },
        };
    }

    public EndpointSide Side { get; }

    public bool CaseInsensitive { get; }

    public bool SupportsReplace { get; set; } = true;

    /// <summary>Called before each operation: (operation, path). Throw to inject a failure.</summary>
    public Action<string, string>? BeforeOperation { get; set; }

    /// <summary>Called after each chunk is written to a file: (path, totalBytesSoFar).</summary>
    public Action<string, long>? OnWriteProgress { get; set; }

    /// <summary>Called after each chunk is read from a file: (path, totalBytesSoFar).</summary>
    public Action<string, long>? OnReadProgress { get; set; }

    public int ChunkSize { get; set; } = 64 * 1024;

    public long? AvailableSpace { get; set; }

    public int ConcurrentWriters;
    public int MaxConcurrentWritersObserved;
    public List<string> Operations { get; } = new();

    // ---- setup helpers ----
    public InMemoryFileStore Dir(string path, DateTime? mtime = null)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[path] = new Node { Kind = EntryKind.Directory, ActualPath = path, ModifiedUtc = mtime ?? DateTime.UtcNow };
        }

        return this;
    }

    public InMemoryFileStore File(string path, byte[] data, DateTime? mtime = null)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[path] = new Node { Kind = EntryKind.File, Data = data, ActualPath = path, ModifiedUtc = mtime ?? new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc) };
        }

        return this;
    }

    public InMemoryFileStore File(string path, string text, DateTime? mtime = null) => File(path, System.Text.Encoding.UTF8.GetBytes(text), mtime);

    public InMemoryFileStore Link(string path)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[path] = new Node { Kind = EntryKind.SymbolicLink, ActualPath = path, ModifiedUtc = DateTime.UtcNow };
        }

        return this;
    }

    public void MarkUnreadable(string path)
    {
        lock (_gate)
        {
            _nodes[path].Unreadable = true;
        }
    }

    public Node? Get(string path)
    {
        lock (_gate)
        {
            return _nodes.GetValueOrDefault(path);
        }
    }

    public byte[] Read(string path) => Get(path)!.Data;

    public string ReadText(string path) => System.Text.Encoding.UTF8.GetString(Read(path));

    public bool Exists(string path) => Get(path) is not null;

    public IReadOnlyList<string> AllPaths()
    {
        lock (_gate)
        {
            return _nodes.Values.Select(n => n.ActualPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        }
    }

    public void Touch(string path, byte[] data, DateTime mtime)
    {
        lock (_gate)
        {
            var n = _nodes[path];
            n.Data = data;
            n.ModifiedUtc = mtime;
        }
    }

    private void EnsureParents(string path)
    {
        var parent = RemotePath.GetParent(path);
        while (parent is not null && !_nodes.ContainsKey(parent))
        {
            _nodes[parent] = new Node { Kind = EntryKind.Directory, ActualPath = parent, ModifiedUtc = DateTime.UtcNow };
            parent = RemotePath.GetParent(parent);
        }
    }

    private void Before(string op, string path)
    {
        lock (_gate)
        {
            Operations.Add(op + " " + path);
        }

        BeforeOperation?.Invoke(op, path);
    }

    // ---- IFileStore ----
    public string Combine(string directory, string name) => RemotePath.Combine(directory, name);

    public string CombineRelative(string root, string relativePath) => RemotePath.CombineRelative(root, relativePath);

    public string? GetParent(string path) => RemotePath.GetParent(path);

    public string GetName(string path) => RemotePath.GetName(path);

    public string LockKey(string path) => CaseInsensitive ? path.ToUpperInvariant() : path;

    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Before("stat", path);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(path, out var n))
            {
                return Task.FromResult<FileEntry?>(null);
            }

            return Task.FromResult<FileEntry?>(ToEntry(n));
        }
    }

    private static FileEntry ToEntry(Node n) =>
        new(RemotePath.GetName(n.ActualPath), n.ActualPath, n.Kind, n.Kind == EntryKind.File ? n.Data.LongLength : 0, n.ModifiedUtc);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Before("list", directory);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(directory, out var dir) || dir.Kind != EntryKind.Directory)
            {
                throw new StoreException(FailureKind.NotFound, $"Folder {directory} not found.");
            }

            if (dir.Unreadable)
            {
                throw new StoreException(FailureKind.PermissionDenied, $"Permission denied: {directory}");
            }

            var prefix = directory == "/" ? "/" : directory + "/";
            var list = _nodes.Values
                .Where(n => n.ActualPath.StartsWith(prefix, StringComparison.Ordinal) && n.ActualPath.Length > prefix.Length && !n.ActualPath[prefix.Length..].Contains('/'))
                .Select(ToEntry)
                .ToList();
            return Task.FromResult<IReadOnlyList<FileEntry>>(list);
        }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Before("mkdir", path);
        lock (_gate)
        {
            if (_nodes.ContainsKey(path))
            {
                throw new StoreException(FailureKind.AlreadyExists, "exists");
            }

            if (!_nodes.TryGetValue(RemotePath.GetParent(path)!, out var p) || p.Kind != EntryKind.Directory)
            {
                throw new StoreException(FailureKind.NotFound, "parent missing");
            }

            _nodes[path] = new Node { Kind = EntryKind.Directory, ActualPath = path, ModifiedUtc = DateTime.UtcNow };
        }

        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        Before("open", path);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(path, out var n) || n.Kind != EntryKind.File)
            {
                throw new StoreException(FailureKind.NotFound, "not found");
            }

            return Task.FromResult<Stream>(new ObservedReadStream(this, path, n.Data));
        }
    }

    public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken)
    {
        Before("create", path);
        lock (_gate)
        {
            if (_nodes.ContainsKey(path))
            {
                throw new StoreException(FailureKind.AlreadyExists, "exists");
            }

            var parent = RemotePath.GetParent(path)!;
            if (!_nodes.TryGetValue(parent, out var p) || p.Kind != EntryKind.Directory)
            {
                throw new StoreException(FailureKind.NotFound, "parent missing");
            }

            var node = new Node { Kind = EntryKind.File, ActualPath = path, ModifiedUtc = DateTime.UtcNow };
            _nodes[path] = node;
            return Task.FromResult<Stream>(new NodeWriteStream(this, path, node));
        }
    }

    public async Task WriteNewFileAsync(string path, Stream content, Action<long> progress, CancellationToken cancellationToken)
    {
        await using var output = await CreateNewAsync(path, cancellationToken);
        var buffer = new byte[ChunkSize];
        long total = 0;
        int n;
        while ((n = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            total += n;
            progress(total);
        }
    }

    public async Task ReadFileAsync(string path, Stream output, Action<long> progress, CancellationToken cancellationToken)
    {
        await using var input = await OpenReadAsync(path, cancellationToken);
        var buffer = new byte[ChunkSize];
        long total = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            total += n;
            progress(total);
        }
    }

    public Task MoveNoReplaceAsync(string from, string to, CancellationToken cancellationToken)
    {
        Before("rename", from + " -> " + to);
        lock (_gate)
        {
            if (_nodes.ContainsKey(to))
            {
                throw new StoreException(FailureKind.AlreadyExists, "target exists");
            }

            var n = _nodes[from];
            _nodes.Remove(from);
            n.ActualPath = to;
            _nodes[to] = n;
        }

        AfterRename?.Invoke(from, to);
        return Task.CompletedTask;
    }

    /// <summary>Called after a rename has been applied: (from, to).</summary>
    public Action<string, string>? AfterRename { get; set; }

    public Task MoveReplaceAsync(string from, string to, CancellationToken cancellationToken)
    {
        Before("replace", from + " -> " + to);
        if (!SupportsReplace)
        {
            throw new StoreException(FailureKind.ReplaceNotSupported, "The server cannot replace files safely.");
        }

        lock (_gate)
        {
            var n = _nodes[from];
            _nodes.Remove(from);
            _nodes.Remove(to);
            n.ActualPath = to;
            _nodes[to] = n;
        }

        AfterRename?.Invoke(from, to);
        return Task.CompletedTask;
    }

    public Task DeleteOwnedFileAsync(string path, CancellationToken cancellationToken)
    {
        Before("delete", path);
        lock (_gate)
        {
            _nodes.Remove(path);
        }

        return Task.CompletedTask;
    }

    public Task SetModifiedTimeAsync(string path, DateTime modifiedUtc, CancellationToken cancellationToken)
    {
        Before("utime", path);
        lock (_gate)
        {
            _nodes[path].ModifiedUtc = modifiedUtc;
        }

        return Task.CompletedTask;
    }

    public Task<long?> GetAvailableSpaceAsync(string path, CancellationToken cancellationToken) => Task.FromResult(AvailableSpace);

    private sealed class ObservedReadStream : Stream
    {
        private readonly InMemoryFileStore _store;
        private readonly string _path;
        private readonly byte[] _data;
        private long _pos;

        public ObservedReadStream(InMemoryFileStore store, string path, byte[] data)
        {
            _store = store;
            _path = path;
            _data = data;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.LongLength;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(Math.Min(buffer.Length, _store.ChunkSize), _data.LongLength - _pos);
            if (n <= 0)
            {
                return 0;
            }

            _data.AsSpan((int)_pos, n).CopyTo(buffer);
            _pos += n;
            _store.OnReadProgress?.Invoke(_path, _pos);
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class NodeWriteStream : Stream
    {
        private readonly InMemoryFileStore _store;
        private readonly string _path;
        private readonly Node _node;
        private readonly MemoryStream _buffer = new();
        private bool _closed;

        public NodeWriteStream(InMemoryFileStore store, string path, Node node)
        {
            _store = store;
            _path = path;
            _node = node;
            var c = Interlocked.Increment(ref store.ConcurrentWriters);
            lock (store._gate)
            {
                store.MaxConcurrentWritersObserved = Math.Max(store.MaxConcurrentWritersObserved, c);
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _buffer.Write(buffer);
            lock (_store._gate)
            {
                _node.Data = _buffer.ToArray();
            }

            _store.OnWriteProgress?.Invoke(_path, _buffer.Length);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                Interlocked.Decrement(ref _store.ConcurrentWriters);
            }

            base.Dispose(disposing);
        }
    }
}

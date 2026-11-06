namespace BridgeTransfer.Core.Transfers;

/// <summary>Pass-through stream that counts bytes read and written (64-bit).</summary>
public sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private long _count;

    public CountingStream(Stream inner, bool leaveOpen = false)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    public long Count => Interlocked.Read(ref _count);

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Interlocked.Add(ref _count, n);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        Interlocked.Add(ref _count, n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _count, n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Interlocked.Add(ref _count, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Interlocked.Add(ref _count, buffer.Length);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _count, buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

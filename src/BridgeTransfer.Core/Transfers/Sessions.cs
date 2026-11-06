using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;

namespace BridgeTransfer.Core.Transfers;

/// <summary>A connected pair of stores used by one worker at a time.</summary>
public interface ITransferSession : IAsyncDisposable
{
    IFileStore Local { get; }

    IFileStore Remote { get; }

    bool IsConnected { get; }
}

/// <summary>
/// Opens transfer sessions for a specific server. Implementations must verify the host key with the
/// same policy as browsing and throw <see cref="StoreException"/> on failure. Opening a session for a
/// connection other than the active one must fail, so queued work can never reach the wrong server.
/// </summary>
public interface ITransferSessionFactory
{
    Task<ITransferSession> OpenAsync(ConnectionKey connection, CancellationToken cancellationToken);
}

internal sealed class SessionPool
{
    private readonly ITransferSessionFactory _factory;
    private readonly object _gate = new();
    private readonly Dictionary<ConnectionKey, List<(ITransferSession Session, DateTime ReturnedUtc)>> _idle = new();

    public SessionPool(ITransferSessionFactory factory) => _factory = factory;

    public async Task<ITransferSession> RentAsync(ConnectionKey key, CancellationToken ct)
    {
        while (true)
        {
            ITransferSession? candidate = null;
            lock (_gate)
            {
                if (_idle.TryGetValue(key, out var list) && list.Count > 0)
                {
                    candidate = list[^1].Session;
                    list.RemoveAt(list.Count - 1);
                }
            }

            if (candidate is null)
            {
                return await _factory.OpenAsync(key, ct).ConfigureAwait(false);
            }

            if (candidate.IsConnected)
            {
                return candidate;
            }

            await SafeDisposeAsync(candidate).ConfigureAwait(false);
        }
    }

    public void Return(ConnectionKey key, ITransferSession session, bool healthy)
    {
        if (!healthy || !session.IsConnected)
        {
            _ = SafeDisposeAsync(session);
            return;
        }

        lock (_gate)
        {
            if (!_idle.TryGetValue(key, out var list))
            {
                _idle[key] = list = new();
            }

            list.Add((session, DateTime.UtcNow));
        }
    }

    public async Task TrimAsync(TimeSpan maxIdle)
    {
        var toDispose = new List<ITransferSession>();
        lock (_gate)
        {
            foreach (var list in _idle.Values)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (DateTime.UtcNow - list[i].ReturnedUtc > maxIdle)
                    {
                        toDispose.Add(list[i].Session);
                        list.RemoveAt(i);
                    }
                }
            }
        }

        foreach (var s in toDispose)
        {
            await SafeDisposeAsync(s).ConfigureAwait(false);
        }
    }

    public async Task CloseAsync(ConnectionKey? key)
    {
        var toDispose = new List<ITransferSession>();
        lock (_gate)
        {
            foreach (var (k, list) in _idle)
            {
                if (key is null || k == key)
                {
                    toDispose.AddRange(list.Select(x => x.Session));
                    list.Clear();
                }
            }
        }

        foreach (var s in toDispose)
        {
            await SafeDisposeAsync(s).ConfigureAwait(false);
        }
    }

    private static async Task SafeDisposeAsync(ITransferSession s)
    {
        try
        {
            await s.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Closing a broken connection must never take down the queue.
        }
    }
}

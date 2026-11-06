using System.Threading;

namespace BridgeTransfer.App.Services;

/// <summary>
/// Ensures one running instance per user, so two processes never process the same persisted queue.
/// A second launch asks the first one to come to the front and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly bool _owned;
    private RegisteredWaitHandle? _registration;

    public SingleInstance(string name)
    {
        var user = Environment.UserName.Replace('\\', '_');
        _mutex = new Mutex(true, $"Local\\{name}-{user}-instance", out _owned);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}-{user}-activate");
    }

    public bool IsFirstInstance => _owned;

    public void SignalFirstInstance() => _activate.Set();

    public void ListenForActivation(Action onActivate) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (_owned)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _activate.Dispose();
    }
}

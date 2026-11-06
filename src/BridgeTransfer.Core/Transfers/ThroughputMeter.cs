namespace BridgeTransfer.Core.Transfers;

/// <summary>Measures transfer speed over a sliding window. Thread-safe.</summary>
public sealed class ThroughputMeter
{
    private readonly object _gate = new();
    private readonly Queue<(DateTime Time, long Bytes)> _samples = new();
    private readonly TimeSpan _window;

    public ThroughputMeter(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(5);

    public void Record(long totalBytes, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        lock (_gate)
        {
            _samples.Enqueue((t, totalBytes));
            while (_samples.Count > 2 && t - _samples.Peek().Time > _window)
            {
                _samples.Dequeue();
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
        }
    }

    public bool HasEnoughSamples
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count >= 2 && (_samples.Last().Time - _samples.Peek().Time) >= TimeSpan.FromSeconds(2);
            }
        }
    }

    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count < 2)
                {
                    return 0;
                }

                var first = _samples.Peek();
                var last = _samples.Last();
                var seconds = (last.Time - first.Time).TotalSeconds;
                return seconds < 0.5 ? 0 : Math.Max(0, (last.Bytes - first.Bytes) / seconds);
            }
        }
    }
}

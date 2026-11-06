using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.UnitTests.Fakes;

public sealed class FakeSessionFactory : ITransferSessionFactory
{
    public FakeSessionFactory(InMemoryFileStore local, InMemoryFileStore remote)
    {
        Local = local;
        Remote = remote;
    }

    public InMemoryFileStore Local { get; }

    public InMemoryFileStore Remote { get; }

    public int Opened;

    /// <summary>Throw from here to simulate connection failures.</summary>
    public Func<ConnectionKey, Exception?>? OnOpen { get; set; }

    public Task<ITransferSession> OpenAsync(ConnectionKey connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OnOpen?.Invoke(connection) is { } ex)
        {
            throw ex;
        }

        Interlocked.Increment(ref Opened);
        return Task.FromResult<ITransferSession>(new Session(Local, Remote));
    }

    private sealed class Session : ITransferSession
    {
        public Session(IFileStore local, IFileStore remote)
        {
            Local = local;
            Remote = remote;
        }

        public IFileStore Local { get; }

        public IFileStore Remote { get; }

        public bool IsConnected => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public static class TestUtil
{
    public static readonly ConnectionKey Key = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "vm.test", 22, "tester");

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(20);
        }
    }

    public static async Task<TransferState> WaitFinalAsync(TransferJob job, int timeoutMs = 15000)
    {
        await WaitUntilAsync(() => job.State.IsFinal(), timeoutMs);
        return job.State;
    }

    public static async Task WaitWaitingAsync(TransferJob job, int timeoutMs = 15000) =>
        await WaitUntilAsync(() => job.State == TransferState.WaitingForDecision, timeoutMs);

    public static TransferOptions FastOptions() => new()
    {
        RetryDelays = new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(80) },
    };

    public static byte[] Bytes(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}

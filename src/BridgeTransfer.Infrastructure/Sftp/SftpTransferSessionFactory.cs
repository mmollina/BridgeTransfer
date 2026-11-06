using BridgeTransfer.Core.Models;
using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Core.Transfers;
using BridgeTransfer.Infrastructure.Local;

namespace BridgeTransfer.Infrastructure.Sftp;

/// <summary>What a transfer worker needs to open its own connection to the active server.</summary>
public sealed record ConnectionTicket(ConnectionKey Key, ConnectionProfile Profile, SessionCredentials Credentials, SftpConnectOptions Options);

/// <summary>
/// Opens one independent SFTP connection per transfer worker, with the same host-key policy as browsing
/// but without prompting: only an already trusted key is accepted. Work bound to a connection that is no
/// longer active is refused, so it can never be sent to a different server.
/// </summary>
public sealed class SftpTransferSessionFactory : ITransferSessionFactory
{
    private readonly Func<ConnectionKey, ConnectionTicket?> _tickets;
    private readonly HostKeyVerifier _verifier;
    private readonly LocalFileStore _local;

    public SftpTransferSessionFactory(Func<ConnectionKey, ConnectionTicket?> tickets, HostKeyVerifier verifier, LocalFileStore local)
    {
        _tickets = tickets;
        _verifier = verifier;
        _local = local;
    }

    public async Task<ITransferSession> OpenAsync(ConnectionKey connection, CancellationToken cancellationToken)
    {
        var ticket = _tickets(connection);
        if (ticket is null || ticket.Key != connection)
        {
            throw new StoreException(FailureKind.Authentication, "This transfer belongs to a connection that is no longer active. Reconnect to that server and retry.");
        }

        var client = await SftpConnector.ConnectAsync(ticket.Profile, ticket.Credentials, _verifier, prompt: null, ticket.Options, cancellationToken).ConfigureAwait(false);
        return new Session(_local, new SftpFileStore(client));
    }

    private sealed class Session : ITransferSession
    {
        private readonly SftpFileStore _remote;

        public Session(LocalFileStore local, SftpFileStore remote)
        {
            Local = local;
            _remote = remote;
        }

        public IFileStore Local { get; }

        public IFileStore Remote => _remote;

        public bool IsConnected => _remote.IsConnected;

        public ValueTask DisposeAsync()
        {
            _remote.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

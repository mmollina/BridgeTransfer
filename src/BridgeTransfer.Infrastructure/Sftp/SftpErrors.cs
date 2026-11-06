using System.Net.Sockets;
using BridgeTransfer.Core.Storage;
using Renci.SshNet.Common;

namespace BridgeTransfer.Infrastructure.Sftp;

/// <summary>Translates SSH.NET and socket exceptions into <see cref="StoreException"/> with plain-language messages.</summary>
internal static class SftpErrors
{
    public static StoreException Translate(Exception ex, string? path = null)
    {
        var where = path is null ? "" : $" ({path})";
        return ex switch
        {
            StoreException se => se,
            OperationCanceledException => new StoreException(FailureKind.Cancelled, "Cancelled.", ex),
            SftpPathNotFoundException => new StoreException(FailureKind.NotFound, "The item was not found on the VM" + where + ".", ex),
            SftpPermissionDeniedException => new StoreException(FailureKind.PermissionDenied, "Permission denied on the VM" + where + ".", ex),
            SshAuthenticationException => new StoreException(FailureKind.Authentication, "The VM rejected the login. Check the user name and the key or password.", ex),
            SshOperationTimeoutException => new StoreException(FailureKind.Transient, "The VM did not respond in time" + where + ".", ex),
            SshConnectionException => new StoreException(FailureKind.Transient, "The connection to the VM was lost.", ex),
            SocketException => new StoreException(FailureKind.Transient, "The VM could not be reached over the network.", ex),
            ObjectDisposedException => new StoreException(FailureKind.Transient, "The connection to the VM was closed.", ex),
            IOException io when io.InnerException is SocketException => new StoreException(FailureKind.Transient, "The connection to the VM was interrupted.", ex),
            SftpException sftp => new StoreException(FailureKind.Unknown, $"The VM reported an error{where}: {sftp.Message}", ex),
            SshException ssh when ssh.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) => new StoreException(FailureKind.Transient, "The connection to the VM was lost.", ex),
            SshException ssh => new StoreException(FailureKind.Unknown, $"The VM reported an error{where}: {ssh.Message}", ex),
            IOException io => new StoreException(FailureKind.Unknown, io.Message, ex),
            _ => new StoreException(FailureKind.Unknown, ex.Message, ex),
        };
    }
}

using BridgeTransfer.Core.Security;
using BridgeTransfer.Core.Storage;
using BridgeTransfer.Infrastructure.Sftp;

namespace BridgeTransfer.App.Services;

/// <summary>Plain-language message plus redacted technical details for any exception.</summary>
public static class ErrorText
{
    public static (string Message, string Details) Describe(Exception ex)
    {
        var message = ex switch
        {
            KeyFileException k => k.Message,
            HostKeyChangedException h => h.Message,
            StoreException s => s.Message,
            UnauthorizedAccessException => "Windows denied access.",
            _ => "Something unexpected happened. The details below can help diagnose it.",
        };
        var details = ex is StoreException se ? se.TechnicalDetails + Environment.NewLine + Environment.NewLine + ex : ex.ToString();
        return (SecretRedactor.Redact(message), SecretRedactor.Redact(details));
    }
}

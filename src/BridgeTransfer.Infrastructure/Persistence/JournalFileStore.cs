using System.Text.Json;
using BridgeTransfer.Core.Transfers;

namespace BridgeTransfer.Infrastructure.Persistence;

/// <summary>Writes unfinished-job information to queue.json so interrupted work can be shown after a restart.</summary>
public sealed class JournalFileStore : IJobJournal
{
    private readonly string _path;
    private readonly object _gate = new();

    public JournalFileStore(string path) => _path = path;

    public void Write(JournalDocument document)
    {
        lock (_gate)
        {
            AtomicJson.Write(_path, document, durable: false);
        }
    }

    public JournalDocument? Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            try
            {
                return AtomicJson.Read<JournalDocument>(_path);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return null;
            }
        }
    }
}

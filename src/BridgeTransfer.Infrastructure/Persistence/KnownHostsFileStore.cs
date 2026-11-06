using System.Text.Json;
using BridgeTransfer.Core.Security;

namespace BridgeTransfer.Infrastructure.Persistence;

/// <summary>Trusted host keys stored in known_hosts.json.</summary>
public sealed class KnownHostsFileStore : IKnownHostsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<KnownHostRecord>? _cache;

    public KnownHostsFileStore(string path) => _path = path;

    private sealed class Document
    {
        public int SchemaVersion { get; set; } = 1;

        public List<KnownHostRecord> Hosts { get; set; } = new();
    }

    public IReadOnlyList<KnownHostRecord> GetAll()
    {
        lock (_gate)
        {
            if (_cache is null)
            {
                _cache = new List<KnownHostRecord>();
                if (File.Exists(_path))
                {
                    try
                    {
                        _cache = AtomicJson.Read<Document>(_path)?.Hosts ?? new();
                    }
                    catch (Exception ex) when (ex is JsonException or IOException)
                    {
                        // An unreadable trust file means nothing is trusted; keep the file for inspection.
                        try
                        {
                            File.Copy(_path, _path + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", false);
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            }

            return _cache.ToList();
        }
    }

    public void Save(IReadOnlyList<KnownHostRecord> records)
    {
        lock (_gate)
        {
            _cache = records.ToList();
            AtomicJson.Write(_path, new Document { Hosts = _cache }, keepBackup: true);
        }
    }
}

using BridgeTransfer.Core.Models;

namespace BridgeTransfer.Core.Transfers;

/// <summary>A staging file created by this application, recorded before it was created.</summary>
public sealed class OwnedStagingFile
{
    public EndpointSide Side { get; set; }

    public string Path { get; set; } = "";

    public Guid JobId { get; set; }

    public Guid TaskId { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>Persisted description of an unfinished job, used to show interrupted work after a restart.</summary>
public sealed class JournalJob
{
    public Guid JobId { get; set; }

    public JobKind Kind { get; set; }

    public string DisplayName { get; set; } = "";

    public TransferDirection Direction { get; set; }

    public Guid ProfileId { get; set; }

    public string Host { get; set; } = "";

    public int Port { get; set; }

    public string Username { get; set; } = "";

    public List<string> SourcePaths { get; set; } = new();

    public string SourceDescription { get; set; } = "";

    public string DestinationDirectory { get; set; } = "";

    public string LastState { get; set; } = "";

    public int TotalFiles { get; set; }

    public int FinishedFiles { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<OwnedStagingFile> StagingFiles { get; set; } = new();
}

public sealed class JournalDocument
{
    public int SchemaVersion { get; set; } = 1;

    public DateTime WrittenUtc { get; set; }

    public List<JournalJob> Jobs { get; set; } = new();
}

/// <summary>Persistence of unfinished jobs. Implementations must write atomically.</summary>
public interface IJobJournal
{
    void Write(JournalDocument document);

    JournalDocument? Read();
}

public sealed class NullJournal : IJobJournal
{
    public void Write(JournalDocument document)
    {
    }

    public JournalDocument? Read() => null;
}

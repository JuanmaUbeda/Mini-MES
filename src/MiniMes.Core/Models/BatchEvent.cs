namespace MiniMes.Core.Models;

public enum EventLevel
{
    Info,
    Warning,
    Error
}

/// <summary>An entry in a batch's audit trail (created, XML sent, accepted, rejected...).</summary>
public class BatchEvent
{
    private BatchEvent() { } // for EF Core

    public BatchEvent(string batchId, EventLevel level, string message, DateTime timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            throw new DomainException("Batch ID is required.");
        if (string.IsNullOrWhiteSpace(message))
            throw new DomainException("Event message is required.");

        BatchId = batchId;
        Level = level;
        Message = message;
        TimestampUtc = timestampUtc;
    }

    public int Id { get; private set; }
    public string BatchId { get; private set; } = "";
    public EventLevel Level { get; private set; }
    public string Message { get; private set; } = "";
    public DateTime TimestampUtc { get; private set; }
}

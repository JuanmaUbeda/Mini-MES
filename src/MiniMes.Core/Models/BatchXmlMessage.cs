namespace MiniMes.Core.Models;

public enum XmlMessageResult
{
    /// <summary>Saved, and sent (or about to be); no answer recorded yet.</summary>
    Pending,
    Accepted,
    Rejected,
    /// <summary>No decision: the external system could not be reached.</summary>
    Unavailable
}

/// <summary>
/// One XML message sent to the external system, kept exactly as sent, together with how the exchange ended.
/// A batch has one row per attempt, so this is the history of the interface.
/// </summary>
public class BatchXmlMessage
{
    private BatchXmlMessage() { } // for EF Core

    public BatchXmlMessage(string batchId, string fileName, string xml, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            throw new DomainException("Batch ID is required.");
        if (string.IsNullOrWhiteSpace(fileName))
            throw new DomainException("File name is required.");
        if (string.IsNullOrWhiteSpace(xml))
            throw new DomainException("XML content is required.");

        BatchId = batchId;
        FileName = fileName;
        Xml = xml;
        CreatedAtUtc = createdAtUtc;
        Result = XmlMessageResult.Pending;
    }

    public int Id { get; private set; }
    public string BatchId { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string Xml { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }
    public XmlMessageResult Result { get; private set; }

    /// <summary>The ERP reference when accepted, or the reason when rejected or unreachable.</summary>
    public string? Detail { get; private set; }

    /// <summary>Records how the exchange ended. Allowed once: a message's outcome never changes afterwards.</summary>
    public void RecordResult(XmlMessageResult result, string? detail)
    {
        if (Result != XmlMessageResult.Pending)
            throw new DomainException($"Message {Id} already has a result ({Result}).");
        if (result == XmlMessageResult.Pending)
            throw new DomainException("A result cannot be Pending.");

        Result = result;
        Detail = detail;
    }
}

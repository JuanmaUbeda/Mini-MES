namespace MiniMes.Core;

/// <summary>Batch IDs look like B-20261016-001: prefix, UTC date, and a global sequence number that never resets.</summary>
public static class BatchIdGenerator
{
    /// <summary>The part shared by all IDs of one day, e.g. "B-20261016-".</summary>
    public static string PrefixFor(DateTime utcDate) => $"B-{utcDate:yyyyMMdd}-";

    public static string Format(DateTime utcDate, int sequence)
    {
        if (sequence < 1)
            throw new DomainException("Batch sequence must be 1 or greater.");
        return $"{PrefixFor(utcDate)}{sequence:000}";
    }

    /// <summary>Reads the sequence number from an ID, or null if it doesn't have the expected format.</summary>
    public static int? ParseSequence(string batchId)
    {
        var lastDash = batchId.LastIndexOf('-');
        if (lastDash < 0) return null;
        return int.TryParse(batchId[(lastDash + 1)..], out var seq) ? seq : null;
    }
}

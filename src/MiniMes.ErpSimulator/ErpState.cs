using System.Collections.Concurrent;

namespace MiniMes.ErpSimulator;

public enum ErpMode
{
    /// <summary>Applies the normal business rules.</summary>
    Normal,
    /// <summary>Rejects every batch (simulates a business-side block).</summary>
    RejectAll,
    /// <summary>Answers 503 (simulates the ERP being down).</summary>
    Unavailable
}

/// <summary>What the simulated ERP remembers. Kept in memory: restarting the simulator resets it.</summary>
public class ErpState
{
    private int _referenceCounter;

    public ErpMode Mode { get; set; } = ErpMode.Normal;

    /// <summary>Batch IDs already accepted, with the reference we gave them.</summary>
    public ConcurrentDictionary<string, string> Accepted { get; } = new();

    public string NextReference() => $"ERP-{Interlocked.Increment(ref _referenceCounter):000000}";
}

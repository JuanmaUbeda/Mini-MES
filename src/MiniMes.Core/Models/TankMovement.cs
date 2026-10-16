namespace MiniMes.Core.Models;

/// <summary>One line of the material ledger: a consumption by a batch, or a refill. This is material traceability.</summary>
public class TankMovement
{
    private TankMovement() { } // for EF Core

    /// <param name="deltaKg">Negative when material leaves the tank, positive when it is added.</param>
    public TankMovement(Container container, decimal deltaKg, decimal levelAfterKg, string reason, string? batchId, DateTime timestampUtc)
    {
        if (deltaKg == 0)
            throw new DomainException("A movement cannot be zero.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("A movement needs a reason.");

        Container = container;
        DeltaKg = deltaKg;
        LevelAfterKg = levelAfterKg;
        Reason = reason;
        BatchId = batchId;
        TimestampUtc = timestampUtc;
    }

    public int Id { get; private set; }
    public Container Container { get; private set; }
    public decimal DeltaKg { get; private set; }
    public decimal LevelAfterKg { get; private set; }
    public string Reason { get; private set; } = "";
    public string? BatchId { get; private set; }
    public DateTime TimestampUtc { get; private set; }
}

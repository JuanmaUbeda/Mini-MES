namespace MiniMes.Core.Models;

public class Batch
{
    /// <summary>Mixer capacity. A batch cannot be planned above this.</summary>
    public const decimal MaxQuantityKg = 500m;

    private Batch() { } // for EF Core

    public Batch(string batchId, Recipe recipe, decimal plannedQuantityKg, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            throw new DomainException("Batch ID is required.");
        if (recipe is null)
            throw new DomainException("A recipe is required.");
        if (plannedQuantityKg <= 0)
            throw new DomainException("Planned quantity must be greater than 0.");
        if (plannedQuantityKg > MaxQuantityKg)
            throw new DomainException($"Planned quantity cannot exceed the mixer capacity of {MaxQuantityKg} kg.");

        BatchId = batchId;
        Recipe = recipe;
        RecipeId = recipe.Id;
        PlannedQuantityKg = plannedQuantityKg;
        CreatedAtUtc = createdAtUtc;
        Status = BatchStatus.Created;
    }

    public string BatchId { get; private set; } = "";
    public int RecipeId { get; private set; }
    public Recipe Recipe { get; private set; } = null!;
    public decimal PlannedQuantityKg { get; private set; }
    public BatchStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    // Created -> Ready: the external service accepted the batch.
    public void MarkReady() => Transition(BatchStatus.Created, BatchStatus.Ready);

    // Ready -> Running: the mixer started.
    public void Start(DateTime startedAtUtc)
    {
        Transition(BatchStatus.Ready, BatchStatus.Running);
        StartedAtUtc = startedAtUtc;
    }

    // Running -> Completed: the mixer finished.
    public void Complete(DateTime completedAtUtc)
    {
        Transition(BatchStatus.Running, BatchStatus.Completed);
        CompletedAtUtc = completedAtUtc;
    }

    /// <summary>Any unfinished batch can fail (e.g. rejected by the external service).</summary>
    public void Fail(string reason)
    {
        if (Status is BatchStatus.Completed or BatchStatus.Error)
            throw new DomainException($"A batch in status {Status} cannot be failed.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("An error reason is required.");

        Status = BatchStatus.Error;
        ErrorMessage = reason;
    }

    private void Transition(BatchStatus expectedCurrent, BatchStatus next)
    {
        if (Status != expectedCurrent)
            throw new DomainException($"Cannot move batch {BatchId} to {next} from {Status}; it must be {expectedCurrent}.");
        Status = next;
    }
}

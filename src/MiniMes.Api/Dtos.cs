using MiniMes.Core.Models;

namespace MiniMes.Api;

// The API contract. Kept separate from the domain classes so the database model can change without breaking clients.

public record CreateBatchRequest(int RecipeId, decimal PlannedQuantityKg);

public record IngredientDto(Container Container, string IngredientName, decimal Percentage);

public record RecipeDto(int Id, string Name, IReadOnlyList<IngredientDto> Ingredients)
{
    public static RecipeDto From(Recipe r) =>
        new(r.Id, r.Name, r.Ingredients.Select(i => new IngredientDto(i.Container, i.IngredientName, i.Percentage)).ToList());
}

public record IngredientQuantityDto(Container Container, string IngredientName, decimal Kilograms);

public record BatchDto(
    string BatchId,
    int RecipeId,
    string RecipeName,
    decimal PlannedQuantityKg,
    BatchStatus Status,
    DateTime CreatedAtUtc,
    string? ErrorMessage,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<IngredientQuantityDto> Ingredients)
{
    public static BatchDto From(Batch b) => new(
        b.BatchId, b.RecipeId, b.Recipe.Name, b.PlannedQuantityKg, b.Status, b.CreatedAtUtc, b.ErrorMessage,
        b.StartedAtUtc, b.CompletedAtUtc,
        b.Recipe.CalculateQuantities(b.PlannedQuantityKg)
            .Select(q => new IngredientQuantityDto(q.Container, q.IngredientName, q.Kilograms)).ToList());
}

public record BatchEventDto(EventLevel Level, string Message, DateTime TimestampUtc)
{
    public static BatchEventDto From(BatchEvent e) => new(e.Level, e.Message, e.TimestampUtc);
}

public record RefillRequest(decimal QuantityKg);

public record TankDto(Container Container, string MaterialName, decimal CapacityKg, decimal LevelKg, bool IsLow)
{
    public static TankDto From(Tank t) => new(t.Container, t.MaterialName, t.CapacityKg, t.LevelKg, t.IsLow);
}

public record TankMovementDto(Container Container, decimal DeltaKg, decimal LevelAfterKg, string Reason, string? BatchId, DateTime TimestampUtc)
{
    public static TankMovementDto From(TankMovement m) =>
        new(m.Container, m.DeltaKg, m.LevelAfterKg, m.Reason, m.BatchId, m.TimestampUtc);
}

/// <summary>Server-side limits the web page needs, so they are defined in one place only.</summary>
public record ConfigDto(decimal MaxQuantityKg, int MixerCycleSeconds, decimal LowLevelFraction);

public record SubmitResultDto(ErpOutcome Outcome, string Message, BatchDto Batch);

/// <summary>An XML message sent to the ERP. The XML itself is served separately, by /api/messages/{id}/xml.</summary>
public record XmlMessageDto(int Id, string FileName, DateTime CreatedAtUtc, XmlMessageResult Result, string? Detail)
{
    public static XmlMessageDto From(BatchXmlMessage m) => new(m.Id, m.FileName, m.CreatedAtUtc, m.Result, m.Detail);
}

public record BatchDetailDto(BatchDto Batch, IReadOnlyList<BatchEventDto> Events, IReadOnlyList<XmlMessageDto> Messages);

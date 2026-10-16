using MiniMes.Core.Models;

namespace MiniMes.Tests;

/// <summary>Small helpers so each test only states what it cares about.</summary>
internal static class TestData
{
    public static Recipe StandardRecipe() => new(
        "Standard Mix",
        new[]
        {
            new RecipeIngredient(Container.A, "Resin", 60),
            new RecipeIngredient(Container.B, "Hardener", 40)
        });

    public static Batch NewBatch(decimal quantityKg = 100m) =>
        new("B-20261016-001", StandardRecipe(), quantityKg, DateTime.UtcNow);
}

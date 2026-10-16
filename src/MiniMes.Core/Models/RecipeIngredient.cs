namespace MiniMes.Core.Models;

/// <summary>Which supply container an ingredient comes from.</summary>
public enum Container
{
    A,
    B
}

/// <summary>One line of a recipe: how much of a container's ingredient, as a percentage of the batch.</summary>
public class RecipeIngredient
{
    private RecipeIngredient() { } // for EF Core

    public RecipeIngredient(Container container, string ingredientName, decimal percentage)
    {
        if (string.IsNullOrWhiteSpace(ingredientName))
            throw new DomainException("Ingredient name is required.");
        if (percentage <= 0 || percentage > 100)
            throw new DomainException("Ingredient percentage must be greater than 0 and at most 100.");

        Container = container;
        IngredientName = ingredientName;
        Percentage = percentage;
    }

    public int Id { get; private set; }
    public int RecipeId { get; private set; }
    public Container Container { get; private set; }
    public string IngredientName { get; private set; } = "";
    public decimal Percentage { get; private set; }
}

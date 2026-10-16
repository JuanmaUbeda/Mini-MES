namespace MiniMes.Core.Models;

public class Recipe
{
    private readonly List<RecipeIngredient> _ingredients = new();

    private Recipe() { } // for EF Core

    public Recipe(string name, IEnumerable<RecipeIngredient> ingredients)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Recipe name is required.");

        _ingredients.AddRange(ingredients);

        if (_ingredients.Count == 0)
            throw new DomainException("A recipe needs at least one ingredient.");
        if (_ingredients.Sum(i => i.Percentage) != 100m)
            throw new DomainException("Ingredient percentages must add up to exactly 100.");
        if (_ingredients.Select(i => i.Container).Distinct().Count() != _ingredients.Count)
            throw new DomainException("Each container can only be used once per recipe.");

        Name = name;
    }

    public int Id { get; private set; }
    public string Name { get; private set; } = "";
    public IReadOnlyList<RecipeIngredient> Ingredients => _ingredients;

    /// <summary>Converts a total batch quantity (kg) into kg per ingredient.</summary>
    public IReadOnlyList<IngredientQuantity> CalculateQuantities(decimal totalKg) =>
        _ingredients
            .Select(i => new IngredientQuantity(i.Container, i.IngredientName, Math.Round(totalKg * i.Percentage / 100m, 3)))
            .ToList();
}

public record IngredientQuantity(Container Container, string IngredientName, decimal Kilograms);

using MiniMes.Core;
using MiniMes.Core.Models;

namespace MiniMes.Tests;

public class RecipeTests
{
    [Fact]
    public void CalculateQuantities_SplitsTotalByPercentage()
    {
        var recipe = TestData.StandardRecipe(); // 60% A / 40% B

        var quantities = recipe.CalculateQuantities(200m);

        Assert.Equal(120m, quantities.Single(q => q.Container == Container.A).Kilograms);
        Assert.Equal(80m, quantities.Single(q => q.Container == Container.B).Kilograms);
    }

    [Fact]
    public void Constructor_RejectsPercentagesNotSummingTo100()
    {
        var ingredients = new[]
        {
            new RecipeIngredient(Container.A, "Resin", 60),
            new RecipeIngredient(Container.B, "Hardener", 30)
        };

        Assert.Throws<DomainException>(() => new Recipe("Bad", ingredients));
    }

    [Fact]
    public void Constructor_RejectsSameContainerTwice()
    {
        var ingredients = new[]
        {
            new RecipeIngredient(Container.A, "Resin", 50),
            new RecipeIngredient(Container.A, "Filler", 50)
        };

        Assert.Throws<DomainException>(() => new Recipe("Bad", ingredients));
    }

    [Fact]
    public void Constructor_RejectsEmptyName()
    {
        Assert.Throws<DomainException>(() => new Recipe(" ", TestData.StandardRecipe().Ingredients));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public void Ingredient_RejectsInvalidPercentage(decimal percentage)
    {
        Assert.Throws<DomainException>(() => new RecipeIngredient(Container.A, "Resin", percentage));
    }
}

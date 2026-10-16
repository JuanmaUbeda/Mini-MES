using Microsoft.EntityFrameworkCore;
using MiniMes.Core.Models;

namespace MiniMes.Data;

public static class DbSeeder
{
    /// <summary>Inserts starter recipes if the table is empty. Safe to call on every startup.</summary>
    public static async Task SeedAsync(MiniMesDbContext db)
    {
        if (await db.Recipes.AnyAsync())
            return;

        db.Recipes.AddRange(
            new Recipe("Standard Mix", new[]
            {
                new RecipeIngredient(Container.A, "Resin", 60),
                new RecipeIngredient(Container.B, "Hardener", 40)
            }),
            new Recipe("High Strength Mix", new[]
            {
                new RecipeIngredient(Container.A, "Resin", 75),
                new RecipeIngredient(Container.B, "Hardener", 25)
            }));

        await db.SaveChangesAsync();
    }
}

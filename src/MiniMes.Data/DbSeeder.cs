using Microsoft.EntityFrameworkCore;
using MiniMes.Core.Models;

namespace MiniMes.Data;

public static class DbSeeder
{
    public const decimal TankCapacityKg = 1000m;
    public const decimal TankInitialLevelKg = 800m;

    /// <summary>
    /// Inserts starter recipes and tanks when their tables are empty. Safe to call on every startup;
    /// each table is checked on its own so a database created by an older version gets the new data too.
    /// </summary>
    public static async Task SeedAsync(MiniMesDbContext db)
    {
        if (!await db.Recipes.AnyAsync())
        {
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
        }

        if (!await db.Tanks.AnyAsync())
        {
            db.Tanks.AddRange(
                new Tank(Container.A, "Resin", TankCapacityKg, TankInitialLevelKg),
                new Tank(Container.B, "Hardener", TankCapacityKg, TankInitialLevelKg));
        }

        await db.SaveChangesAsync();
    }
}

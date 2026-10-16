using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiniMes.Core.Models;
using MiniMes.Data;

namespace MiniMes.Tests;

/// <summary>
/// Uses a real SQLite engine in memory (not a fake), so foreign keys and mappings behave like production.
/// The in-memory database lives only while the connection stays open.
/// </summary>
public class PersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public PersistenceTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    private MiniMesDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MiniMesDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Seeder_CreatesRecipesWithIngredients_AndIsIdempotent()
    {
        using (var db = CreateContext())
        {
            await DbSeeder.SeedAsync(db);
            await DbSeeder.SeedAsync(db); // second call must not duplicate
        }

        using var check = CreateContext();
        var recipes = await check.Recipes.Include(r => r.Ingredients).ToListAsync();
        Assert.Equal(2, recipes.Count);
        Assert.All(recipes, r => Assert.Equal(2, r.Ingredients.Count));
    }

    [Fact]
    public async Task Batch_SurvivesANewContext_WithRecipeAndStatus()
    {
        using (var db = CreateContext())
        {
            await DbSeeder.SeedAsync(db);
            var recipe = await db.Recipes.FirstAsync();
            var batch = new Batch("B-20261016-001", recipe, 250m, DateTime.UtcNow);
            batch.MarkReady();
            db.Batches.Add(batch);
            await db.SaveChangesAsync();
        }

        using var fresh = CreateContext(); // simulates an application restart
        var loaded = await fresh.Batches.Include(b => b.Recipe).ThenInclude(r => r.Ingredients).SingleAsync();
        Assert.Equal(BatchStatus.Ready, loaded.Status);
        Assert.Equal(250m, loaded.PlannedQuantityKg);
        Assert.Equal(2, loaded.Recipe.Ingredients.Count);
    }

    [Fact]
    public async Task DuplicateBatchId_IsRejectedByDatabase()
    {
        using var db = CreateContext();
        await DbSeeder.SeedAsync(db);
        var recipe = await db.Recipes.FirstAsync();
        db.Batches.Add(new Batch("B-1", recipe, 100m, DateTime.UtcNow));
        await db.SaveChangesAsync();

        using var db2 = CreateContext();
        var recipe2 = await db2.Recipes.FirstAsync();
        db2.Batches.Add(new Batch("B-1", recipe2, 50m, DateTime.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task BatchEvent_WithUnknownBatch_IsRejectedByForeignKey()
    {
        using var db = CreateContext();
        db.BatchEvents.Add(new BatchEvent("NOPE", EventLevel.Info, "orphan", DateTime.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}

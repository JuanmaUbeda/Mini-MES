using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiniMes.Core;
using MiniMes.Core.Models;
using MiniMes.Data;

namespace MiniMes.Tests;

public class BatchIdGeneratorTests
{
    [Fact]
    public void Format_UsesDateAndPaddedSequence()
    {
        Assert.Equal("B-20261016-007", BatchIdGenerator.Format(new DateTime(2026, 10, 16), 7));
    }

    [Fact]
    public void Format_RejectsSequenceBelowOne()
    {
        Assert.Throws<DomainException>(() => BatchIdGenerator.Format(DateTime.UtcNow, 0));
    }

    [Theory]
    [InlineData("B-20261016-012", 12)]
    [InlineData("garbage", null)]
    public void ParseSequence_ReadsTrailingNumber(string id, int? expected)
    {
        Assert.Equal(expected, BatchIdGenerator.ParseSequence(id));
    }
}

public class BatchServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FixedTimeProvider _time = new(new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc));

    public BatchServiceTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    private MiniMesDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MiniMesDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();

    private async Task<int> FirstRecipeIdAsync()
    {
        using var db = CreateContext();
        return (await db.Recipes.OrderBy(r => r.Id).FirstAsync()).Id;
    }

    [Fact]
    public async Task CreateBatch_AssignsSequentialIds()
    {
        var recipeId = await FirstRecipeIdAsync();
        using var db = CreateContext();
        var service = new BatchService(db, _time);

        var first = await service.CreateBatchAsync(recipeId, 100m);
        var second = await service.CreateBatchAsync(recipeId, 100m);

        Assert.Equal("B-20261016-001", first.BatchId);
        Assert.Equal("B-20261016-002", second.BatchId);
    }

    [Fact]
    public async Task CreateBatch_KeepsCountingAcrossDaysAndNewContexts()
    {
        var recipeId = await FirstRecipeIdAsync();
        using (var db = CreateContext())
            await new BatchService(db, _time).CreateBatchAsync(recipeId, 100m);

        _time.Set(new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc));
        using var db2 = CreateContext(); // simulates an application restart
        var next = await new BatchService(db2, _time).CreateBatchAsync(recipeId, 100m);

        Assert.Equal("B-20261017-002", next.BatchId);
    }

    [Fact]
    public async Task CreateBatch_DoesNotConsumeANumberWhenItFails()
    {
        var recipeId = await FirstRecipeIdAsync();
        using var db = CreateContext();
        var service = new BatchService(db, _time);

        await Assert.ThrowsAsync<DomainException>(() => service.CreateBatchAsync(recipeId, 0m));
        var first = await service.CreateBatchAsync(recipeId, 100m);

        Assert.Equal("B-20261016-001", first.BatchId);
    }

    [Fact]
    public async Task CreateBatch_StoresCreatedStatusAndAnEvent()
    {
        var recipeId = await FirstRecipeIdAsync();
        using var db = CreateContext();
        var service = new BatchService(db, _time);

        var batch = await service.CreateBatchAsync(recipeId, 150m);

        var events = await service.GetEventsAsync(batch.BatchId);
        Assert.Equal(BatchStatus.Created, batch.Status);
        Assert.Single(events);
        Assert.Equal(EventLevel.Info, events[0].Level);
    }

    [Fact]
    public async Task CreateBatch_WithUnknownRecipe_IsRejected()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);

        await Assert.ThrowsAsync<DomainException>(() => service.CreateBatchAsync(999, 100m));
    }

    [Fact]
    public async Task CreateBatch_WithInvalidQuantity_SavesNothing()
    {
        var recipeId = await FirstRecipeIdAsync();
        using var db = CreateContext();
        var service = new BatchService(db, _time);

        await Assert.ThrowsAsync<DomainException>(() => service.CreateBatchAsync(recipeId, -5m));

        using var check = CreateContext();
        Assert.Empty(await check.Batches.ToListAsync());
    }

    [Fact]
    public async Task ListBatches_ReturnsNewestFirst()
    {
        var recipeId = await FirstRecipeIdAsync();
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        await service.CreateBatchAsync(recipeId, 100m);
        _time.Set(_time.GetUtcNow().UtcDateTime.AddMinutes(5));
        await service.CreateBatchAsync(recipeId, 100m);

        var list = await service.ListBatchesAsync();

        Assert.Equal(new[] { "B-20261016-002", "B-20261016-001" }, list.Select(b => b.BatchId));
    }

    /// <summary>A clock we control, so tests don't depend on the real date.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedTimeProvider(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public void Set(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

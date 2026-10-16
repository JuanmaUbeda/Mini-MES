using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiniMes.Core;
using MiniMes.Core.Models;
using MiniMes.Data;

namespace MiniMes.Tests;

public class TankDomainTests
{
    private static Tank NewTank(decimal level = 800m) => new(Container.A, "Resin", 1000m, level);

    [Fact]
    public void Consume_ReducesTheLevel()
    {
        var tank = NewTank();

        tank.Consume(120m);

        Assert.Equal(680m, tank.LevelKg);
    }

    [Fact]
    public void Consume_MoreThanAvailable_IsRejected_AndLeavesLevelUntouched()
    {
        var tank = NewTank(100m);

        var ex = Assert.Throws<DomainException>(() => tank.Consume(120m));

        Assert.Contains("Not enough Resin in TK-A", ex.Message);
        Assert.Equal(100m, tank.LevelKg);
    }

    [Fact]
    public void Consume_TheExactRemainder_EmptiesTheTank()
    {
        var tank = NewTank(100m);

        tank.Consume(100m);

        Assert.Equal(0m, tank.LevelKg);
    }

    [Fact]
    public void Refill_IncreasesTheLevel()
    {
        var tank = NewTank();

        tank.Refill(150m);

        Assert.Equal(950m, tank.LevelKg);
    }

    [Fact]
    public void Refill_AboveCapacity_IsRejected_WithTheRoomLeft()
    {
        var tank = NewTank(900m);

        var ex = Assert.Throws<DomainException>(() => tank.Refill(200m));

        Assert.Contains("at most 100 kg more", ex.Message);
        Assert.Equal(900m, tank.LevelKg);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ConsumeAndRefill_RejectNonPositiveQuantities(double kg)
    {
        var tank = NewTank();

        Assert.Throws<DomainException>(() => tank.Consume((decimal)kg));
        Assert.Throws<DomainException>(() => tank.Refill((decimal)kg));
    }

    [Theory]
    [InlineData(800, false)]
    [InlineData(201, false)]
    [InlineData(200, true)]   // exactly 20 % counts as low
    [InlineData(0, true)]
    public void IsLow_IsTrueAtOrBelowTwentyPercent(double level, bool expected)
    {
        Assert.Equal(expected, NewTank((decimal)level).IsLow);
    }

    [Fact]
    public void Constructor_RejectsInvalidValues()
    {
        Assert.Throws<DomainException>(() => new Tank(Container.A, " ", 1000m, 0m));
        Assert.Throws<DomainException>(() => new Tank(Container.A, "Resin", 0m, 0m));
        Assert.Throws<DomainException>(() => new Tank(Container.A, "Resin", 1000m, 1001m));
        Assert.Throws<DomainException>(() => new Tank(Container.A, "Resin", 1000m, -1m));
    }
}

public class TankServiceTests : IDisposable
{
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(20);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FixedClock _time = new(new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc));

    public TankServiceTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private MiniMesDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MiniMesDbContext>().UseSqlite(_connection).Options);

    private async Task<string> ReadyBatchAsync(BatchService service, decimal kg, string recipeName = "Standard Mix")
    {
        var recipe = (await service.GetRecipesAsync()).Single(r => r.Name == recipeName);
        var batch = await service.CreateBatchAsync(recipe.Id, kg);
        await service.MarkReadyAsync(batch.BatchId, "Accepted by ERP.");
        return batch.BatchId;
    }

    private static decimal Level(IEnumerable<Tank> tanks, Container c) => tanks.Single(t => t.Container == c).LevelKg;

    [Fact]
    public async Task Seeder_CreatesBothTanks_AndDoesNotDuplicateThem()
    {
        using var db = CreateContext();
        await DbSeeder.SeedAsync(db);   // second call

        var tanks = await new BatchService(db, _time).GetTanksAsync();

        Assert.Equal(2, tanks.Count);
        Assert.Equal(DbSeeder.TankInitialLevelKg, Level(tanks, Container.A));
        Assert.Equal("Hardener", tanks.Single(t => t.Container == Container.B).MaterialName);
    }

    [Fact]
    public async Task Start_DosesTheRecipeQuantities_OutOfTheTanks()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service, 200m);          // Standard Mix 60/40: 120 kg A, 80 kg B

        await service.StartAsync(id, Cycle);

        var tanks = await service.GetTanksAsync();
        Assert.Equal(680m, Level(tanks, Container.A));
        Assert.Equal(720m, Level(tanks, Container.B));
        Assert.Contains(await service.GetEventsAsync(id), e => e.Message.StartsWith("Dosed 120 kg Resin (TK-A), 80 kg Hardener (TK-B)"));
    }

    [Fact]
    public async Task Start_WritesOneLedgerLinePerTank_LinkedToTheBatch()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service, 200m);

        await service.StartAsync(id, Cycle);

        var movements = await service.GetMovementsAsync(10);
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m => Assert.Equal(id, m.BatchId));
        var a = movements.Single(m => m.Container == Container.A);
        Assert.Equal(-120m, a.DeltaKg);
        Assert.Equal(680m, a.LevelAfterKg);
    }

    [Fact]
    public async Task Start_WithAnEmptyTank_IsRefused_AndConsumesNothing()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        // Two big batches drain TK-A from 800 kg to 50 kg (each takes 375 kg of it)...
        var drain = await ReadyBatchAsync(service, 500m, "High Strength Mix");
        await service.StartAsync(drain, Cycle);
        var drain2 = await ReadyBatchAsync(service, 500m, "High Strength Mix");
        await service.StartAsync(drain2, Cycle);
        var before = await service.GetTanksAsync();

        // ...so a third batch needing 300 kg of TK-A cannot be dosed.
        var id = await ReadyBatchAsync(service, 500m, "Standard Mix");

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.StartAsync(id, Cycle));

        Assert.Contains("Not enough", ex.Message);
        Assert.Contains("Refill the tank", ex.Message);
        var after = await service.GetTanksAsync();
        Assert.Equal(Level(before, Container.A), Level(after, Container.A));   // nothing consumed
        Assert.Equal(Level(before, Container.B), Level(after, Container.B));
        Assert.Equal(BatchStatus.Ready, (await service.GetBatchAsync(id))!.Status);
    }

    [Fact]
    public async Task Start_OnABatchThatIsNotReady_ConsumesNothing()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var recipe = (await service.GetRecipesAsync()).First();
        var created = await service.CreateBatchAsync(recipe.Id, 100m);   // still Created

        await Assert.ThrowsAsync<DomainException>(() => service.StartAsync(created.BatchId, Cycle));

        Assert.All(await service.GetTanksAsync(), t => Assert.Equal(DbSeeder.TankInitialLevelKg, t.LevelKg));
        Assert.Empty(await service.GetMovementsAsync(10));
    }

    [Fact]
    public async Task Refill_AddsMaterial_AndIsLogged()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);

        var tank = await service.RefillTankAsync(Container.B, 150m);

        Assert.Equal(950m, tank.LevelKg);
        var movement = Assert.Single(await service.GetMovementsAsync(10));
        Assert.Equal(150m, movement.DeltaKg);
        Assert.Null(movement.BatchId);
        Assert.Equal("Manual refill", movement.Reason);
    }

    [Fact]
    public async Task Refill_AboveCapacity_IsRejected_AndNothingIsLogged()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);

        await Assert.ThrowsAsync<DomainException>(() => service.RefillTankAsync(Container.A, 500m));

        Assert.Empty(await service.GetMovementsAsync(10));
    }

    [Fact]
    public async Task Movements_AreReturnedNewestFirst_AndLimited()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        await service.RefillTankAsync(Container.A, 10m);
        _time.Advance(TimeSpan.FromMinutes(1));
        await service.RefillTankAsync(Container.A, 20m);
        _time.Advance(TimeSpan.FromMinutes(1));
        await service.RefillTankAsync(Container.A, 30m);

        var latestTwo = await service.GetMovementsAsync(2);

        Assert.Equal(new[] { 30m, 20m }, latestTwo.Select(m => m.DeltaKg));
    }

    [Fact]
    public async Task FaultedBatch_DoesNotGiveTheMaterialBack()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service, 200m);
        await service.StartAsync(id, Cycle);

        await service.FaultAsync(id);

        // Dosed material is gone: the batch failed after it was added to the mixer.
        Assert.Equal(680m, Level(await service.GetTanksAsync(), Container.A));
    }

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

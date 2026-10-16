using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiniMes.Core;
using MiniMes.Core.Models;
using MiniMes.Data;

namespace MiniMes.Tests;

public class BatchRunTimesTests
{
    [Fact]
    public void Start_And_Complete_RecordTheirTimes()
    {
        var batch = TestData.NewBatch();
        var started = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);
        var completed = started.AddSeconds(20);

        batch.MarkReady();
        batch.Start(started);
        batch.Complete(completed);

        Assert.Equal(started, batch.StartedAtUtc);
        Assert.Equal(completed, batch.CompletedAtUtc);
    }

    [Fact]
    public void FailedStart_DoesNotRecordATime()
    {
        var batch = TestData.NewBatch(); // still Created, so Start must fail

        Assert.Throws<DomainException>(() => batch.Start(DateTime.UtcNow));
        Assert.Null(batch.StartedAtUtc);
    }
}

public class MixerServiceTests : IDisposable
{
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(20);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MutableTimeProvider _time = new(new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc));

    public MixerServiceTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private MiniMesDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MiniMesDbContext>().UseSqlite(_connection).Options);

    /// <summary>Creates a batch and moves it to Ready, as a successful ERP answer would.</summary>
    private async Task<string> ReadyBatchAsync(BatchService service)
    {
        var recipe = (await service.GetRecipesAsync()).First();
        var batch = await service.CreateBatchAsync(recipe.Id, 100m);
        await service.MarkReadyAsync(batch.BatchId, "Accepted by ERP.");
        return batch.BatchId;
    }

    [Fact]
    public async Task Start_MovesReadyBatchToRunning_AndLogsIt()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service);

        await service.StartAsync(id, Cycle);

        var batch = (await service.GetBatchAsync(id))!;
        Assert.Equal(BatchStatus.Running, batch.Status);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, batch.StartedAtUtc);
        Assert.Contains(await service.GetEventsAsync(id), e => e.Message.Contains("Mixing started"));
    }

    [Fact]
    public async Task Start_FromCreated_IsRejected()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var recipe = (await service.GetRecipesAsync()).First();
        var batch = await service.CreateBatchAsync(recipe.Id, 100m);

        await Assert.ThrowsAsync<DomainException>(() => service.StartAsync(batch.BatchId, Cycle));
    }

    [Fact]
    public async Task CompleteDue_DoesNothingBeforeTheCycleElapses()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service);
        await service.StartAsync(id, Cycle);

        _time.Advance(TimeSpan.FromSeconds(19));
        var completed = await service.CompleteDueAsync(Cycle);

        Assert.Empty(completed);
        Assert.Equal(BatchStatus.Running, (await service.GetBatchAsync(id))!.Status);
    }

    [Fact]
    public async Task CompleteDue_CompletesTheBatchOnceTheCycleElapsed()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service);
        await service.StartAsync(id, Cycle);

        _time.Advance(Cycle);
        var completed = await service.CompleteDueAsync(Cycle);

        Assert.Equal(new[] { id }, completed);
        var batch = (await service.GetBatchAsync(id))!;
        Assert.Equal(BatchStatus.Completed, batch.Status);
        Assert.NotNull(batch.CompletedAtUtc);
        Assert.Contains(await service.GetEventsAsync(id), e => e.Message.Contains("Mixing completed"));
    }

    [Fact]
    public async Task CompleteDue_RunTwice_DoesNotCompleteAgain()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service);
        await service.StartAsync(id, Cycle);
        _time.Advance(Cycle);

        await service.CompleteDueAsync(Cycle);
        var second = await service.CompleteDueAsync(Cycle);

        Assert.Empty(second);
        Assert.Single((await service.GetEventsAsync(id)).Where(e => e.Message.Contains("Mixing completed")));
    }

    [Fact]
    public async Task CompleteDue_IgnoresBatchesThatAreNotRunning()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        await ReadyBatchAsync(service); // Ready, never started
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Empty(await service.CompleteDueAsync(Cycle));
    }

    [Fact]
    public async Task CompleteDue_SurvivesARestart_BecauseTheStartTimeIsStored()
    {
        string id;
        using (var db = CreateContext())
        {
            var service = new BatchService(db, _time);
            id = await ReadyBatchAsync(service);
            await service.StartAsync(id, Cycle);
        }

        _time.Advance(Cycle); // "the app was down for a while", then comes back with a fresh context
        using var freshDb = CreateContext();
        var restarted = new BatchService(freshDb, _time);

        Assert.Equal(new[] { id }, await restarted.CompleteDueAsync(Cycle));
    }

    [Fact]
    public async Task Fault_MovesRunningBatchToError_WithAReason()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service);
        await service.StartAsync(id, Cycle);

        await service.FaultAsync(id);

        var batch = (await service.GetBatchAsync(id))!;
        Assert.Equal(BatchStatus.Error, batch.Status);
        Assert.Contains("fault", batch.ErrorMessage);
        Assert.Contains(await service.GetEventsAsync(id), e => e.Level == EventLevel.Error);
    }

    [Fact]
    public async Task Fault_OnABatchThatIsNotRunning_IsRejected()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service);

        await Assert.ThrowsAsync<DomainException>(() => service.FaultAsync(id));
    }

    [Fact]
    public async Task FaultedBatch_IsNotCompletedByTheTimer()
    {
        using var db = CreateContext();
        var service = new BatchService(db, _time);
        var id = await ReadyBatchAsync(service);
        await service.StartAsync(id, Cycle);
        await service.FaultAsync(id);

        _time.Advance(Cycle);
        Assert.Empty(await service.CompleteDueAsync(Cycle));
        Assert.Equal(BatchStatus.Error, (await service.GetBatchAsync(id))!.Status);
    }

    /// <summary>A clock the test can move forward.</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableTimeProvider(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

using Microsoft.EntityFrameworkCore;
using MiniMes.Core;
using MiniMes.Core.Models;

namespace MiniMes.Data;

/// <summary>Use cases around batches. Business rules live in the domain classes; this only coordinates them with the database.</summary>
public class BatchService
{
    private readonly MiniMesDbContext _db;
    private readonly TimeProvider _time;

    public BatchService(MiniMesDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public Task<List<Recipe>> GetRecipesAsync() =>
        _db.Recipes.Include(r => r.Ingredients).AsNoTracking().OrderBy(r => r.Name).ToListAsync();

    public async Task<Batch> CreateBatchAsync(int recipeId, decimal plannedQuantityKg)
    {
        var recipe = await _db.Recipes.Include(r => r.Ingredients).SingleOrDefaultAsync(r => r.Id == recipeId)
            ?? throw new DomainException($"Recipe {recipeId} does not exist.");

        var now = _time.GetUtcNow().UtcDateTime;
        // The counter and the batch commit together: if validation or the save fails, the number is rolled back.
        await using var tx = await _db.Database.BeginTransactionAsync();
        var batchId = BatchIdGenerator.Format(now, await NextBatchNumberAsync());

        var batch = new Batch(batchId, recipe, plannedQuantityKg, now); // validates quantity
        _db.Batches.Add(batch);
        _db.BatchEvents.Add(new BatchEvent(batchId, EventLevel.Info,
            $"Batch created: {recipe.Name}, {plannedQuantityKg} kg planned.", now));

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return batch;
    }

    public Task<List<Batch>> ListBatchesAsync() =>
        _db.Batches.Include(b => b.Recipe).ThenInclude(r => r.Ingredients)
            .AsNoTracking().OrderByDescending(b => b.CreatedAtUtc).ThenByDescending(b => b.BatchId).ToListAsync();

    public Task<Batch?> GetBatchAsync(string batchId) =>
        _db.Batches.Include(b => b.Recipe).ThenInclude(r => r.Ingredients)
            .AsNoTracking().SingleOrDefaultAsync(b => b.BatchId == batchId);

    public Task<List<BatchEvent>> GetEventsAsync(string batchId) =>
        _db.BatchEvents.AsNoTracking().Where(e => e.BatchId == batchId)
            .OrderBy(e => e.TimestampUtc).ThenBy(e => e.Id).ToListAsync();

    /// <summary>Stores an XML message exactly as it is about to be sent. Returns its ID so the outcome can be recorded.</summary>
    public async Task<int> SaveXmlMessageAsync(string batchId, string fileName, string xml)
    {
        var message = new BatchXmlMessage(batchId, fileName, xml, _time.GetUtcNow().UtcDateTime);
        _db.XmlMessages.Add(message);
        await _db.SaveChangesAsync();
        return message.Id;
    }

    public async Task RecordXmlResultAsync(int messageId, XmlMessageResult result, string? detail)
    {
        var message = await _db.XmlMessages.SingleOrDefaultAsync(m => m.Id == messageId)
                      ?? throw new DomainException($"XML message {messageId} does not exist.");
        message.RecordResult(result, detail);
        await _db.SaveChangesAsync();
    }

    public Task<List<BatchXmlMessage>> GetXmlMessagesAsync(string batchId) =>
        _db.XmlMessages.AsNoTracking().Where(m => m.BatchId == batchId).OrderBy(m => m.Id).ToListAsync();

    public Task<BatchXmlMessage?> GetXmlMessageAsync(int messageId) =>
        _db.XmlMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == messageId);

    public async Task LogEventAsync(string batchId, EventLevel level, string message)
    {
        _db.BatchEvents.Add(new BatchEvent(batchId, level, message, _time.GetUtcNow().UtcDateTime));
        await _db.SaveChangesAsync();
    }

    /// <summary>Created -> Ready, with an audit event. Saved together in one transaction.</summary>
    public async Task MarkReadyAsync(string batchId, string message)
    {
        var batch = await LoadTrackedAsync(batchId);
        batch.MarkReady();
        _db.BatchEvents.Add(new BatchEvent(batchId, EventLevel.Info, message, _time.GetUtcNow().UtcDateTime));
        await _db.SaveChangesAsync();
    }

    /// <summary>Any unfinished status -> Error, storing the reason on the batch and in the event log.</summary>
    public async Task FailAsync(string batchId, string reason)
    {
        var batch = await LoadTrackedAsync(batchId);
        batch.Fail(reason);
        _db.BatchEvents.Add(new BatchEvent(batchId, EventLevel.Error, reason, _time.GetUtcNow().UtcDateTime));
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Ready -> Running. Doses the recipe's materials out of the tanks first; if any tank cannot supply its share,
    /// nothing is consumed and the batch stays Ready. The mixer cycle then runs for <paramref name="cycle"/>.
    /// </summary>
    public async Task StartAsync(string batchId, TimeSpan cycle)
    {
        var batch = await _db.Batches.Include(b => b.Recipe).ThenInclude(r => r.Ingredients)
                        .SingleOrDefaultAsync(b => b.BatchId == batchId)
                    ?? throw new DomainException($"Batch {batchId} does not exist.");
        var now = _time.GetUtcNow().UtcDateTime;

        var charge = batch.Recipe.CalculateQuantities(batch.PlannedQuantityKg);
        var tanks = await _db.Tanks.ToListAsync();

        // 1. Check everything BEFORE changing anything, so a shortage never leaves a half-dosed batch.
        var shortages = new List<string>();
        foreach (var line in charge)
        {
            var tank = tanks.SingleOrDefault(t => t.Container == line.Container);
            if (tank is null)
                shortages.Add($"TK-{line.Container} does not exist.");
            else if (!tank.CanSupply(line.Kilograms))
                shortages.Add($"Not enough {tank.MaterialName} in TK-{tank.Container}: " +
                              $"{Format(line.Kilograms)} kg needed, {Format(tank.LevelKg)} kg available.");
        }
        if (shortages.Count > 0)
            throw new DomainException(string.Join(" ", shortages) + " Refill the tank, then start the batch again.");

        // 2. Change state (throws if the batch is not Ready), then consume, then save everything together.
        batch.Start(now);

        var doses = new List<string>();
        foreach (var line in charge)
        {
            var tank = tanks.Single(t => t.Container == line.Container);
            tank.Consume(line.Kilograms);
            _db.TankMovements.Add(new TankMovement(tank.Container, -line.Kilograms, tank.LevelKg,
                $"Dosed for batch {batchId}", batchId, now));
            doses.Add($"{Format(line.Kilograms)} kg {tank.MaterialName} (TK-{tank.Container})");
        }

        _db.BatchEvents.Add(new BatchEvent(batchId, EventLevel.Info, $"Dosed {string.Join(", ", doses)}.", now));
        _db.BatchEvents.Add(new BatchEvent(batchId, EventLevel.Info,
            $"Mixing started (cycle {cycle.TotalSeconds:0} s).", now));
        await _db.SaveChangesAsync();
    }

    public Task<List<Tank>> GetTanksAsync() =>
        _db.Tanks.AsNoTracking().OrderBy(t => t.Container).ToListAsync();

    public async Task<List<TankMovement>> GetMovementsAsync(int limit)
    {
        // SQLite cannot sort DateTime reliably in SQL here, so sort the (small) most-recent page in memory.
        var recent = await _db.TankMovements.AsNoTracking().OrderByDescending(m => m.Id).Take(limit).ToListAsync();
        return recent.OrderByDescending(m => m.TimestampUtc).ThenByDescending(m => m.Id).ToList();
    }

    /// <summary>Adds material to a tank (an operator topping it up) and records it in the ledger.</summary>
    public async Task<Tank> RefillTankAsync(Container container, decimal kg)
    {
        var tank = await _db.Tanks.SingleOrDefaultAsync(t => t.Container == container)
                   ?? throw new DomainException($"TK-{container} does not exist.");

        tank.Refill(kg);
        _db.TankMovements.Add(new TankMovement(container, kg, tank.LevelKg, "Manual refill", null,
            _time.GetUtcNow().UtcDateTime));
        await _db.SaveChangesAsync();
        return tank;
    }

    private static string Format(decimal value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Completes every Running batch whose mixer cycle has elapsed. Returns the IDs completed.</summary>
    public async Task<IReadOnlyList<string>> CompleteDueAsync(TimeSpan cycle)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var running = await _db.Batches.Where(b => b.Status == BatchStatus.Running).ToListAsync();

        var completed = new List<string>();
        foreach (var batch in running)
        {
            if (batch.StartedAtUtc is not { } started || now - started < cycle)
                continue;

            batch.Complete(now);
            _db.BatchEvents.Add(new BatchEvent(batch.BatchId, EventLevel.Info,
                $"Mixing completed after {(now - started).TotalSeconds:0} s.", now));
            completed.Add(batch.BatchId);
        }

        if (completed.Count > 0)
            await _db.SaveChangesAsync();
        return completed;
    }

    /// <summary>Simulates an equipment fault. Only a Running batch can fault.</summary>
    public async Task FaultAsync(string batchId)
    {
        var batch = await LoadTrackedAsync(batchId);
        if (batch.Status != BatchStatus.Running)
            throw new DomainException($"Batch {batchId} is {batch.Status}; a fault can only be simulated while it is Running.");

        const string reason = "Simulated equipment fault during mixing.";
        batch.Fail(reason);
        _db.BatchEvents.Add(new BatchEvent(batchId, EventLevel.Error, reason, _time.GetUtcNow().UtcDateTime));
        await _db.SaveChangesAsync();
    }

    private async Task<Batch> LoadTrackedAsync(string batchId) =>
        await _db.Batches.SingleOrDefaultAsync(b => b.BatchId == batchId)
            ?? throw new DomainException($"Batch {batchId} does not exist.");

    /// <summary>
    /// Takes the next number from the persistent global counter. Must run inside a transaction: the UPDATE
    /// takes SQLite's write lock, so two requests can never receive the same number.
    /// </summary>
    private async Task<int> NextBatchNumberAsync()
    {
        const string name = SequenceCounter.BatchName;
        await _db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO Counters (Name, Value) VALUES ({name}, 0)");
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Counters SET Value = Value + 1 WHERE Name = {name}");
        return (int)await _db.Database.SqlQuery<long>($"SELECT Value FROM Counters WHERE Name = {name}").SingleAsync();
    }
}

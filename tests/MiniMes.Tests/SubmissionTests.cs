using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiniMes.Api;
using MiniMes.Core;
using MiniMes.Core.Models;
using MiniMes.Data;
using MiniMes.ErpSimulator;
using MiniMes.Xml;

namespace MiniMes.Tests;

public class ErpBatchReviewerTests
{
    private static readonly DateTime Stamp = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    private static string XmlFor(decimal quantityKg, string batchId = "B-20261016-001")
    {
        var batch = new Batch(batchId, TestData.StandardRecipe(), quantityKg, Stamp);
        return BatchXmlGenerator.GenerateString(batch, Stamp);
    }

    private static ErpBatchReviewer NewReviewer(ErpState? state = null) => new(state ?? new ErpState(), 450m);

    [Fact]
    public void ValidBatch_IsAccepted_WithAReference()
    {
        var decision = NewReviewer().Review(XmlFor(200m));

        Assert.True(decision.Accepted);
        Assert.Equal("ERP-000001", decision.Reference);
    }

    [Fact]
    public void QuantityAboveErpLimit_IsRejected()
    {
        var decision = NewReviewer().Review(XmlFor(451m));

        Assert.False(decision.Accepted);
        Assert.Contains("planning limit", decision.Reason);
    }

    [Fact]
    public void SameBatchTwice_IsRejectedAsDuplicate()
    {
        var reviewer = NewReviewer();
        reviewer.Review(XmlFor(100m));

        var second = reviewer.Review(XmlFor(100m));

        Assert.False(second.Accepted);
        Assert.Contains("duplicate", second.Reason);
    }

    [Fact]
    public void InvalidXml_IsRejected()
    {
        var decision = NewReviewer().Review("<nonsense/>");

        Assert.False(decision.Accepted);
        Assert.Contains("Invalid batch XML", decision.Reason);
    }

    [Fact]
    public void RejectAllMode_RejectsEvenValidBatches()
    {
        var reviewer = NewReviewer(new ErpState { Mode = ErpMode.RejectAll });

        Assert.False(reviewer.Review(XmlFor(100m)).Accepted);
    }
}

public class BatchSubmissionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _outputFolder = Path.Combine(Path.GetTempPath(), "minimes-tests-" + Guid.NewGuid());
    private readonly FakeErpClient _erp = new();

    public BatchSubmissionServiceTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_outputFolder)) Directory.Delete(_outputFolder, true);
    }

    private MiniMesDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MiniMesDbContext>().UseSqlite(_connection).Options);

    private (BatchSubmissionService Submission, BatchService Batches) Create(MiniMesDbContext db)
    {
        var batches = new BatchService(db, TimeProvider.System);
        var options = Options.Create(new XmlOutputOptions { Folder = _outputFolder });
        return (new BatchSubmissionService(batches, _erp, TimeProvider.System, options,
            NullLogger<BatchSubmissionService>.Instance), batches);
    }

    private async Task<string> NewBatchIdAsync(BatchService batches)
    {
        var recipe = (await batches.GetRecipesAsync()).First();
        return (await batches.CreateBatchAsync(recipe.Id, 200m)).BatchId;
    }

    [Fact]
    public async Task Accepted_MovesBatchToReady_SavesXmlAndLogsEvents()
    {
        using var db = CreateContext();
        var (submission, batches) = Create(db);
        var id = await NewBatchIdAsync(batches);
        _erp.Next = new ErpResult(ErpOutcome.Accepted, Reference: "ERP-000042");

        var result = await submission.SubmitAsync(id);

        Assert.Equal(BatchStatus.Ready, result.Batch.Status);
        var file = Assert.Single(Directory.GetFiles(_outputFolder, id + "_*.xml"));
        Assert.True(BatchXmlValidator.Validate(File.ReadAllText(file)).IsValid);
        Assert.True(BatchXmlValidator.Validate(_erp.LastXml!).IsValid); // what was sent over the wire is valid too
        var events = await batches.GetEventsAsync(id);
        Assert.Contains(events, e => e.Message.Contains("ERP-000042"));
    }

    [Fact]
    public async Task Rejected_MovesBatchToError_WithTheReason()
    {
        using var db = CreateContext();
        var (submission, batches) = Create(db);
        var id = await NewBatchIdAsync(batches);
        _erp.Next = new ErpResult(ErpOutcome.Rejected, "Over planning limit");

        var result = await submission.SubmitAsync(id);

        Assert.Equal(BatchStatus.Error, result.Batch.Status);
        Assert.Contains("Over planning limit", result.Batch.ErrorMessage);
        Assert.Contains(await batches.GetEventsAsync(id), e => e.Level == EventLevel.Error);
    }

    [Fact]
    public async Task Unavailable_LeavesBatchCreated_AndAllowsResend()
    {
        using var db = CreateContext();
        var (submission, batches) = Create(db);
        var id = await NewBatchIdAsync(batches);
        _erp.Next = new ErpResult(ErpOutcome.Unavailable, "connection refused");

        var first = await submission.SubmitAsync(id);
        Assert.Equal(BatchStatus.Created, first.Batch.Status);
        Assert.Contains(await batches.GetEventsAsync(id), e => e.Level == EventLevel.Warning);

        _erp.Next = new ErpResult(ErpOutcome.Accepted, Reference: "ERP-000001"); // ERP is back
        var second = await submission.SubmitAsync(id);
        Assert.Equal(BatchStatus.Ready, second.Batch.Status);
    }

    [Fact]
    public async Task BatchNotInCreatedStatus_CannotBeSent()
    {
        using var db = CreateContext();
        var (submission, batches) = Create(db);
        var id = await NewBatchIdAsync(batches);
        _erp.Next = new ErpResult(ErpOutcome.Accepted, Reference: "ERP-000001");
        await submission.SubmitAsync(id); // now Ready

        await Assert.ThrowsAsync<DomainException>(() => submission.SubmitAsync(id));
        Assert.Equal(1, _erp.CallCount); // the ERP was not contacted again
    }

    [Fact]
    public async Task UnknownBatch_IsRejected()
    {
        using var db = CreateContext();
        var (submission, _) = Create(db);

        await Assert.ThrowsAsync<DomainException>(() => submission.SubmitAsync("B-00000000-000"));
    }

    private sealed class FakeErpClient : IErpClient
    {
        public ErpResult Next { get; set; } = new(ErpOutcome.Accepted, Reference: "ERP-000001");
        public string? LastXml { get; private set; }
        public int CallCount { get; private set; }

        public Task<ErpResult> SendBatchAsync(string xml, CancellationToken cancellationToken = default)
        {
            LastXml = xml;
            CallCount++;
            return Task.FromResult(Next);
        }
    }
}

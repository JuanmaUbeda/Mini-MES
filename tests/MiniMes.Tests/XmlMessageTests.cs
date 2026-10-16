using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiniMes.Api;
using MiniMes.Core;
using MiniMes.Core.Models;
using MiniMes.Data;
using MiniMes.Xml;

namespace MiniMes.Tests;

public class BatchXmlMessageDomainTests
{
    private static BatchXmlMessage NewMessage() => new("B-20261016-001", "B-20261016-001_x.xml", "<a/>", DateTime.UtcNow);

    [Fact]
    public void NewMessage_StartsPending()
    {
        Assert.Equal(XmlMessageResult.Pending, NewMessage().Result);
    }

    [Fact]
    public void RecordResult_StoresTheOutcomeAndDetail()
    {
        var message = NewMessage();

        message.RecordResult(XmlMessageResult.Accepted, "ERP reference ERP-000001");

        Assert.Equal(XmlMessageResult.Accepted, message.Result);
        Assert.Equal("ERP reference ERP-000001", message.Detail);
    }

    [Fact]
    public void RecordResult_CannotBeChangedAfterwards()
    {
        var message = NewMessage();
        message.RecordResult(XmlMessageResult.Rejected, "too big");

        Assert.Throws<DomainException>(() => message.RecordResult(XmlMessageResult.Accepted, null));
        Assert.Equal(XmlMessageResult.Rejected, message.Result);
    }

    [Fact]
    public void RecordResult_RejectsPendingAsAResult()
    {
        Assert.Throws<DomainException>(() => NewMessage().RecordResult(XmlMessageResult.Pending, null));
    }

    [Theory]
    [InlineData("", "f.xml", "<a/>")]
    [InlineData("B-1", " ", "<a/>")]
    [InlineData("B-1", "f.xml", "")]
    public void Constructor_RejectsMissingValues(string batchId, string fileName, string xml)
    {
        Assert.Throws<DomainException>(() => new BatchXmlMessage(batchId, fileName, xml, DateTime.UtcNow));
    }
}

public class XmlMessageStorageTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _outputFolder = Path.Combine(Path.GetTempPath(), "minimes-xmlmsg-" + Guid.NewGuid());
    private readonly FakeErp _erp = new();

    public XmlMessageStorageTests()
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
        var submission = new BatchSubmissionService(batches, _erp, TimeProvider.System,
            Options.Create(new XmlOutputOptions { Folder = _outputFolder }), NullLogger<BatchSubmissionService>.Instance);
        return (submission, batches);
    }

    private static async Task<string> NewBatchIdAsync(BatchService batches)
    {
        var recipe = (await batches.GetRecipesAsync()).First();
        return (await batches.CreateBatchAsync(recipe.Id, 200m)).BatchId;
    }

    [Fact]
    public async Task Accepted_StoresTheExactXmlThatWasSent_WithTheErpReference()
    {
        using var db = CreateContext();
        var (submission, batches) = Create(db);
        var id = await NewBatchIdAsync(batches);
        _erp.Next = new ErpResult(ErpOutcome.Accepted, Reference: "ERP-000042");

        await submission.SubmitAsync(id);

        var message = Assert.Single(await batches.GetXmlMessagesAsync(id));
        Assert.Equal(XmlMessageResult.Accepted, message.Result);
        Assert.Equal("ERP reference ERP-000042", message.Detail);
        Assert.Equal(_erp.LastXml, message.Xml);                          // byte-for-byte what went over the wire
        Assert.True(BatchXmlValidator.Validate(message.Xml).IsValid);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(_outputFolder, message.FileName)), message.Xml);   // same as the exported file
    }

    [Fact]
    public async Task Rejected_StoresTheReason()
    {
        using var db = CreateContext();
        var (submission, batches) = Create(db);
        var id = await NewBatchIdAsync(batches);
        _erp.Next = new ErpResult(ErpOutcome.Rejected, "Over planning limit");

        await submission.SubmitAsync(id);

        var message = Assert.Single(await batches.GetXmlMessagesAsync(id));
        Assert.Equal(XmlMessageResult.Rejected, message.Result);
        Assert.Equal("Over planning limit", message.Detail);
    }

    [Fact]
    public async Task ResendAfterAnOutage_KeepsOneRowPerAttempt()
    {
        using var db = CreateContext();
        var (submission, batches) = Create(db);
        var id = await NewBatchIdAsync(batches);

        _erp.Next = new ErpResult(ErpOutcome.Unavailable, "connection refused");
        await submission.SubmitAsync(id);
        _erp.Next = new ErpResult(ErpOutcome.Accepted, Reference: "ERP-000001");
        await submission.SubmitAsync(id);

        var messages = await batches.GetXmlMessagesAsync(id);
        Assert.Equal(new[] { XmlMessageResult.Unavailable, XmlMessageResult.Accepted }, messages.Select(m => m.Result));
        Assert.Equal("connection refused", messages[0].Detail);
    }

    [Fact]
    public async Task BatchNotInCreatedStatus_StoresNoMessage()
    {
        using var db = CreateContext();
        var (submission, batches) = Create(db);
        var id = await NewBatchIdAsync(batches);
        _erp.Next = new ErpResult(ErpOutcome.Accepted, Reference: "ERP-000001");
        await submission.SubmitAsync(id);   // now Ready

        await Assert.ThrowsAsync<DomainException>(() => submission.SubmitAsync(id));

        Assert.Single(await batches.GetXmlMessagesAsync(id));   // the refused attempt left nothing behind
    }

    [Fact]
    public async Task Messages_AreReadBackByIdAndPerBatch()
    {
        using var db = CreateContext();
        var (submission, batches) = Create(db);
        var first = await NewBatchIdAsync(batches);
        var second = await NewBatchIdAsync(batches);
        await submission.SubmitAsync(first);
        await submission.SubmitAsync(second);

        var firstMessage = Assert.Single(await batches.GetXmlMessagesAsync(first));
        var loaded = await batches.GetXmlMessageAsync(firstMessage.Id);

        Assert.Equal(first, loaded!.BatchId);
        Assert.Null(await batches.GetXmlMessageAsync(9999));
    }

    private sealed class FakeErp : IErpClient
    {
        public ErpResult Next { get; set; } = new(ErpOutcome.Accepted, Reference: "ERP-000001");
        public string? LastXml { get; private set; }

        public Task<ErpResult> SendBatchAsync(string xml, CancellationToken cancellationToken = default)
        {
            LastXml = xml;
            return Task.FromResult(Next);
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MiniMes.Api;
using MiniMes.Core.Models;
using MiniMes.ErpSimulator;
using MiniMes.Xml;

namespace MiniMes.Tests.Integration;

// Every test starts the real API and the real ERP simulator in memory and talks to them over HTTP.
// Arrange / Act / Assert is done through the public API only: no test touches the database directly.

public class StartupAndReadingTests
{
    [Fact]
    public async Task Startup_AppliesMigrations_AndSeedsRecipesAndTanks()
    {
        await using var app = new TestApp();

        var recipes = await app.GetAsync<List<RecipeDto>>("/api/recipes");
        var tanks = await app.GetTanksAsync();

        Assert.Equal(new[] { "High Strength Mix", "Standard Mix" }, recipes.Select(r => r.Name));   // sorted by name
        Assert.Equal(2, tanks.Count);
        Assert.All(tanks, t => Assert.Equal(800m, t.LevelKg));
    }

    [Fact]
    public async Task Config_ExposesTheServerLimits()
    {
        await using var app = new TestApp(mixerCycleSeconds: 7);

        var config = await app.GetAsync<ConfigDto>("/api/config");

        Assert.Equal(500m, config.MaxQuantityKg);
        Assert.Equal(7, config.MixerCycleSeconds);      // the test setting reached the real code
        Assert.Equal(0.2m, config.LowLevelFraction);
    }

    [Fact]
    public async Task UnknownBatch_Returns404_WithAJsonError()
    {
        await using var app = new TestApp();

        var response = await app.Api.GetAsync("/api/batches/B-00000000-000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not found", (await app.ReadAsync<ErrorBody>(response)).Error);
    }
}

public class CreatingBatchesTests
{
    [Fact]
    public async Task ValidBatch_Returns201_WithLocation_Id_AndCalculatedQuantities()
    {
        await using var app = new TestApp();
        var recipeId = await app.RecipeIdAsync("Standard Mix");

        var response = await app.Api.PostAsJsonAsync("/api/batches", new CreateBatchRequest(recipeId, 200m), TestApp.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var batch = await app.ReadAsync<BatchDto>(response);
        Assert.Matches(@"^B-\d{8}-001$", batch.BatchId);
        Assert.Equal($"/api/batches/{batch.BatchId}", response.Headers.Location!.OriginalString);
        Assert.Equal(BatchStatus.Created, batch.Status);
        Assert.Equal(120m, batch.Ingredients.Single(i => i.Container == Container.A).Kilograms);
        Assert.Equal(80m, batch.Ingredients.Single(i => i.Container == Container.B).Kilograms);
    }

    [Fact]
    public async Task SecondBatchOfTheDay_GetsTheNextSequenceNumber()
    {
        await using var app = new TestApp();

        var first = await app.CreateBatchAsync(100m);
        var second = await app.CreateBatchAsync(100m);

        Assert.EndsWith("-001", first.BatchId);
        Assert.EndsWith("-002", second.BatchId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(500.5)]
    public async Task InvalidQuantity_Returns400_AndCreatesNothing(double quantity)
    {
        await using var app = new TestApp();
        var recipeId = await app.RecipeIdAsync("Standard Mix");

        var response = await app.Api.PostAsJsonAsync("/api/batches",
            new CreateBatchRequest(recipeId, (decimal)quantity), TestApp.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await app.ReadAsync<ErrorBody>(response)).Error));
        Assert.Empty(await app.GetAsync<List<BatchDto>>("/api/batches"));
    }

    [Fact]
    public async Task UnknownRecipe_Returns400()
    {
        await using var app = new TestApp();

        var response = await app.Api.PostAsJsonAsync("/api/batches", new CreateBatchRequest(999, 100m), TestApp.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not exist", (await app.ReadAsync<ErrorBody>(response)).Error);
    }

    [Fact]
    public async Task MalformedJson_Returns400_NotAServerError()
    {
        await using var app = new TestApp();
        using var body = new StringContent("{ this is not json", Encoding.UTF8, "application/json");

        var response = await app.Api.PostAsync("/api/batches", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NewBatch_AppearsInTheListAndInItsDetailWithAnEventLog()
    {
        await using var app = new TestApp();
        var created = await app.CreateBatchAsync(150m);

        var list = await app.GetAsync<List<BatchDto>>("/api/batches");
        var detail = await app.GetAsync<BatchDetailDto>($"/api/batches/{created.BatchId}");

        Assert.Equal(created.BatchId, Assert.Single(list).BatchId);
        Assert.Contains(detail.Events, e => e.Message.StartsWith("Batch created"));
    }
}

public class XmlExportTests
{
    [Fact]
    public async Task XmlPreview_IsXml_AndPassesTheSchema()
    {
        await using var app = new TestApp();
        var batch = await app.CreateBatchAsync(200m);

        var response = await app.Api.GetAsync($"/api/batches/{batch.BatchId}/xml");
        var xml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType!.MediaType);
        Assert.True(BatchXmlValidator.Validate(xml).IsValid);
        Assert.Contains(batch.BatchId, xml);
    }

    [Fact]
    public async Task XmlPreview_OfAnUnknownBatch_Returns404()
    {
        await using var app = new TestApp();

        var response = await app.Api.GetAsync("/api/batches/NOPE/xml");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public class ErpIntegrationTests
{
    [Fact]
    public async Task AcceptedBatch_BecomesReady_SavesTheXml_AndTheErpRemembersIt()
    {
        await using var app = new TestApp();
        var batch = await app.CreateBatchAsync(200m);

        var response = await app.PostAsync($"/api/batches/{batch.BatchId}/submit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await app.ReadAsync<SubmitResultDto>(response);
        Assert.Equal(ErpOutcome.Accepted, result.Outcome);
        Assert.Equal(BatchStatus.Ready, result.Batch.Status);

        var file = Assert.Single(Directory.GetFiles(app.XmlFolder, batch.BatchId + "_*.xml"));   // exported file
        Assert.True(BatchXmlValidator.Validate(await File.ReadAllTextAsync(file)).IsValid);

        var erpState = await app.Erp.GetFromJsonAsync<JsonElement>("/admin/mode");               // the ERP really got it
        Assert.True(erpState.GetProperty("accepted").TryGetProperty(batch.BatchId, out _));
    }

    [Fact]
    public async Task SentXml_IsKeptInTheDatabase_AndServedBack()
    {
        await using var app = new TestApp();
        var batch = await app.CreateBatchAsync(200m);
        await app.PostAsync($"/api/batches/{batch.BatchId}/submit");

        var detail = await app.GetAsync<BatchDetailDto>($"/api/batches/{batch.BatchId}");

        var message = Assert.Single(detail.Messages);
        Assert.Equal(XmlMessageResult.Accepted, message.Result);
        Assert.StartsWith("ERP reference ERP-", message.Detail);

        var response = await app.Api.GetAsync($"/api/messages/{message.Id}/xml");
        var xml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains(batch.BatchId, xml);
        Assert.True(BatchXmlValidator.Validate(xml).IsValid);
    }

    [Fact]
    public async Task ErpOutage_LeavesAnUnavailableMessage_ThenAnAcceptedOneAfterTheResend()
    {
        await using var app = new TestApp();
        var batch = await app.CreateBatchAsync(200m);
        await app.SetErpModeAsync(ErpMode.Unavailable);
        await app.PostAsync($"/api/batches/{batch.BatchId}/submit");
        await app.SetErpModeAsync(ErpMode.Normal);
        await app.PostAsync($"/api/batches/{batch.BatchId}/submit");

        var detail = await app.GetAsync<BatchDetailDto>($"/api/batches/{batch.BatchId}");

        Assert.Equal(new[] { XmlMessageResult.Unavailable, XmlMessageResult.Accepted }, detail.Messages.Select(m => m.Result));
    }

    [Fact]
    public async Task UnknownXmlMessage_Returns404()
    {
        await using var app = new TestApp();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Api.GetAsync("/api/messages/999/xml")).StatusCode);
    }

    [Fact]
    public async Task BatchAboveTheErpLimit_IsRejected_AndBecomesError_WithTheReason()
    {
        await using var app = new TestApp();
        var batch = await app.CreateBatchAsync(480m);   // valid for the mixer (500) but above the ERP's 450

        var response = await app.PostAsync($"/api/batches/{batch.BatchId}/submit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // a refusal is a decision, not a failure
        var result = await app.ReadAsync<SubmitResultDto>(response);
        Assert.Equal(ErpOutcome.Rejected, result.Outcome);
        Assert.Equal(BatchStatus.Error, result.Batch.Status);
        Assert.Contains("planning limit", result.Batch.ErrorMessage);
    }

    [Fact]
    public async Task ErpDown_Returns503_KeepsTheBatchCreated_AndAResendWorksLater()
    {
        await using var app = new TestApp();
        var batch = await app.CreateBatchAsync(200m);
        await app.SetErpModeAsync(ErpMode.Unavailable);

        var down = await app.PostAsync($"/api/batches/{batch.BatchId}/submit");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal(ErpOutcome.Unavailable, (await app.ReadAsync<SubmitResultDto>(down)).Outcome);
        Assert.Equal(BatchStatus.Created, (await app.GetBatchAsync(batch.BatchId)).Status);   // no decision, no change

        await app.SetErpModeAsync(ErpMode.Normal);
        var retry = await app.PostAsync($"/api/batches/{batch.BatchId}/submit");

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(BatchStatus.Ready, (await app.GetBatchAsync(batch.BatchId)).Status);
    }

    [Fact]
    public async Task ErpRejectAllMode_RejectsAnOtherwiseValidBatch()
    {
        await using var app = new TestApp();
        var batch = await app.CreateBatchAsync(100m);
        await app.SetErpModeAsync(ErpMode.RejectAll);

        var result = await app.ReadAsync<SubmitResultDto>(await app.PostAsync($"/api/batches/{batch.BatchId}/submit"));

        Assert.Equal(ErpOutcome.Rejected, result.Outcome);
        Assert.Equal(BatchStatus.Error, result.Batch.Status);
    }

    [Fact]
    public async Task SubmittingTwice_IsRefused_BecauseTheBatchIsNoLongerCreated()
    {
        await using var app = new TestApp();
        var batch = await app.CreateReadyBatchAsync(100m);

        var again = await app.PostAsync($"/api/batches/{batch.BatchId}/submit");

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("only Created batches", (await app.ReadAsync<ErrorBody>(again)).Error);
    }

    [Fact]
    public async Task SubmittingAnUnknownBatch_Returns404()
    {
        await using var app = new TestApp();

        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync("/api/batches/NOPE/submit")).StatusCode);
    }
}

public class MixerAndInventoryTests
{
    [Fact]
    public async Task FullLifecycle_Created_Ready_Running_Completed_WithDosingAndLedger()
    {
        await using var app = new TestApp(mixerCycleSeconds: 1);
        var batch = await app.CreateReadyBatchAsync(200m);          // 120 kg Resin, 80 kg Hardener

        var start = await app.PostAsync($"/api/batches/{batch.BatchId}/start");

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(BatchStatus.Running, (await app.ReadAsync<BatchDto>(start)).Status);
        Assert.Equal(680m, await app.TankLevelAsync(Container.A));      // dosed at start
        Assert.Equal(720m, await app.TankLevelAsync(Container.B));

        var done = await app.WaitForStatusAsync(batch.BatchId, BatchStatus.Completed);   // the background service finishes it

        Assert.NotNull(done.StartedAtUtc);
        Assert.NotNull(done.CompletedAtUtc);

        var ledger = await app.GetAsync<List<TankMovementDto>>("/api/tanks/movements");
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, m => Assert.Equal(batch.BatchId, m.BatchId));

        var detail = await app.GetAsync<BatchDetailDto>($"/api/batches/{batch.BatchId}");
        Assert.Contains(detail.Events, e => e.Message.StartsWith("Dosed 120 kg Resin"));
        Assert.Contains(detail.Events, e => e.Message.StartsWith("Mixing completed"));
    }

    [Fact]
    public async Task StartingABatchThatIsNotReady_Returns400_AndConsumesNothing()
    {
        await using var app = new TestApp();
        var batch = await app.CreateBatchAsync(100m);   // still Created

        var response = await app.PostAsync($"/api/batches/{batch.BatchId}/start");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(800m, await app.TankLevelAsync(Container.A));
        Assert.Empty(await app.GetAsync<List<TankMovementDto>>("/api/tanks/movements"));
    }

    [Fact]
    public async Task ShortTank_RefusesTheStart_ChangesNothing_AndWorksAfterARefill()
    {
        await using var app = new TestApp();
        // High Strength Mix 450 kg takes 337.5 kg of Resin. Two batches leave 125 kg, so a third cannot be dosed.
        foreach (var _ in new[] { 1, 2 })
        {
            var ok = await app.CreateReadyBatchAsync(450m, "High Strength Mix");
            (await app.PostAsync($"/api/batches/{ok.BatchId}/start")).EnsureSuccessStatusCode();
        }
        var third = await app.CreateReadyBatchAsync(450m, "High Strength Mix");
        var levelBefore = await app.TankLevelAsync(Container.A);

        var refused = await app.PostAsync($"/api/batches/{third.BatchId}/start");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var message = (await app.ReadAsync<ErrorBody>(refused)).Error;
        Assert.Contains("Not enough Resin in TK-A", message);
        Assert.Contains("337.5 kg needed", message);
        Assert.Equal(levelBefore, await app.TankLevelAsync(Container.A));                      // nothing consumed
        Assert.Equal(BatchStatus.Ready, (await app.GetBatchAsync(third.BatchId)).Status);      // still startable

        var refill = await app.Api.PostAsJsonAsync("/api/tanks/A/refill", new RefillRequest(600m), TestApp.Json);
        Assert.Equal(HttpStatusCode.OK, refill.StatusCode);

        var retry = await app.PostAsync($"/api/batches/{third.BatchId}/start");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(BatchStatus.Running, (await app.GetBatchAsync(third.BatchId)).Status);
    }

    [Fact]
    public async Task Refill_AboveCapacity_Returns400_UnknownTank404_AndNegative400()
    {
        await using var app = new TestApp();

        var tooMuch = await app.Api.PostAsJsonAsync("/api/tanks/A/refill", new RefillRequest(300m), TestApp.Json);
        var unknown = await app.Api.PostAsJsonAsync("/api/tanks/Z/refill", new RefillRequest(10m), TestApp.Json);
        var negative = await app.Api.PostAsJsonAsync("/api/tanks/A/refill", new RefillRequest(-5m), TestApp.Json);

        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.Contains("at most 200 kg more", (await app.ReadAsync<ErrorBody>(tooMuch)).Error);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(800m, await app.TankLevelAsync(Container.A));
    }

    [Fact]
    public async Task Refill_RaisesTheLevel_AndIsRecordedWithoutABatch()
    {
        await using var app = new TestApp();

        var response = await app.Api.PostAsJsonAsync("/api/tanks/B/refill", new RefillRequest(150m), TestApp.Json);

        Assert.Equal(950m, (await app.ReadAsync<TankDto>(response)).LevelKg);
        var movement = Assert.Single(await app.GetAsync<List<TankMovementDto>>("/api/tanks/movements"));
        Assert.Equal(150m, movement.DeltaKg);
        Assert.Null(movement.BatchId);
    }

    [Fact]
    public async Task SimulatedFault_FailsARunningBatch_AndTheMaterialIsNotReturned()
    {
        await using var app = new TestApp();   // long cycle: the batch stays Running
        var batch = await app.CreateReadyBatchAsync(200m);
        (await app.PostAsync($"/api/batches/{batch.BatchId}/start")).EnsureSuccessStatusCode();

        var fault = await app.PostAsync($"/api/batches/{batch.BatchId}/fault");

        Assert.Equal(HttpStatusCode.OK, fault.StatusCode);
        var failed = await app.ReadAsync<BatchDto>(fault);
        Assert.Equal(BatchStatus.Error, failed.Status);
        Assert.Contains("fault", failed.ErrorMessage);
        Assert.Equal(680m, await app.TankLevelAsync(Container.A));   // already dosed, so not given back
    }

    [Fact]
    public async Task Fault_OnABatchThatIsNotRunning_Returns400()
    {
        await using var app = new TestApp();
        var batch = await app.CreateReadyBatchAsync(100m);   // Ready, not Running

        var response = await app.PostAsync($"/api/batches/{batch.BatchId}/fault");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BatchStaysRunning_UntilTheCycleHasElapsed()
    {
        await using var app = new TestApp(mixerCycleSeconds: 60);
        var batch = await app.CreateReadyBatchAsync(100m);
        (await app.PostAsync($"/api/batches/{batch.BatchId}/start")).EnsureSuccessStatusCode();

        await Task.Delay(2500);   // longer than the background timer's tick, far shorter than the cycle

        Assert.Equal(BatchStatus.Running, (await app.GetBatchAsync(batch.BatchId)).Status);
    }
}

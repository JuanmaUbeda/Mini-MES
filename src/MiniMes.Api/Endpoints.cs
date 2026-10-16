using Microsoft.Extensions.Options;
using MiniMes.Core.Models;
using MiniMes.Data;
using MiniMes.Xml;

namespace MiniMes.Api;

public static class Endpoints
{
    public static void MapMesEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/recipes", async (BatchService service) =>
            (await service.GetRecipesAsync()).Select(RecipeDto.From));

        api.MapGet("/batches", async (BatchService service) =>
            (await service.ListBatchesAsync()).Select(BatchDto.From));

        api.MapGet("/batches/{batchId}", async (string batchId, BatchService service) =>
        {
            var batch = await service.GetBatchAsync(batchId);
            if (batch is null)
                return Results.NotFound(new { error = $"Batch {batchId} not found." });

            var events = await service.GetEventsAsync(batchId);
            var messages = await service.GetXmlMessagesAsync(batchId);
            return Results.Ok(new BatchDetailDto(
                BatchDto.From(batch),
                events.Select(BatchEventDto.From).ToList(),
                messages.Select(XmlMessageDto.From).ToList()));
        });

        // An XML message exactly as it was sent to the ERP, read back from the database.
        api.MapGet("/messages/{messageId:int}/xml", async (int messageId, BatchService service) =>
        {
            var message = await service.GetXmlMessageAsync(messageId);
            return message is null
                ? Results.NotFound(new { error = $"XML message {messageId} not found." })
                : Results.Content(message.Xml, "application/xml");
        });

        // Preview of the XML that /submit sends to the external system, validated against the XSD first.
        api.MapGet("/batches/{batchId}/xml", async (string batchId, BatchService service, TimeProvider time) =>
        {
            var batch = await service.GetBatchAsync(batchId);
            if (batch is null)
                return Results.NotFound(new { error = $"Batch {batchId} not found." });

            var now = time.GetUtcNow().UtcDateTime;
            var validation = BatchXmlValidator.Validate(BatchXmlGenerator.Generate(batch, now));
            if (!validation.IsValid)
                throw new InvalidOperationException("Generated XML failed schema validation: " + string.Join("; ", validation.Errors));

            return Results.Content(BatchXmlGenerator.GenerateString(batch, now), "application/xml");
        });

        // Send the batch to the external service; the response shows the outcome and the updated batch.
        api.MapPost("/batches/{batchId}/submit", async (string batchId, BatchService service, BatchSubmissionService submission) =>
        {
            if (await service.GetBatchAsync(batchId) is null)
                return Results.NotFound(new { error = $"Batch {batchId} not found." });

            var result = await submission.SubmitAsync(batchId);
            var dto = new SubmitResultDto(result.Outcome, result.Message, BatchDto.From(result.Batch));

            // 503 when the ERP could not be reached, so callers can tell "try again later" from a decision.
            return result.Outcome == ErpOutcome.Unavailable
                ? Results.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(dto);
        });

        // Tank inventory: current levels, the material ledger, and manual refills.
        api.MapGet("/tanks", async (BatchService service) =>
            (await service.GetTanksAsync()).Select(TankDto.From));

        api.MapGet("/tanks/movements", async (BatchService service, int? limit) =>
            (await service.GetMovementsAsync(Math.Clamp(limit ?? 20, 1, 100))).Select(TankMovementDto.From));

        api.MapPost("/tanks/{container}/refill", async (string container, RefillRequest request, BatchService service) =>
        {
            if (!Enum.TryParse<Container>(container, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Results.NotFound(new { error = $"Tank '{container}' not found. Use A or B." });

            var tank = await service.RefillTankAsync(parsed, request.QuantityKg);
            return Results.Ok(TankDto.From(tank));
        });

        api.MapGet("/config",(IOptions<MixerOptions> mixer) =>
            new ConfigDto(Batch.MaxQuantityKg, mixer.Value.CycleSeconds, Tank.LowLevelFraction));

        // Ready -> Running. A background service completes the batch when the mixer cycle has elapsed.
        api.MapPost("/batches/{batchId}/start", async (string batchId, BatchService service, IOptions<MixerOptions> mixer) =>
        {
            if (await service.GetBatchAsync(batchId) is null)
                return Results.NotFound(new { error = $"Batch {batchId} not found." });

            await service.StartAsync(batchId, TimeSpan.FromSeconds(mixer.Value.CycleSeconds));
            return Results.Ok(BatchDto.From((await service.GetBatchAsync(batchId))!));
        });

        // Demo helper: makes a Running batch fail, to show the error path.
        api.MapPost("/batches/{batchId}/fault", async (string batchId, BatchService service) =>
        {
            if (await service.GetBatchAsync(batchId) is null)
                return Results.NotFound(new { error = $"Batch {batchId} not found." });

            await service.FaultAsync(batchId);
            return Results.Ok(BatchDto.From((await service.GetBatchAsync(batchId))!));
        });

        api.MapPost("/batches", async (CreateBatchRequest request, BatchService service) =>
        {
            var batch = await service.CreateBatchAsync(request.RecipeId, request.PlannedQuantityKg);
            var created = await service.GetBatchAsync(batch.BatchId);
            return Results.Created($"/api/batches/{batch.BatchId}", BatchDto.From(created!));
        });
    }
}

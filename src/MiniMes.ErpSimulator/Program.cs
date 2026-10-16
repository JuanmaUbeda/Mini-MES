using System.Text.Json.Serialization;
using MiniMes.ErpSimulator;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ErpState>();
builder.Services.AddSingleton(sp => new ErpBatchReviewer(
    sp.GetRequiredService<ErpState>(),
    builder.Configuration.GetValue("Erp:MaxQuantityKg", 450m)));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// The interface MiniMES calls: send batch XML, get an accept/reject decision.
app.MapPost("/api/batches", async (HttpRequest request, ErpState state, ErpBatchReviewer reviewer) =>
{
    if (state.Mode == ErpMode.Unavailable)
    {
        app.Logger.LogWarning("Request refused: simulator is in Unavailable mode.");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    using var reader = new StreamReader(request.Body);
    var xml = await reader.ReadToEndAsync();

    var decision = reviewer.Review(xml);
    var response = new ErpResponse(decision.Accepted, decision.Reason, decision.Reference);

    if (decision.Accepted)
    {
        app.Logger.LogInformation("ACCEPTED batch, reference {Reference}", decision.Reference);
        return Results.Ok(response);
    }

    app.Logger.LogWarning("REJECTED batch: {Reason}", decision.Reason);
    return Results.UnprocessableEntity(response); // 422: understood, but refused on business grounds
});

// Simulation controls, so you can demonstrate each outcome on demand.
app.MapGet("/admin/mode", (ErpState state) => new { mode = state.Mode, accepted = state.Accepted });
app.MapPut("/admin/mode", (ModeRequest request, ErpState state) =>
{
    state.Mode = request.Mode;
    app.Logger.LogInformation("Simulator mode set to {Mode}", request.Mode);
    return Results.Ok(new { mode = state.Mode });
});

app.Run();

public record ErpResponse(bool Accepted, string? Reason, string? Reference);
public record ModeRequest(ErpMode Mode);

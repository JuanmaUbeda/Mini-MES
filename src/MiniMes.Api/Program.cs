using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MiniMes.Api;
using MiniMes.Core;
using MiniMes.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MiniMesDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("MiniMes")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<BatchService>();
builder.Services.AddScoped<BatchSubmissionService>();
builder.Services.Configure<XmlOutputOptions>(builder.Configuration.GetSection("Output"));
builder.Services.Configure<MixerOptions>(builder.Configuration.GetSection("Mixer"));
builder.Services.AddHostedService<MixerSimulationService>();

// Typed HttpClient: the factory manages connections; the timeout stops a hung ERP from hanging us.
builder.Services.AddHttpClient<IErpClient, ErpClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Erp:BaseUrl"] ?? "http://localhost:5063");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Show enums as text ("Created") in JSON instead of numbers.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Business-rule violations become a clean 400 response instead of a 500 crash.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (exception is DomainException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
    else if (exception is BadHttpRequestException)
    {
        // The client sent something unreadable (broken JSON, wrong types): their mistake, not a server error.
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "The request body is not valid JSON for this endpoint." });
    }
    else
    {
        app.Logger.LogError(exception, "Unhandled error on {Path}", context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "Unexpected server error." });
    }
}));

// Create/upgrade the database and seed starter recipes at startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MiniMesDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db);
}

// Serve the operator screen (wwwroot/index.html) at "/".
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapMesEndpoints();

app.Run();

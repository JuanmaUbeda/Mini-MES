using Microsoft.Extensions.Options;
using MiniMes.Data;

namespace MiniMes.Api;

public class MixerOptions
{
    /// <summary>How long a mixing cycle lasts, in seconds.</summary>
    public int CycleSeconds { get; set; } = 20;
}

/// <summary>
/// Plays the part of the mixer's controller: once a second it completes the batches whose cycle has elapsed.
/// The start time is stored in the database, so a restart in the middle of a cycle loses nothing.
/// </summary>
public class MixerSimulationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly MixerOptions _options;
    private readonly ILogger<MixerSimulationService> _logger;

    public MixerSimulationService(IServiceScopeFactory scopes, IOptions<MixerOptions> options, ILogger<MixerSimulationService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    // A BackgroundService lives forever, but DbContext is per-unit-of-work: make a scope per tick.
                    using var scope = _scopes.CreateScope();
                    var batches = scope.ServiceProvider.GetRequiredService<BatchService>();
                    foreach (var id in await batches.CompleteDueAsync(TimeSpan.FromSeconds(_options.CycleSeconds)))
                        _logger.LogInformation("Batch {BatchId}: mixing completed.", id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed tick must not kill the simulation.
                    _logger.LogError(ex, "Mixer simulation tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}

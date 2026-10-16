using System.Net;

namespace MiniMes.Api;

public enum ErpOutcome
{
    Accepted,
    /// <summary>The ERP understood the batch and refused it. Final.</summary>
    Rejected,
    /// <summary>We couldn't get a decision (down, timeout, 5xx). Temporary: the batch can be resent.</summary>
    Unavailable
}

public record ErpResult(ErpOutcome Outcome, string? Reason = null, string? Reference = null);

/// <summary>The door to the external system. An interface so tests can replace the real HTTP call.</summary>
public interface IErpClient
{
    Task<ErpResult> SendBatchAsync(string xml, CancellationToken cancellationToken = default);
}

public class ErpClient : IErpClient
{
    private readonly HttpClient _http;
    private readonly ILogger<ErpClient> _logger;

    public ErpClient(HttpClient http, ILogger<ErpClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ErpResult> SendBatchAsync(string xml, CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml");
            using var response = await _http.PostAsync("/api/batches", content, cancellationToken);

            // 200 = accepted, 422 = refused: both carry a JSON decision. Anything else is a technical failure.
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity)
            {
                var body = await response.Content.ReadFromJsonAsync<ErpResponse>(cancellationToken);
                if (body is null)
                    return new ErpResult(ErpOutcome.Unavailable, "ERP returned an empty response.");

                return body.Accepted
                    ? new ErpResult(ErpOutcome.Accepted, Reference: body.Reference)
                    : new ErpResult(ErpOutcome.Rejected, body.Reason ?? "No reason given.");
            }

            _logger.LogWarning("ERP answered unexpected status {Status}", (int)response.StatusCode);
            return new ErpResult(ErpOutcome.Unavailable, $"ERP answered HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "Could not reach the ERP");
            return new ErpResult(ErpOutcome.Unavailable, $"Could not reach the ERP: {ex.Message}");
        }
    }

    private record ErpResponse(bool Accepted, string? Reason, string? Reference);
}

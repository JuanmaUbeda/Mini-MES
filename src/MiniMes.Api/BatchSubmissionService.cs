using Microsoft.Extensions.Options;
using MiniMes.Core;
using MiniMes.Core.Models;
using MiniMes.Data;
using MiniMes.Xml;

namespace MiniMes.Api;

public class XmlOutputOptions
{
    /// <summary>Folder (relative to the app, or absolute) where a copy of each sent XML is saved.</summary>
    public string Folder { get; set; } = "output/xml";
}

public record SubmissionResult(ErpOutcome Outcome, string Message, Batch Batch);

/// <summary>The workflow "send a batch to the ERP": XML -> file -> ERP -> new status, logging each step.</summary>
public class BatchSubmissionService
{
    private readonly BatchService _batches;
    private readonly IErpClient _erp;
    private readonly TimeProvider _time;
    private readonly XmlOutputOptions _options;
    private readonly ILogger<BatchSubmissionService> _logger;

    public BatchSubmissionService(
        BatchService batches, IErpClient erp, TimeProvider time,
        IOptions<XmlOutputOptions> options, ILogger<BatchSubmissionService> logger)
    {
        _batches = batches;
        _erp = erp;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SubmissionResult> SubmitAsync(string batchId, CancellationToken cancellationToken = default)
    {
        var batch = await _batches.GetBatchAsync(batchId)
            ?? throw new DomainException($"Batch {batchId} does not exist.");

        if (batch.Status != BatchStatus.Created)
            throw new DomainException($"Batch {batchId} is {batch.Status}; only Created batches can be sent to the ERP.");

        // 1. Build and self-check the XML before it leaves the building.
        var now = _time.GetUtcNow().UtcDateTime;
        var document = BatchXmlGenerator.Generate(batch, now);
        var validation = BatchXmlValidator.Validate(document);
        if (!validation.IsValid)
        {
            var problem = $"Generated XML failed schema validation: {string.Join("; ", validation.Errors)}";
            _logger.LogError("Batch {BatchId}: {Problem}", batchId, problem);
            await _batches.FailAsync(batchId, problem);
            return new SubmissionResult(ErpOutcome.Rejected, problem, (await _batches.GetBatchAsync(batchId))!);
        }
        var xml = BatchXmlGenerator.GenerateString(batch, now);

        // 2. Keep a copy on disk: the exported file and an audit trail.
        var fileName = $"{batchId}_{now:yyyyMMddTHHmmss}.xml";
        var path = await SaveXmlAsync(fileName, xml, cancellationToken);
        var messageId = await _batches.SaveXmlMessageAsync(batchId, fileName, xml);   // and in the database
        await _batches.LogEventAsync(batchId, EventLevel.Info, $"XML generated and saved as {fileName}.");
        _logger.LogInformation("Batch {BatchId}: XML saved to {Path}", batchId, path);

        // 3. Send it, record how the exchange ended, and act on the answer.
        await _batches.LogEventAsync(batchId, EventLevel.Info, "XML sent to the ERP.");
        var result = await _erp.SendBatchAsync(xml, cancellationToken);

        await _batches.RecordXmlResultAsync(messageId,
            result.Outcome switch
            {
                ErpOutcome.Accepted => XmlMessageResult.Accepted,
                ErpOutcome.Rejected => XmlMessageResult.Rejected,
                _ => XmlMessageResult.Unavailable
            },
            result.Outcome == ErpOutcome.Accepted ? $"ERP reference {result.Reference}" : result.Reason);

        string message;
        switch (result.Outcome)
        {
            case ErpOutcome.Accepted:
                message = $"Accepted by ERP (reference {result.Reference}).";
                await _batches.MarkReadyAsync(batchId, message);
                _logger.LogInformation("Batch {BatchId}: {Message}", batchId, message);
                break;

            case ErpOutcome.Rejected:
                message = $"Rejected by ERP: {result.Reason}";
                await _batches.FailAsync(batchId, message);
                _logger.LogWarning("Batch {BatchId}: {Message}", batchId, message);
                break;

            default: // Unavailable: no decision was made, so the status must not change.
                message = $"ERP unavailable: {result.Reason} The batch stays Created and can be sent again.";
                await _batches.LogEventAsync(batchId, EventLevel.Warning, message);
                _logger.LogError("Batch {BatchId}: {Message}", batchId, message);
                break;
        }

        var updated = (await _batches.GetBatchAsync(batchId))!;
        return new SubmissionResult(result.Outcome, message, updated);
    }

    private async Task<string> SaveXmlAsync(string fileName, string xml, CancellationToken cancellationToken)
    {
        var folder = Path.GetFullPath(_options.Folder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        await File.WriteAllTextAsync(path, xml, cancellationToken);
        return path;
    }
}

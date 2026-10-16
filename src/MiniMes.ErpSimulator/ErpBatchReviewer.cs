using System.Xml.Linq;
using MiniMes.Xml;

namespace MiniMes.ErpSimulator;

public record ErpDecision(bool Accepted, string? Reason, string? Reference);

/// <summary>The simulated ERP's business rules: decides whether to accept a batch.</summary>
public class ErpBatchReviewer
{
    private static readonly XNamespace Ns = BatchXmlGenerator.Namespace;
    private readonly ErpState _state;
    private readonly decimal _maxQuantityKg;

    public ErpBatchReviewer(ErpState state, decimal maxQuantityKg)
    {
        _state = state;
        _maxQuantityKg = maxQuantityKg;
    }

    public ErpDecision Review(string xml)
    {
        if (_state.Mode == ErpMode.RejectAll)
            return Reject("ERP is configured to reject all batches (simulation).");

        var validation = BatchXmlValidator.Validate(xml);
        if (!validation.IsValid)
            return Reject($"Invalid batch XML: {validation.Errors[0]}");

        // The XML is schema-valid now, so these reads are safe.
        var root = XDocument.Parse(xml).Root!;
        var batchId = root.Element(Ns + "BatchId")!.Value;
        var quantity = decimal.Parse(root.Element(Ns + "PlannedQuantityKg")!.Value, System.Globalization.CultureInfo.InvariantCulture);

        if (_state.Accepted.ContainsKey(batchId))
            return Reject($"Batch {batchId} was already accepted (duplicate).");

        if (quantity > _maxQuantityKg)
            return Reject(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Planned quantity {quantity:0.###} kg exceeds the ERP planning limit of {_maxQuantityKg:0.###} kg."));

        var reference = _state.NextReference();
        _state.Accepted[batchId] = reference;
        return new ErpDecision(true, null, reference);
    }

    private static ErpDecision Reject(string reason) => new(false, reason, null);
}

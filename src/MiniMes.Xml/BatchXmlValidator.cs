using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace MiniMes.Xml;

public record XmlValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public static class BatchXmlValidator
{
    // Loaded once; XmlSchemaSet is safe to share for validation.
    private static readonly Lazy<XmlSchemaSet> Schemas = new(LoadSchemas);

    public static XmlValidationResult Validate(XDocument document)
    {
        // The .NET validator silently accepts a root element it has no schema for, so check it explicitly.
        var expectedRoot = XNamespace.Get(BatchXmlGenerator.Namespace) + "BatchOrder";
        if (document.Root?.Name != expectedRoot)
        {
            return new XmlValidationResult(false, new[]
            {
                $"Unexpected root element '{document.Root?.Name}'; expected '{expectedRoot}'."
            });
        }

        var errors = new List<string>();
        document.Validate(Schemas.Value, (_, e) => errors.Add(Describe(e)));
        return new XmlValidationResult(errors.Count == 0, errors);
    }

    /// <summary>Validates raw text, e.g. XML received from another system. Malformed XML is reported as an error, not thrown.</summary>
    public static XmlValidationResult Validate(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            return new XmlValidationResult(false, new[] { $"XML is not well-formed: {ex.Message}" });
        }
        return Validate(document);
    }

    private static string Describe(ValidationEventArgs e) =>
        e.Exception is { LineNumber: > 0 } ex
            ? $"Line {ex.LineNumber}: {e.Message}"
            : e.Message;

    private static XmlSchemaSet LoadSchemas()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BatchSchema.xsd")
            ?? throw new InvalidOperationException("BatchSchema.xsd is missing from the MiniMes.Xml assembly.");
        using var reader = XmlReader.Create(stream);

        var set = new XmlSchemaSet();
        set.Add(BatchXmlGenerator.Namespace, reader);
        return set;
    }
}

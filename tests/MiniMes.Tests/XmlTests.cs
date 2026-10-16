using System.Globalization;
using System.Xml.Linq;
using MiniMes.Core.Models;
using MiniMes.Xml;

namespace MiniMes.Tests;

public class XmlTests
{
    private static readonly DateTime Stamp = new(2026, 10, 16, 14, 30, 0, DateTimeKind.Utc);
    private static readonly XNamespace Ns = BatchXmlGenerator.Namespace;

    private static string ValidXml(decimal quantityKg = 200m) =>
        BatchXmlGenerator.GenerateString(TestData.NewBatch(quantityKg), Stamp);

    [Fact]
    public void GeneratedXml_PassesSchemaValidation()
    {
        var result = BatchXmlValidator.Validate(ValidXml());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void GeneratedXml_ContainsBatchRecipeAndIngredientQuantities()
    {
        var doc = BatchXmlGenerator.Generate(TestData.NewBatch(200m), Stamp);
        var root = doc.Root!;

        Assert.Equal("B-20261016-001", root.Element(Ns + "BatchId")!.Value);
        Assert.Equal("Standard Mix", root.Element(Ns + "Recipe")!.Attribute("name")!.Value);
        Assert.Equal("Created", root.Element(Ns + "Status")!.Value);
        Assert.StartsWith("2026-10-16T14:30:00", root.Element(Ns + "GeneratedAtUtc")!.Value);
        Assert.EndsWith("Z", root.Element(Ns + "GeneratedAtUtc")!.Value);

        var ingredients = root.Element(Ns + "Ingredients")!.Elements(Ns + "Ingredient").ToList();
        Assert.Equal(2, ingredients.Count);
        Assert.Equal("A", ingredients[0].Attribute("container")!.Value);
        Assert.Equal(120m, decimal.Parse(ingredients[0].Element(Ns + "QuantityKg")!.Value, CultureInfo.InvariantCulture));
        Assert.Equal(80m, decimal.Parse(ingredients[1].Element(Ns + "QuantityKg")!.Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Numbers_UseDotAsDecimalSeparator_EvenOnASpanishPc()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES"); // would write "120,5"
            var xml = ValidXml(200.5m);

            Assert.DoesNotContain("120,", xml);
            Assert.True(BatchXmlValidator.Validate(xml).IsValid);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // --- Invalid documents: each one breaks exactly one rule ---

    [Fact]
    public void MalformedXml_IsReportedNotThrown()
    {
        var result = BatchXmlValidator.Validate("<BatchOrder><unclosed></BatchOrder>");

        Assert.False(result.IsValid);
        Assert.Contains("not well-formed", result.Errors[0]);
    }

    [Fact]
    public void MissingBatchId_IsRejected()
    {
        var xml = ValidXml().Replace("<BatchId>B-20261016-001</BatchId>", "");

        Assert.False(BatchXmlValidator.Validate(xml).IsValid);
    }

    [Fact]
    public void BadBatchIdFormat_IsRejected()
    {
        var xml = ValidXml().Replace("B-20261016-001", "BATCH-1");

        Assert.False(BatchXmlValidator.Validate(xml).IsValid);
    }

    [Fact]
    public void NegativeQuantity_IsRejected()
    {
        var xml = ValidXml(200m).Replace("<PlannedQuantityKg>200</PlannedQuantityKg>", "<PlannedQuantityKg>-5</PlannedQuantityKg>");

        Assert.False(BatchXmlValidator.Validate(xml).IsValid);
    }

    [Fact]
    public void UnknownContainer_IsRejected()
    {
        var xml = ValidXml().Replace("container=\"A\"", "container=\"Z\"");

        Assert.False(BatchXmlValidator.Validate(xml).IsValid);
    }

    [Fact]
    public void UnknownStatus_IsRejected()
    {
        var xml = ValidXml().Replace("<Status>Created</Status>", "<Status>Exploded</Status>");

        Assert.False(BatchXmlValidator.Validate(xml).IsValid);
    }

    [Fact]
    public void WrongNamespace_IsRejected()
    {
        var xml = ValidXml().Replace(BatchXmlGenerator.Namespace, "urn:someone:else");

        Assert.False(BatchXmlValidator.Validate(xml).IsValid);
    }

    [Fact]
    public void NoIngredients_IsRejected()
    {
        var doc = BatchXmlGenerator.Generate(TestData.NewBatch(), Stamp);
        doc.Root!.Element(Ns + "Ingredients")!.RemoveNodes();

        Assert.False(BatchXmlValidator.Validate(doc).IsValid);
    }
}

using System.Xml;
using System.Xml.Linq;
using MiniMes.Core.Models;

namespace MiniMes.Xml;

public static class BatchXmlGenerator
{
    public const string Namespace = "urn:minimes:batch:v1";
    public const string SourceSystem = "MiniMES";

    /// <summary>Builds the XML document for a batch. The batch must have its Recipe (with ingredients) loaded.</summary>
    public static XDocument Generate(Batch batch, DateTime generatedAtUtc)
    {
        XNamespace ns = Namespace;

        var ingredients = batch.Recipe.Ingredients.Zip(
            batch.Recipe.CalculateQuantities(batch.PlannedQuantityKg),
            (line, quantity) => new XElement(ns + "Ingredient",
                new XAttribute("container", line.Container.ToString()),
                new XElement(ns + "Name", line.IngredientName),
                new XElement(ns + "Percentage", Num(line.Percentage)),
                new XElement(ns + "QuantityKg", Num(quantity.Kilograms))));

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "BatchOrder",
                new XAttribute("version", "1.0"),
                new XElement(ns + "BatchId", batch.BatchId),
                new XElement(ns + "GeneratedAtUtc", XmlConvert.ToString(generatedAtUtc, XmlDateTimeSerializationMode.Utc)),
                new XElement(ns + "Source", SourceSystem),
                new XElement(ns + "Recipe",
                    new XAttribute("id", batch.RecipeId),
                    new XAttribute("name", batch.Recipe.Name)),
                new XElement(ns + "PlannedQuantityKg", Num(batch.PlannedQuantityKg)),
                new XElement(ns + "Status", batch.Status.ToString()),
                new XElement(ns + "Ingredients", ingredients)));
    }

    /// <summary>Generates the XML as text, with the XML declaration included.</summary>
    public static string GenerateString(Batch batch, DateTime generatedAtUtc)
    {
        var document = Generate(batch, generatedAtUtc);
        return document.Declaration + Environment.NewLine + document.Root;
    }

    // XmlConvert always uses "." as the decimal separator, whatever the PC's language is.
    private static string Num(decimal value) => XmlConvert.ToString(value);
}

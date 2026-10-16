using System.Globalization;

namespace MiniMes.Core.Models;

/// <summary>A supply tank (Container A or B) holding one raw material.</summary>
public class Tank
{
    /// <summary>At or below this fraction of capacity the tank is considered low.</summary>
    public const decimal LowLevelFraction = 0.2m;

    private Tank() { } // for EF Core

    public Tank(Container container, string materialName, decimal capacityKg, decimal initialLevelKg)
    {
        if (string.IsNullOrWhiteSpace(materialName))
            throw new DomainException("Material name is required.");
        if (capacityKg <= 0)
            throw new DomainException("Tank capacity must be greater than 0.");
        if (initialLevelKg < 0 || initialLevelKg > capacityKg)
            throw new DomainException("Initial level must be between 0 and the tank capacity.");

        Container = container;
        MaterialName = materialName;
        CapacityKg = capacityKg;
        LevelKg = initialLevelKg;
    }

    /// <summary>The tank's identity: there is exactly one tank per container.</summary>
    public Container Container { get; private set; }
    public string MaterialName { get; private set; } = "";
    public decimal CapacityKg { get; private set; }
    public decimal LevelKg { get; private set; }

    public bool IsLow => LevelKg <= CapacityKg * LowLevelFraction;

    public bool CanSupply(decimal kg) => kg <= LevelKg;

    /// <summary>Takes material out of the tank (dosing a batch).</summary>
    public void Consume(decimal kg)
    {
        if (kg <= 0)
            throw new DomainException("Quantity to consume must be greater than 0.");
        if (!CanSupply(kg))
            throw new DomainException(
                $"Not enough {MaterialName} in TK-{Container}: {Fmt(kg)} kg needed, {Fmt(LevelKg)} kg available.");

        LevelKg -= kg;
    }

    /// <summary>Adds material to the tank, up to its capacity.</summary>
    public void Refill(decimal kg)
    {
        if (kg <= 0)
            throw new DomainException("Refill quantity must be greater than 0.");
        if (LevelKg + kg > CapacityKg)
            throw new DomainException(
                $"TK-{Container} can take at most {Fmt(CapacityKg - LevelKg)} kg more (capacity {Fmt(CapacityKg)} kg).");

        LevelKg += kg;
    }

    // Always "." as the decimal separator, whatever the PC's language is.
    private static string Fmt(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

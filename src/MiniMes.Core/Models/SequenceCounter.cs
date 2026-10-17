namespace MiniMes.Core.Models;

/// <summary>A named counter that only ever goes up, e.g. the last batch number issued. Never reset.</summary>
public class SequenceCounter
{
    public const string BatchName = "Batch";

    public string Name { get; private set; } = "";
    public long Value { get; private set; }
}

namespace MiniMes.Core;

/// <summary>Thrown when a business rule is violated (bad quantity, illegal status change...).</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

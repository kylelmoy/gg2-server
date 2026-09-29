namespace GG2.Protocol;

/// <summary>Thrown when a buffer does not hold what the protocol says it should.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}

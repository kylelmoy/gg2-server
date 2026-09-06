namespace Gg2.Server;

/// <summary>
/// The server stopped because too many ticks failed.
/// </summary>
/// <remarks>
/// <b>Its own type so the host can tell it from a startup failure</b> — the two
/// need different exit codes and different messages, and both would otherwise
/// arrive at <c>Main</c> as an anonymous exception with a stack trace. The tick
/// that failed is the <see cref="Exception.InnerException"/>; this one carries
/// only the count and the decision.
/// </remarks>
public sealed class TickFaultException(string message, Exception inner)
    : Exception(message, inner);

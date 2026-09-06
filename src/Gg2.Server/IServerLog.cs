namespace Gg2.Server;

/// <summary>How serious a log line is.</summary>
/// <remarks>
/// <b>Three levels, not five.</b> An operator running this unattended needs
/// exactly one question answered from the log: "is anything wrong?". More
/// levels than that push the decision back onto whoever is writing the line,
/// and the failure mode of a rich level set is that everything ends up at the
/// two the author reaches for anyway.
/// </remarks>
public enum LogLevel
{
    /// <summary>The server did something it was asked to do.</summary>
    Info,

    /// <summary>
    /// Something is wrong but the server is carrying on, and it is worth an
    /// operator's attention: a client refused, a limit hit, an outbound service
    /// unreachable.
    /// </summary>
    Warning,

    /// <summary>
    /// Something failed that was not supposed to be able to fail. Every one of
    /// these is a bug or an attack, and none is routine.
    /// </summary>
    Error,
}

/// <summary>Where the server reports what it did. Nothing here is on the wire.</summary>
/// <remarks>
/// <para>
/// <b><see cref="Info"/> is the only required member.</b> The other two have
/// default implementations that fold into it, so a sink written before levels
/// existed still compiles and still sees every line. A sink that can do better
/// — the console, which has a second stream to write to — overrides them.
/// </para>
/// <para>
/// ⚠️ <b>Anything a client chose goes through <see cref="LogText"/> first.</b>
/// A player name is up to twenty arbitrary bytes including <c>\n</c>, so
/// interpolating one straight into a line lets a player write log lines of
/// their own. <see cref="LogText.Safe"/> is the whole of the defence and it is
/// one call.
/// </para>
/// </remarks>
public interface IServerLog
{
    void Info(string message);

    /// <summary>Something is wrong; the server is carrying on.</summary>
    void Warn(string message) => Info($"[warn] {message}");

    /// <summary>Something failed that should not have been able to.</summary>
    /// <param name="error">
    /// The exception, when there was one. Rendered by the sink, so a console
    /// can print a stack trace where a 500-line ring buffer prints one line.
    /// </param>
    void Error(string message, Exception? error = null) =>
        Info($"[error] {message}{(error is null ? string.Empty : $" — {error.GetType().Name}: {error.Message}")}");
}

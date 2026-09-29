using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// A player name is twenty arbitrary bytes, and the log is the one place they
/// were being written out without an encoder.
/// </summary>
/// <remarks>
/// ⚠️ <b>The oracle is not "the name looks tidy".</b> It is that a name cannot
/// produce a line the server did not write. Every case below is a byte a client
/// can legally put in a <c>RESERVE_SLOT</c>: <c>GG2Text.Decode</c> is Latin-1,
/// so 0..255 all round-trip, and nothing on the join path rejects any of them.
/// </remarks>
public sealed class LogTextTests
{
    /// <summary>
    /// The whole point: a newline in a name cannot start a second log line.
    /// </summary>
    [Fact]
    public void ANewlineInANameCannotForgeALogLine()
    {
        var forged = LogText.Safe("bob\n2026-01-01 00:00:00 server shutting down");

        Assert.DoesNotContain('\n', forged);
        Assert.DoesNotContain('\r', forged);
        Assert.Contains("\\x0A", forged, StringComparison.Ordinal);
    }

    /// <summary>A carriage return, which on a terminal rewrites the line in place.</summary>
    [Fact]
    public void ACarriageReturnCannotOverwriteWhatWasAlreadyPrinted()
    {
        Assert.Equal("bob\\x0Dadmin", LogText.Safe("bob\radmin"));
    }

    /// <summary>
    /// An ESC cannot reach a terminal, so a name cannot colour itself, move the
    /// cursor or retitle the operator's window.
    /// </summary>
    [Fact]
    public void AnEscapeSequenceIsNeutralised()
    {
        Assert.Equal("\\x1B[31mred", LogText.Safe("\u001b[31mred"));
    }

    /// <summary>
    /// ⚠️ <b>The control case.</b> Every other assertion here is that something
    /// was changed, which is the shape that also passes if the sanitiser mangles
    /// everything. An ordinary name — including the accented Latin-1 range a
    /// real player's name uses — must come back untouched and by reference.
    /// </summary>
    [Fact]
    public void AnOrdinaryNameIsNotTouched()
    {
        const string name = "Zoë_Über-99";

        Assert.Equal(name, LogText.Safe(name));
        Assert.Same(name, LogText.Safe(name));
    }

    /// <summary>
    /// Escaped rather than stripped, so two players are never rendered as one.
    /// </summary>
    /// <remarks>
    /// A sanitiser that dropped control bytes would print <c>bob</c> for both
    /// <c>bob</c> and <c>b\x00ob</c> — a name collision the log itself would be
    /// creating, and the impersonation this is supposed to prevent.
    /// </remarks>
    [Fact]
    public void TwoNamesThatDifferOnlyInAnInvisibleByteStillRenderDifferently()
    {
        Assert.NotEqual(LogText.Safe("bob"), LogText.Safe("bo\0b"));
    }

    /// <summary>
    /// Bounded, so a long string cannot push the rest of the line off a screen.
    /// </summary>
    [Fact]
    public void ALongStringIsCutShortAndSaysSo()
    {
        var rendered = LogText.Safe(new string('a', 500));

        Assert.Equal(LogText.MaxLength + 1, rendered.Length);
        Assert.EndsWith("…", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingRendersAsSomethingRatherThanAsAGapInTheLine(string? value)
    {
        Assert.Equal(LogText.Empty, LogText.Safe(value));
    }
}

/// <summary>
/// The level plumbing: a sink that overrides nothing still sees every line, and
/// one that overrides both keeps its own rendering through a
/// <see cref="TeeLog"/>.
/// </summary>
public sealed class ServerLogLevelTests
{
    /// <summary>A sink written before levels existed. Only <c>Info</c>.</summary>
    private sealed class InfoOnlyLog : IServerLog
    {
        public List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add(message);
    }

    /// <summary>
    /// ⚠️ <b>The compatibility guarantee.</b> Three sinks in this repo and any
    /// number outside it implement <c>Info</c> and nothing else; a level they do
    /// not implement must still reach them, or re-levelling a line silently
    /// deletes it from that sink.
    /// </summary>
    [Fact]
    public void ASinkThatOnlyImplementsInfoStillSeesWarningsAndErrors()
    {
        IServerLog log = new InfoOnlyLog();

        log.Warn("a warning");
        log.Error("an error", new InvalidOperationException("why"));

        var lines = ((InfoOnlyLog)log).Lines;

        Assert.Equal(2, lines.Count);
        Assert.Contains("a warning", lines[0], StringComparison.Ordinal);
        Assert.Contains("an error", lines[1], StringComparison.Ordinal);

        // The exception is folded in rather than dropped: a sink that cannot
        // render a trace must still say what the failure was.
        Assert.Contains("InvalidOperationException", lines[1], StringComparison.Ordinal);
        Assert.Contains("why", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A tee forwards the level, not the folded text — otherwise turning the web
    /// panel on would cost the console its stderr routing and its stack traces.
    /// </summary>
    [Fact]
    public void TheTeeForwardsTheLevelRatherThanFlatteningIt()
    {
        var inner = new RecordingLog();
        var ring = new LogRing();
        IServerLog tee = new TeeLog(inner, ring);

        tee.Info("one");
        tee.Warn("two");
        tee.Error("three", new IOException("disk"));

        Assert.Equal(["two", "three"], inner.Warnings);
        Assert.Equal(["three"], inner.Errors);
        Assert.Single(inner.Exceptions);

        // And the panel's ring got all three, with the level visible in the text
        // — it has no level field of its own.
        var seen = ring.Since(-1, out _).Select(entry => entry.Text).ToList();

        Assert.Equal(3, seen.Count);
        Assert.Contains("[warn]", seen[1], StringComparison.Ordinal);
        Assert.Contains("[error]", seen[2], StringComparison.Ordinal);
        Assert.Contains("disk", seen[2], StringComparison.Ordinal);
    }
}

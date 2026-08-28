using Gg2.Parity;

namespace Gg2.Tests;

/// <summary>
/// ⚠️ <b>Reading the map back out of a <c>.part</c> file's name, which is the
/// only place it survives.</b>
/// </summary>
/// <remarks>
/// <b>A live server's newest recording exists only as a <c>.part</c> until its
/// segment closes</b> — a trace is a header in front of one stream and the
/// header needs the stream's length, so <c>MatchRecorder.Close</c> writes it at
/// the end. A clean stop finalises; a kill, a crash or a power cut does not.
/// <c>gg2-parity recover</c> puts the header on afterwards, and the map name is
/// the one thing it cannot read from the bytes.
/// <para>
/// ⚠️ <b>Getting it wrong is quiet.</b> A trace with the wrong map frames
/// against the wrong world rather than failing, so this parse is worth its own
/// test — and it is exercised during whatever emergency orphaned the file, which
/// is the worst moment to discover a bug in it.
/// </para>
/// <para>
/// Found needed by a two-human playtest on 2026-08-27, where a task kill left
/// 4,096 bytes of a real round unreadable. <c>docs/BOTAI.md</c> item 3's
/// deadline is what makes it matter: on a public server the segment in progress
/// is the round in progress.
/// </para>
/// </remarks>
public sealed class RecoverPartTests
{
    /// <summary>
    /// <c>MatchRecorder</c>'s own format:
    /// <c>&lt;date&gt;-&lt;time&gt;-&lt;map&gt;-&lt;index&gt;.trace.part</c>.
    /// </summary>
    [Theory]
    [InlineData("20260828-022743-koth_corinth-03.trace.part", "koth_corinth")]
    [InlineData("20260828-021457-gg_debug-01.trace.part", "gg_debug")]

    // Already finalised, or recovered once already.
    [InlineData("20260828-021457-ctf_2dfort-11.trace", "ctf_2dfort")]

    // ⚠️ The case the obvious parse gets wrong: a map name containing dashes.
    // Taken from between the SECOND dash and the LAST, because the timestamp
    // and the index never contain one and a map name may.
    [InlineData("20260828-022743-some-custom-map-07.trace.part", "some-custom-map")]
    public void TheMapComesOutOfTheFileName(string name, string map) =>
        Assert.Equal(map, Program.MapFromName(name));

    /// <summary>
    /// A name that is not the recorder's gets null, so the caller can ask for
    /// <c>--map</c> rather than inventing one.
    /// </summary>
    /// <remarks>
    /// ⚠️ Null rather than a guess, deliberately. A wrong map produces a trace
    /// that reads against the wrong world; refusing produces a message.
    /// </remarks>
    [Theory]
    [InlineData("recording.trace.part")]
    [InlineData("20260828-022743.trace.part")]
    [InlineData(".trace.part")]
    public void AnUnrecognisableNameIsRefusedRatherThanGuessed(string name) =>
        Assert.Null(Program.MapFromName(name));
}

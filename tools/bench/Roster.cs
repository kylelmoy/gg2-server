using GG2.Bots;
using GG2.Protocol;

namespace Bench;

/// <summary>
/// Prints the class roster a run is about to use, at the top of that run.
/// </summary>
/// <remarks>
/// ⛔ <b>This exists because the failure it guards has no tell in the result.</b>
/// Since 2026-09-06 a checkpoint carries the classes it was trained on, and every
/// mode that loads one populates its matches from that set rather than from a
/// fixed default. If a run ever populated a default instead, a checkpoint outside that family would drive nothing, every
/// bot would fall back to scripted, and the output would be the scripted arm
/// reported twice with one of them labelled the policy — a clean, plausible null.
/// <para>
/// ⚠️ <b>The roster is the only place that failure is visible</b>, and
/// <c>tools/bench</c> is not in <c>GG2.sln</c> and has no tests, so nothing in a
/// suite can hold it. Printing it unconditionally is what turns a check somebody
/// has to remember into one they cannot miss: the training lane's pre-flight step
/// for a first run against a new checkpoint family is to read this line against
/// the set they trained.
/// </para>
/// <para>
/// ⚠️ It says where the set CAME FROM, not just what it is. "the checkpoint's own"
/// against "the default (no checkpoint)" is the distinction that matters — the two
/// print identically for a file whose set happens to BE the default, and that
/// coincidence hides a wrong set.
/// </para>
/// </remarks>
internal static class Roster
{
    /// <summary>The line every mode prints before it measures anything.</summary>
    public static void Announce(IReadOnlyList<PlayerClass> classes, bool fromCheckpoint) =>
        Console.WriteLine(
            $"classes: {string.Join(',', classes)} "
            + $"({(fromCheckpoint ? "the checkpoint's own" : "the default, no checkpoint")})");
}

namespace Gg2.Tests;

/// <summary>
/// The reference's bot layer, against the coverage `docs/PLAN.md` claims of it.
/// </summary>
/// <remarks>
/// Phase 7 closed with a claim about a <em>negative</em> — "these ten scripts are
/// not ported, and each is conservative when absent" — and a negative nobody
/// checks is the kind of documentation that decays quietly into a lie.
/// <para>
/// ⚠️ <b>What this catches is the REFERENCE moving, not this port catching up.</b>
/// If the fork gains a bot script, renames one, or drops one, the plan's coverage
/// list is out of date and this goes red. If somebody here ports
/// <c>botFindAlly</c> and forgets the plan, nothing notices — the signal for that
/// is the helper's own "deliberately not ported" comment going away, which is a
/// human's job to spot in review. Saying so is the point: a test that implied
/// otherwise would be worse than none.
/// </para>
/// <para>
/// CLAUDE.md: "Facts in <c>docs/</c> were extracted on 2026-08-24 from GG2 v2.9.2.
/// If the fork moves, re-check the protocol docs against it." Nothing noticed a
/// move in the bot layer until this.
/// </para>
/// </remarks>
public sealed class BotLayerCoverageTests
{
    /// <summary>
    /// The three scripts phase 7 left unported, each conservative when absent.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Kept in the same order and the same words as <c>docs/PLAN.md</c>'s
    /// list</b>, so the two can be diffed by eye. Porting one means deleting it
    /// from both.
    /// </remarks>
    private static readonly string[] Unported =
    [
        "botStepSafe",            // a pyro holds where the engage caught it rather than closing
        "botStabWindow",          // a spy never stabs
        "botMinesArmed",          // a demoman never detonates
    ];

    private static string[] Scripts(string folder)
    {
        ReferenceTree.RequireAvailable();

        var path = Path.Combine(ReferenceTree.Root, "Source", "gg2", "Scripts", folder);

        return Directory.GetFiles(path, "*.gml")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The reference's bot layer is the 67 scripts phase 7 was measured against.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Names rather than a line count.</b> A line count fails on a whitespace
    /// change, which is noise; a name changes only when a script is added, removed
    /// or renamed, which is exactly when the plan's coverage claim needs
    /// revisiting. The 7,202-line figure the plan quotes is a measurement with a
    /// date on it and is deliberately not asserted.
    /// </remarks>
    [Fact]
    public void TheReferencesBotLayerIsWhatPhaseSevenWasMeasuredAgainst()
    {
        var bots = Scripts("Bots");
        var nav = Scripts("BotNav");

        Assert.Equal(52, bots.Length);
        Assert.Equal(15, nav.Length);

        // A handful named explicitly, so the failure message points at something
        // rather than at a count: the four biggest, which are most of the layer.
        foreach (var script in new[] { "botPathKeys", "botObjectiveUpdate", "botCombatUpdate", "botInputUpdate" })
            Assert.Contains(script, bots);

        Assert.Contains("navFindPath", nav);
    }

    /// <summary>
    /// Every script the plan calls unported still exists to be ported.
    /// </summary>
    /// <remarks>
    /// ⚠️ The failure this catches is the plan naming a script the reference no
    /// longer has — which reads to the next session as work still owed, and is
    /// work that has evaporated.
    /// </remarks>
    [Fact]
    public void EveryScriptThePlanCallsUnportedStillExists()
    {
        var bots = Scripts("Bots");

        foreach (var script in Unported)
            Assert.Contains(script, bots);

        // ⚠️ And the list is exactly three — ten before the rocket jump, nine before
        // botFindAlly, eight before the projectile reflexes took three at once, five
        // before botSuppressSpot, four before botArcClear. A list that grows without
        // the plan growing with it is the same decay from the other direction.
        Assert.Equal(3, Unported.Length);
        Assert.Equal(3, Unported.Distinct(StringComparer.Ordinal).Count());
    }
}

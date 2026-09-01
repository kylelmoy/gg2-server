using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// The scripted bot with a <see cref="RolloutSearch"/> micro-controller behind
/// the same handover gate <see cref="PolicyBot"/> uses.
/// </summary>
/// <remarks>
/// `gg2-rl`'s arm 3. The macro layer owns routing and objectives; inside the
/// engagement window the search takes the wheel instead of a trained policy.
/// <para>
/// ⛔ <b>ONE thing differs from <see cref="PolicyBot"/>: which controller the gate
/// hands to.</b> Same <see cref="Handover"/>, same range, same rule, same
/// intel carve-out, same "the scripted layer thinks every tick". That is what
/// makes an arm-2/arm-3 comparison a comparison of controllers rather than of two
/// bots that differ in ways nobody enumerated.
/// </para>
/// <para>
/// ⚠️ <b>The search OWNS the decision cadence and is polled every tick while it
/// drives.</b> Not a preference — the alternative is unavailable.
/// <c>ActionDecoder</c> emits the jump bit only on the first tick of a decision,
/// so the held ticks are a <em>different decode of the same action</em> rather
/// than a repeat of one <c>PlayerInput</c>. A gate polling the search only on its
/// own boundaries would either starve it of the held ticks or re-implement the
/// edge logic here, which is CLAUDE.md's "GG2 jumps on the PRESSED edge" scar a
/// second time. So this class asks <c>RolloutSearch.AtDecisionBoundary</c> rather
/// than counting ticks itself.
/// </para>
/// <para>
/// ⛔ <b><see cref="RestrictToTrainedClasses"/> defaults to TRUE and that is a
/// measurement decision, not a limitation.</b> The search drives any class; the
/// policy was trained on some. Letting the search engage on classes arm 2 cannot
/// would move two things between the arms at once, and the extra classes are a
/// separate — and favourable — axis that this experiment is not measuring. Turn it
/// off to measure that instead, and say which you did.
/// </para>
/// <para>
/// ⚠️ <b>Cost is not the mean.</b> A search decision is ~7.6 µs·10³ at twelve live
/// characters — 23% of a 33.3 ms tick — and handovers are correlated, because
/// several bots have an enemy in range during the same firefight. <b>The
/// constraint is the tick tail, not the duty cycle</b>; `tools/bench`, trap 6.
/// </para>
/// </remarks>
public sealed class SearchBot : IInputSource, ICommandSource
{
    private readonly RolloutSearch _search;
    private readonly ScriptedBot _scripted;

    private int _slot = -1;
    private bool _searchHadControl;

    public SearchBot(RolloutSearch search, ScriptedBot scripted)
    {
        _search = search;
        _scripted = scripted;
    }

    /// <summary>The engagement window. Phase 4's measured 150px by default.</summary>
    public double Range { get; init; } = PolicyBot.EngagementRange;

    /// <summary>The handover rule. <c>MacroTask</c> is the shipped conjunction.</summary>
    public HandoverRule Rule { get; init; } = HandoverRule.MacroTask;

    /// <summary>The intel carrier stays the macro layer's, as in arm 2.</summary>
    public bool ScriptedCarriesIntel { get; init; } = true;

    /// <summary>
    /// Whether to engage only on the classes the policy was trained for.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>True keeps the arms comparable; false measures a different thing.</b>
    /// See this type's remarks.
    /// </remarks>
    public bool RestrictToTrainedClasses { get; init; } = true;

    /// <summary>
    /// Whether the search actually SEARCHED on the tick just polled, as opposed to
    /// replaying a decision it already took.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the expensive event, and it is not the same as
    /// <see cref="SearchDrove"/>.</b> The search drives every tick of a decision and
    /// searches on one of them, so counting ticks it drove over-reports the cost by
    /// the decision length. A tick-budget question needs the count of DECISIONS that
    /// coincided, which is what this reports.
    /// </remarks>
    public bool SearchedThisPoll { get; private set; }

    /// <summary>Whether the search drove the tick just polled.</summary>
    public bool SearchDrove { get; private set; }

    /// <summary>Ticks the search has held the wheel.</summary>
    public long SearchTicks { get; private set; }

    /// <summary>
    /// Times the scripted layer has been POLLED, which is every tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not the same as <see cref="ScriptedTicks"/>, and the difference is the
    /// point.</b> That counts ticks it DROVE; this counts ticks it THOUGHT. They
    /// differ by exactly the ticks the search held the wheel, and the invariant
    /// <c>ScriptedPolls == SearchTicks + ScriptedTicks</c> is what says the macro
    /// layer never stopped planning during a fight — arm 2’s measured bug, where
    /// polling it only on the ticks it drove cost 20 captures a hundred matches down
    /// to 7. ⛔ <b>Without this counter that property is untestable at unit level</b>,
    /// and a test asserting it from WantsToEngage alone passes on the broken version.
    /// </remarks>
    public long ScriptedPolls { get; private set; }

    /// <summary>Ticks the scripted layer has held it.</summary>
    public long ScriptedTicks { get; private set; }

    public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands) =>
        _scripted.PollCommands(in world, slot, tick, ref commands);

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        if (slot != _slot)
        {
            _slot = slot;
            _searchHadControl = false;
        }

        ref readonly var self = ref world.Character(slot);

        // ⛔ THE SCRIPTED LAYER THINKS EVERY TICK, WHETHER OR NOT IT DRIVES.
        // Identical to PolicyBot and for the identical measured reason: polling it
        // only on the ticks it drove left the macro layer doing no objective
        // planning during fights — no re-target, no re-path, no spot ban — and
        // captures fell 20 to 7 over a hundred matches.
        var scripted = PollScripted(in world, slot, tick);

        if (!world.IsAlive(slot)
            || (RestrictToTrainedClasses && !PolicyBot.IsTrained(self.Class))
            || (ScriptedCarriesIntel && self.CarryingIntel))
        {
            return Scripted(scripted);
        }

        // ⚠️ Control changes hands only on the SEARCH's decision boundary. Two
        // controllers with independent cadences flap at the threshold, and each
        // holds state the other does not maintain.
        if (!_search.AtDecisionBoundary(slot))
        {
            return _searchHadControl
                ? SearchInput(in world, slot, tick)
                : Scripted(scripted);
        }

        if (!Handover.ShouldEngage(in world, slot, Range, Rule, _scripted.WantsToEngage))
            return Scripted(scripted);

        // ⛔ Resync the search's absolute aim on every handover IN. The scripted
        // layer writes the character's bearing directly, so a decoder carrying the
        // bearing from the last engagement would turn from somewhere nobody is
        // pointing — silently, and in exactly the decisions a handover exists to
        // hand it. RolloutSearch.ResyncAim's own remarks carry the full reason.
        if (!_searchHadControl)
            _search.ResyncAim(in world, slot);

        _searchHadControl = true;

        return SearchInput(in world, slot, tick);
    }

    /// <summary>Polls the macro layer and counts it, in one place.</summary>
    /// <remarks>
    /// ⚠️ <b>One method so the counter cannot lie about the call.</b> The first
    /// version incremented <see cref="ScriptedPolls"/> on the line after the poll,
    /// and a mutation that skipped the poll while leaving the increment produced a
    /// counter reporting work that never happened — which the test then believed.
    /// A counter separable from the thing it counts is the same defect as a
    /// qualifier separable from its claim.
    /// </remarks>
    private PlayerInput PollScripted(in WorldView world, int slot, int tick)
    {
        ScriptedPolls++;

        return _scripted.Poll(in world, slot, tick);
    }

    private PlayerInput SearchInput(in WorldView world, int slot, int tick)
    {
        SearchDrove = true;
        SearchTicks++;

        var input = _search.Poll(in world, slot, tick);

        // ⚠️ Read AFTER the poll: LastDecisionTicksSimulated is zeroed at the top of
        // Poll and set only when it really searched.
        SearchedThisPoll = _search.LastDecisionTicksSimulated > 0;

        return input;
    }

    private PlayerInput Scripted(PlayerInput scripted)
    {
        _searchHadControl = false;
        SearchDrove = false;
        SearchedThisPoll = false;
        ScriptedTicks++;

        return scripted;
    }
}

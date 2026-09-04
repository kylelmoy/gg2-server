using Gg2.Bots;
using Gg2.Protocol;

namespace Gg2.Env;

/// <summary>How a duel is set up.</summary>
public readonly record struct DuelSpec
{
    public required string Map { get; init; }

    public required PlayerClass RedClass { get; init; }

    public required PlayerClass BlueClass { get; init; }

    public required double RedX { get; init; }

    public required double RedY { get; init; }

    public required double BlueX { get; init; }

    public required double BlueY { get; init; }

    /// <summary>
    /// Decision steps before the episode is cut off.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Counted in DECISIONS, not ticks.</b> A cap in ticks would silently
    /// change length when the action repeat changed, and the trainer's episode
    /// budget is in decisions.
    /// </remarks>
    public int DecisionCap { get; init; }

    /// <summary>Ticks each action is held for. 1 means every tick is a decision.</summary>
    public int ActionRepeat { get; init; }

    /// <summary>Who drives red. <see cref="EnvDriver.Client"/> by default.</summary>
    public EnvDriver RedDriver { get; init; }

    /// <summary>Who drives blue. <see cref="EnvDriver.Client"/> by default.</summary>
    public EnvDriver BlueDriver { get; init; }

    /// <summary>
    /// The skill tier for a <see cref="EnvDriver.Scripted"/> side, 0-5.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Zero is a real tier, not "unset".</b> <c>BotSkill.ForTier(0)</c> is
    /// the most handicapped bot rather than the default one, so a caller that
    /// leaves this alone gets the weakest scripted opponent — which is the safe
    /// direction to be wrong in, and is stated because the opposite reading costs
    /// an experiment.
    /// </remarks>
    public int RedTier { get; init; }

    /// <summary>The skill tier for a <see cref="EnvDriver.Scripted"/> blue side.</summary>
    public int BlueTier { get; init; }

    /// <summary>
    /// Which opponent model a <see cref="EnvDriver.Rollout"/> red side searches
    /// against. <see cref="RolloutOpponent.HoldLastInput"/> by default.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The number this exists for is a difference, not a level.</b>
    /// <c>gg2-rl</c>'s phase 1 wants the search baseline reported under
    /// <see cref="RolloutOpponent.HoldLastInput"/> <em>and</em> under
    /// <see cref="RolloutOpponent.Idle"/>: if the win rate barely moves between
    /// them the search is not really using its opponent model and the horizon or
    /// the scoring is the binding constraint; if it moves a lot, that is the
    /// argument for paying for self-play rollouts. It is wanted before a policy
    /// is distilled from the search, because the policy inherits whatever the
    /// search assumed.
    /// <para>
    /// ⚠️ <b>A separate field rather than an overload of <see cref="RedTier"/>.</b>
    /// The two are never both meaningful — a side is Scripted or it is Rollout —
    /// so sharing one integer is tempting. It is refused because both are small
    /// non-negative values with no sentinel between them, so a marshalling slip
    /// would silently ask for tier 1 instead of HoldLastInput and the experiment
    /// would report a matchup that never happened. <see cref="Sampled"/> is
    /// overloaded onto a coordinate precisely because NaN <em>has</em> no legal
    /// collision; this has one.
    /// </para>
    /// </remarks>
    public RolloutOpponent RedRolloutOpponent { get; init; }

    /// <summary>The opponent model a <see cref="EnvDriver.Rollout"/> blue side searches against.</summary>
    public RolloutOpponent BlueRolloutOpponent { get; init; }

    /// <summary>
    /// Ticks a <see cref="EnvDriver.Rollout"/> red side plays each candidate
    /// forward. <b>0 means the search's own default</b>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The number this exists for is the BLIND FRACTION, not the win
    /// rate.</b> <c>RolloutSearch</c> scores every candidate at exactly 0 when
    /// nothing can fire inside the horizon, and the rifle's 40-tick refire is
    /// double the 20-tick default. <c>gg2-rl</c> measured what that does to a
    /// distillation corpus with <see cref="StepResult.BestScore"/>: <b>70.9% of
    /// rows blind overall</b>, by class scout 32.7%, heavy 46.8%, soldier 80.5%,
    /// <b>sniper 92.8%</b>. A sniper corpus at horizon 20 is not noisy, it is
    /// noise — one row in fourteen carries a decision.
    /// <para>
    /// ⚠️ <b>Win rate cannot see this.</b> A search that is blind most of the time
    /// still wins 74%, so the curve phase 2 needs is one no win-rate measurement
    /// would ever have prompted. That is why the knob is here and not a tuning
    /// change: it makes the sweep possible without deciding its outcome.
    /// </para>
    /// <para>
    /// ⚠️ <b>The DEFAULT IS DELIBERATELY UNCHANGED.</b> A horizon is a whole-bot
    /// knob — it triples the per-decision cost at 45 ticks, and phase 1's baseline
    /// is quoted against 20 — so which value ships is a decision that should
    /// follow the curves rather than precede them. <c>gg2-rl</c> asked for the
    /// knob and asked explicitly that the default not move with it.
    /// </para>
    /// <para>
    /// ⛔ <b>THE SWEEP RAN AND THE ANSWER IS "SHORTER", so stop reading the
    /// paragraphs above as an open question.</b> <c>gg2-rl</c>'s
    /// <c>docs/PLAN.md</c>, 2026-08-29: a longer horizon makes the search
    /// <b>monotonically worse in every class</b> — sniper 68.4% → 22.8%, soldier
    /// 93.8% → 63.7% from the default out to 90. The blind fraction falls exactly
    /// as intended and the win rate falls with it, anti-correlated everywhere. So
    /// <b>the blind fraction was the wrong thing to minimise</b>, and this
    /// remark's own framing — "the number this exists for is the BLIND FRACTION,
    /// not the win rate" — is the half that did not survive the measurement it
    /// asked for.
    /// </para>
    /// <para>
    /// ⚠️ <b>The diagnosis, which is why the knob still earns its place:</b>
    /// lookahead and commitment are entangled by the SCORING. A candidate scores
    /// on damage landing inside the window it is acting in, so a longer window
    /// scores a longer commitment to one action, and committing longer is worse
    /// however far you can see. <see cref="RolloutOptions.CommitTicks"/> was built
    /// to separate them and did not: a neutral continuation after the commit
    /// leaves 0.965 of a sniper's rows blind, worse than horizon 20, because a
    /// character that stops acting stops generating damage to score.
    /// <b>A continuation that keeps ACTING is the untried experiment</b> — see
    /// <c>docs/ENV.md</c>, "the policy as the rollout continuation".
    /// </para>
    /// <para>
    /// ⚠️ <b>Only the horizon is exposed, not the rest of <c>RolloutOptions</c>.</b>
    /// The aim ladder and the candidate set are the search's own tuning, and
    /// putting them on the wire would make "the search baseline" a different thing
    /// per batch — the same argument that limited
    /// <see cref="RedRolloutOpponent"/> to the opponent model.
    /// </para>
    /// </remarks>
    public int RedHorizonTicks { get; init; }

    /// <summary>The horizon for a <see cref="EnvDriver.Rollout"/> blue side. 0 is the default.</summary>
    public int BlueHorizonTicks { get; init; }

    /// <summary>
    /// Run a <c>RolloutSearch</c> alongside a <see cref="EnvDriver.Client"/> red
    /// side, grading the states the client reaches without driving them.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>DAgger's aggregation step, and the reason it exists is measured.</b>
    /// A policy cloned from search play wins 19.4% where the search wins 84.2%,
    /// and three levers have each bought single digits against that gap — more
    /// data (+3 exact match for 2.8x the rows), better labels (+4 to +5), a better
    /// expert (+6, and the target rose as fast). What is left untested is
    /// <b>which states the labels are for</b>: cloning fails by compounding error,
    /// one wrong action reaching a state the expert never visited.
    /// <para>
    /// So: <b>the policy acts, the search grades.</b> The next training round sees
    /// the states the policy actually reaches rather than the ones the search
    /// reaches.
    /// </para>
    /// <para>
    /// ⚠️ <b>The cheap approximation does NOT substitute, and was measured before
    /// this was built.</b> Collecting with the search driving while a policy holds
    /// the other slot gives 89.9% blind against 87.8% and similar usable rows —
    /// but those are states the <em>search</em> reaches while facing a policy, not
    /// states the policy reaches while acting.
    /// </para>
    /// <para>
    /// ⚠️ <b>Expensive by construction and never a default</b>: the search cost is
    /// paid on a slot that is not using it, so a labelling batch costs what a
    /// <see cref="EnvDriver.Rollout"/> batch costs. Only meaningful with a
    /// <see cref="EnvDriver.Client"/> driver — a <c>Rollout</c> side already
    /// reports its own scores — and refused otherwise rather than ignored.
    /// </para>
    /// <para>
    /// ⚠️ <b>It grades the state the reply's observation describes.</b> The shadow
    /// is polled after the action repeat and before the observation is written, so
    /// the label and the observation are of the same instant. That is the natural
    /// ordering here rather than an arranged one.
    /// </para>
    /// </remarks>
    public bool RedShadowSearch { get; init; }

    /// <summary>A shadow search grading a client-driven blue side.</summary>
    public bool BlueShadowSearch { get; init; }

    /// <summary>
    /// How many ticks a candidate's action is HELD before the rollout continues
    /// under a continuation, for a red side that searches. 0 is the whole horizon.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Per side, and that is the half of this that the criterion needs.</b>
    /// It lets the new search face the old one head-to-head in one world — null
    /// exactly 50%, no ceiling, and a win share with draws in the denominator.
    /// The alternative is each arm against scripted tier 5, where the search
    /// already wins ~82.5%: a three-point claim fired into seventeen points of
    /// headroom hard against a ceiling.
    /// <para>
    /// ⚠️ <b>Refused unless it is a multiple of the action repeat</b>, in
    /// <c>RolloutOptions.WithDefaults</c> — a continuation re-decides on the
    /// decision cadence counting from the end of the commitment, so a partial
    /// decision at the seam puts every later one out of phase with the cadence the
    /// policy was trained at. ⚠️ The guard does <b>not</b> fire when the
    /// commitment covers the whole horizon, because then no continuation runs at
    /// all and there is nothing to be out of phase.
    /// </para>
    /// </remarks>
    public int RedCommitTicks { get; init; }

    /// <summary>The commitment length for a blue side that searches. 0 is the whole horizon.</summary>
    public int BlueCommitTicks { get; init; }

    /// <summary>
    /// The policy driving red's rollout continuation, or null for the neutral one.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The RESOLVED policy, not the wire's handle.</b> <c>MakeBatch</c>
    /// carries an i32 handle and <c>EnvServer</c> looks it up before building this
    /// spec, refusing an unknown one there. Carrying both would be two sources of
    /// truth for one fact, which is the shape of several bugs this repo has
    /// already paid for; the handle is a wire detail and stops at the socket.
    /// <para>
    /// ⚠️ <b>The continuation runs at <c>DecisionTicks</c> cadence, not every
    /// tick, and that is a cost constraint rather than a preference.</b> Measured
    /// 2026-09-01: one policy decision is 6.58 µs (inference 3.67, observation
    /// extraction 2.92), so at k=20 and horizon 20 a per-tick continuation costs
    /// 3.48 ms against an unpruned baseline of 2.45 ms — <em>more</em> than the
    /// search it replaces — while at cadence 4 it costs 1.47 ms. It also matches
    /// the <c>action_repeat</c> the policy was trained at. Both arguments land on
    /// the same number, which is luck worth naming as luck.
    /// </para>
    /// </remarks>
    public PolicyNetwork? RedContinuationPolicy { get; init; }

    /// <summary>The policy driving blue's rollout continuation, or null.</summary>
    public PolicyNetwork? BlueContinuationPolicy { get; init; }

    /// <summary>Whether either side is graded by a search it is not driven by.</summary>
    public bool HasShadow => RedShadowSearch || BlueShadowSearch;

    /// <summary>
    /// The closest two sampled placements may be, in pixels. 0 for no floor.
    /// </summary>
    /// <remarks>See <see cref="MaxSeparation"/>, which is the switch.</remarks>
    public double MinSeparation { get; init; }

    /// <summary>
    /// The furthest two sampled placements may be, in pixels — and the switch
    /// that moves sampling off the spawn points and onto the nav graph.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Zero means off, and off means spawn points.</b> Sampling from spawn
    /// points is what a real respawn does and is the right default; it is also
    /// unusable for a duel, and that was measured rather than argued.
    /// <c>gg2-rl</c>, <c>RolloutSearch</c> against a stationary <c>Idle</c>, 90
    /// decisions:
    /// <list type="bullet">
    /// <item><c>gg_debug</c>, explicit spawns: 4 distinct actions, 16-0-0.</item>
    /// <item><c>arena_lumberyard</c> and <c>ctf_2dfort</c>, sampled: <b>2</b>
    /// distinct actions, 0 damage, tick cap.</item>
    /// </list>
    /// No candidate can produce damage inside a 20-tick horizon at that range,
    /// so every candidate scores zero, the tie goes to the first, and the bot
    /// stands still. <c>RolloutSearch</c> has no objective layer and no follower
    /// — nothing in it walks toward anybody — so it is not a search bug, it is
    /// out of range.
    /// <para>
    /// And the other half: <c>gg_debug</c> has ONE spawn point per team, so
    /// sampling there draws the same duel every time. Sixteen duels reported
    /// 100.0% [70.7%, 100.0%] p=3.1e-05 off a sample size of one — they genuinely
    /// happened, and they were one duel counted sixteen times. **A map that gives
    /// engagement gave no variety and a map that gave variety gave no
    /// engagement**, so no map satisfied phase 1's exit criterion.
    /// </para>
    /// <para>
    /// ⚠️ <b>With it set, placements are drawn from the NAV GRAPH's walkable
    /// nodes rather than from spawn points</b>, filtered per team by
    /// <see cref="NavGraph.GatePassable"/> and paired inside the band. Gate
    /// filtering is not decoration: <c>koth_harvest</c>, <c>ctf_2dfort</c>,
    /// <c>koth_valley</c> and <c>dkoth_atalia</c> engaged 0 of 8 sampled duels
    /// because the pair started behind their own team gates and could not route
    /// to each other. A node the team may not pass is a node they may not be
    /// placed on.
    /// </para>
    /// <para>
    /// ⚠️ <b>It needs a nav graph and is refused without one</b>, the same way
    /// <see cref="EnvDriver.Scripted"/> is, and for the same reason: quietly
    /// falling back to spawn points would answer the request with the exact
    /// behaviour it was asked to replace.
    /// </para>
    /// </remarks>
    public double MaxSeparation { get; init; }

    /// <summary>Whether placements are drawn from the nav graph inside a band.</summary>
    public bool SeparationBanded => MaxSeparation > 0;

    /// <summary>Whether either side is driven from inside this process.</summary>
    public bool HasBotDriver => RedDriver != EnvDriver.Client || BlueDriver != EnvDriver.Client;

    /// <summary>
    /// Put this in a coordinate to mean <b>"pick a spawn point off the map"</b>
    /// instead of naming one.
    /// </summary>
    /// <remarks>
    /// <c>docs/ENV.md</c>'s <c>MakeBatch</c> row has said "explicit positions or
    /// sampled" since the first draft and only the first half existed until
    /// 2026-08-28. Without the second, a duel on any map required the caller to
    /// know that map's coordinates — a second source of truth about the maps,
    /// which is the thing <c>SIM_UUID</c> exists to prevent, and one that keeps
    /// working right up until a map changes.
    /// <para>
    /// ⚠️ <b>A sentinel rather than a new field, deliberately.</b> It rides in
    /// the four doubles <c>MakeBatch</c> already carries, so the frame layout is
    /// unchanged and <see cref="EnvProtocol.Version"/> does <b>not</b> move — a
    /// client built against v1 goes on working, and one that wants sampling
    /// sends a NaN. NaN is the honest choice for it because no map coordinate
    /// can ever be one: there is no legal value it collides with.
    /// </para>
    /// <para>
    /// ⚠️ <b>And a NaN that is not understood must not flow onward</b>, which is
    /// the standing risk with this sentinel — an unnoticed NaN coordinate would
    /// place a character at NaN and every comparison about it would read false.
    /// <see cref="Validate"/> is what stops that: half a sampled pair is
    /// refused, loudly, rather than half-applied.
    /// </para>
    /// </remarks>
    public static double Sampled => double.NaN;

    /// <summary>Whether a coordinate is the <see cref="Sampled"/> sentinel.</summary>
    public static bool IsSampled(double coordinate) => double.IsNaN(coordinate);

    /// <summary>Whether red's position is to be drawn from the map.</summary>
    public bool RedIsSampled => IsSampled(RedX) && IsSampled(RedY);

    /// <summary>Whether blue's position is to be drawn from the map.</summary>
    public bool BlueIsSampled => IsSampled(BlueX) && IsSampled(BlueY);

    /// <summary>
    /// Refuses a spec whose coordinates are half sampled.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The whole reason a NaN sentinel is safe.</b> One NaN of a pair is
    /// meaningless — a caller either knows where it wants a character or does
    /// not — and it is the shape a marshalling bug produces. Refusing it here
    /// means the sentinel can never reach a placement half-understood, which is
    /// the failure mode that would otherwise put a character at NaN and make
    /// every comparison about it quietly false.
    /// </remarks>
    public void Validate()
    {
        if (IsSampled(RedX) != IsSampled(RedY))
            throw new ArgumentException("red's x and y must both be sampled or both be explicit", nameof(RedX));

        if (IsSampled(BlueX) != IsSampled(BlueY))
            throw new ArgumentException("blue's x and y must both be sampled or both be explicit", nameof(BlueX));

        // ⚠️ Refused rather than clamped. A tier outside the table is a trainer
        // bug, and silently giving it tier 5 would make an experiment report a
        // matchup that never happened.
        if (RedDriver == EnvDriver.Scripted && (uint)RedTier > 5)
            throw new ArgumentOutOfRangeException(nameof(RedTier), RedTier, "a skill tier is 0 to 5");

        if (BlueDriver == EnvDriver.Scripted && (uint)BlueTier > 5)
            throw new ArgumentOutOfRangeException(nameof(BlueTier), BlueTier, "a skill tier is 0 to 5");

        if (!Enum.IsDefined(RedDriver))
            throw new ArgumentOutOfRangeException(nameof(RedDriver), RedDriver, "no such driver");

        if (!Enum.IsDefined(BlueDriver))
            throw new ArgumentOutOfRangeException(nameof(BlueDriver), BlueDriver, "no such driver");

        if (!Enum.IsDefined(RedRolloutOpponent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(RedRolloutOpponent), RedRolloutOpponent, "no such rollout opponent");
        }

        if (!Enum.IsDefined(BlueRolloutOpponent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlueRolloutOpponent), BlueRolloutOpponent, "no such rollout opponent");
        }

        // ⚠️ Refused rather than clamped, like the skill tier: a negative or absurd
        // horizon is a trainer bug, and silently substituting the default would
        // make a sweep report a curve it never measured.
        if (RedCommitTicks < 0 || BlueCommitTicks < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RedCommitTicks),
                RedCommitTicks < 0 ? RedCommitTicks : BlueCommitTicks,
                "a commitment is not negative; 0 means the whole horizon");
        }

        if (RedHorizonTicks < 0 || BlueHorizonTicks < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RedHorizonTicks), RedHorizonTicks, "a horizon is not negative; 0 means the default");
        }

        if (MinSeparation < 0 || MaxSeparation < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinSeparation), MinSeparation, "a separation band is not negative");
        }

        if (SeparationBanded && MinSeparation > MaxSeparation)
        {
            throw new ArgumentException(
                $"the separation band is empty: min {MinSeparation} is past max {MaxSeparation}",
                nameof(MinSeparation));
        }

        // ⚠️ Refused rather than ignored, and the argument is the half-NaN one
        // above. A band between one sampled placement and one the caller named is
        // half-specified — there is nothing to draw for the named side — and
        // ignoring it silently would give back exactly the spawn-point sampling
        // the band exists to replace, which is the failure the band was asked for.
        if (SeparationBanded && !(RedIsSampled && BlueIsSampled))
        {
            throw new ArgumentException(
                "a separation band needs BOTH placements sampled; it has nothing to draw for an explicit one",
                nameof(MaxSeparation));
        }

        // ⚠️ Refused rather than ignored: a shadow on a Rollout side would run a
        // second search whose answer the first already reports, at double the
        // cost, and silently. A caller asking for that has a bug.
        if (RedShadowSearch && RedDriver != EnvDriver.Client)
        {
            throw new ArgumentException(
                $"a shadow search grades a Client side; red is {RedDriver}", nameof(RedShadowSearch));
        }

        if (BlueShadowSearch && BlueDriver != EnvDriver.Client)
        {
            throw new ArgumentException(
                $"a shadow search grades a Client side; blue is {BlueDriver}", nameof(BlueShadowSearch));
        }

        if (!SeparationBanded && MinSeparation > 0)
        {
            throw new ArgumentException(
                "a minimum separation without a maximum does not switch banded sampling on; set MaxSeparation",
                nameof(MinSeparation));
        }
    }
}

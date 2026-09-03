using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The round: the clock running down, and who has won when it stops.
/// </summary>
/// <remarks>
/// <c>Objects/Overlays/Gamemodes/CTFHUD.events/{Create, Step}.xml</c>. The
/// reference puts this on the <em>HUD</em> — an overlay object — which is only
/// a naming accident of a game where the host is also a player: the host's
/// <c>CTFHUD</c> is what decides the match, and a dedicated server creates one
/// too. What lives here is the part that decides; the drawing does not exist.
/// <para>
/// Each gamemode has its own HUD and its own end condition. Only
/// <see cref="GameMode.CaptureTheFlag"/> and <see cref="GameMode.Invasion"/>
/// are transcribed, because those are the modes the port simulates an objective
/// for; the rest arrive with theirs.
/// </para>
/// </remarks>
public static class Rounds
{
    /// <summary>
    /// How long a win is shown before the map actually changes:
    /// <c>impendingMapChange = 300</c>, ten seconds.
    /// </summary>
    /// <remarks>
    /// Server bookkeeping rather than simulation — only
    /// <c>GameServerBeginStep.gml</c> counts it — but the constant belongs
    /// beside the rule it implements.
    /// </remarks>
    public const int MapChangeDelay = 300;

    /// <summary>
    /// <c>KothHUD.redTimer</c>/<c>blueTimer</c>: three minutes of holding the
    /// hill wins the round.
    /// </summary>
    public const int KothHoldTicks = 180 * 30;

    /// <summary>
    /// <c>KothHUD.cpUnlock</c>: thirty seconds of pregame before the hill can be
    /// taken.
    /// </summary>
    public const int KothUnlockTicks = 900;

    /// <summary>
    /// <c>global.setupTimer</c> at the start of an attack/defend round, and of
    /// an Invasion one.
    /// </summary>
    /// <remarks>
    /// Thirty seconds for attack/defend and a full minute for Invasion —
    /// <c>ControlPointHUD</c> and <c>InvasionHUD</c> set different values for
    /// the same global.
    /// </remarks>
    public const int AttackDefendSetupTicks = 900;

    /// <inheritdoc cref="AttackDefendSetupTicks"/>
    public const int InvasionSetupTicks = 1800;

    /// <summary>
    /// ⚠️ Attack/defend <b>ignores the configured time limit</b> and always
    /// runs three minutes: <c>timeLimit = 3*30*60</c>. Each capture then adds
    /// another 5,400 ticks, capped at 9,000.
    /// </summary>
    public const int AttackDefendTimeLimit = 3 * 30 * 60;

    /// <summary>
    /// Ticks a capture adds to an attack/defend clock —
    /// <c>ControlPointHUD.timer += 5400</c>, three minutes.
    /// </summary>
    public const int AttackDefendCaptureBonus = 5400;

    /// <summary>
    /// <c>timeLimit = global.timeLimitMins*30*60</c>: minutes to ticks.
    /// </summary>
    public static int TimerFor(int timeLimitMinutes) => timeLimitMinutes * 30 * 60;

    /// <summary>
    /// The starting values a mode's HUD sets in its Create event.
    /// </summary>
    /// <remarks>
    /// Called once the map's objective is placed — <see cref="Intel.Place"/> and
    /// <see cref="ControlPoints.Place"/> both run first, because this sets the
    /// initial ownership of a DKotH map's two hills.
    /// <para>
    /// <b>Every mode's round clock is the same number.</b> CTF calls it
    /// <c>timer</c> and counts the round down with it; KotH and DKotH call it
    /// <c>hostTimer</c> and spend it only while nothing is happening. Both are
    /// <c>global.timeLimitMins*30*60</c>, so
    /// <see cref="WorldState.RoundTimer"/> is one field.
    /// </para>
    /// </remarks>
    public static void Start(ref WorldState state)
    {
        state.RoundTimer = TimerFor(state.TimeLimitMinutes);
        state.Overtime = false;
        state.RoundDecided = false;
        state.SetupTimer = 0;

        if (state.GameMode == GameMode.Invasion)
        {
            state.SetupTimer = InvasionSetupTicks;
            return;
        }

        if (state.GameMode == GameMode.Arena)
        {
            // ArenaHUD.events/Create.xml. The point starts neutral and locked
            // like KotH's, and the match opens in Waiting until somebody is on
            // each team.
            state.ArenaState = ArenaState.Waiting;
            state.ArenaRedWins = 0;
            state.ArenaBlueWins = 0;
            state.ArenaEndCount = 0;
            state.ArenaRoundStart = 0;
            state.ArenaWinner = Team.Spectator;
            state.ControlPointUnlock = Arena.UnlockTicks;
            return;
        }

        if (state.GameMode == GameMode.ControlPoints)
        {
            // ⚠️ Attack/defend ignores the configured time limit entirely and
            // always plays three minutes, plus three more per capture.
            if (state.AttackDefend)
            {
                state.SetupTimer = AttackDefendSetupTicks;
                state.RoundTimer = AttackDefendTimeLimit;
            }

            for (var i = 0; i < state.ControlPointCount; i++)
            {
                state.ControlPoints[i].CapTime =
                    ControlPoints.CapTimeFor(state.AttackDefend, state.ControlPointCount, i);

                // ⚠️ `User Event 0` starts a point locked in Arena, KotH and
                // DKotH and **unlocked** in the two control-point modes. It
                // matters for the points the neighbour rules never write to: a
                // neutral middle point is touched by neither branch, so it
                // keeps whatever it started as — and starting it locked makes
                // the opening fight of every symmetrical map impossible.
                state.ControlPoints[i].Locked = false;
            }

            ControlPoints.AssignStartingOwners(ref state);
            return;
        }

        if (state.GameMode is not (GameMode.KingOfTheHill or GameMode.DualKingOfTheHill))
            return;

        // KothHUD/DKothHUD Create: three minutes each and thirty seconds of
        // pregame.
        state.RedTimer = KothHoldTicks;
        state.BlueTimer = KothHoldTicks;
        state.ControlPointUnlock = KothUnlockTicks;

        // ⚠️ DKotH starts with each hill owned by its own team, which KotH does
        // not: `KothRedControlPoint.team = TEAM_RED; KothBlueControlPoint.team =
        // TEAM_BLUE;`. Without it both hills read neutral and the cross-lock —
        // which asks who owns the *other* one — never engages.
        if (state.GameMode != GameMode.DualKingOfTheHill || state.ControlPointCount < 2)
            return;

        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Red;
        state.ControlPoints[1].Owned = true;
        state.ControlPoints[1].Team = Team.Blue;
    }

    /// <summary>
    /// One tick of the round: the clock, then the end condition.
    /// </summary>
    /// <remarks>
    /// <b>Where this sits in the tick is observable.</b> <c>CTFHUD</c> is created
    /// by <c>basicRoomSetup</c> when the room loads, so it is younger than the
    /// <c>GameServer</c> (which persists across rooms) and older than every
    /// <c>Character</c> (created when a player spawns). Its Step therefore runs
    /// after <c>move_all_bullets</c> and <b>before</b> any character's — which
    /// matters, because deciding the round sets <c>global.mapchanging</c> and
    /// that opens every gate in <c>charSetSolids</c> for the characters that
    /// move later in the same tick.
    /// <para>
    /// ⚠️ <b>The server does not act on the decision until the next tick.</b>
    /// <c>GameServerBeginStep.gml:105</c> reads <c>global.winners</c> in the
    /// server's Begin Step, which is the first thing in a frame — so a round
    /// decided in tick N's Step is broadcast at the start of tick N+1, after
    /// that tick's state update. <c>GameServer</c> keeps that order.
    /// </para>
    /// </remarks>
    public static void Step(ref WorldState state)
    {
        if (state.GameMode == GameMode.KingOfTheHill)
        {
            StepKoth(ref state);
            return;
        }

        if (state.GameMode == GameMode.DualKingOfTheHill)
        {
            StepDkoth(ref state);
            return;
        }

        if (state.GameMode == GameMode.ControlPoints)
        {
            StepControlPoints(ref state);
            return;
        }

        // Arena decides the map from its own round state machine, and needs the
        // tick's events to broadcast its transitions.
        if (state.GameMode == GameMode.Arena)
            return;

        if (state.GameMode == GameMode.Invasion)
        {
            StepInvasion(ref state);
            return;
        }

        if (state.GameMode == GameMode.Generator)
        {
            StepGenerator(ref state);
            return;
        }

        if (state.GameMode == GameMode.TeamDeathmatch)
        {
            StepTeamDeathmatch(ref state);
            return;
        }

        if (state.GameMode != GameMode.CaptureTheFlag)
            return;

        // `if (timer > 0) timer -= 1` — the clock stops at zero rather than
        // going negative, so `timer <= 0` below stays true once it is reached.
        // A RoundTimer of 0 with no time limit configured never enters the
        // decision at all; see WorldState.TimeLimitMinutes.
        if (state.RoundTimer > 0)
            state.RoundTimer--;

        var timeUp = state.TimeLimitMinutes > 0 && state.RoundTimer <= 0;
        var capsReached = state.CapLimit > 0
                          && (state.RedCaps >= state.CapLimit || state.BlueCaps >= state.CapLimit);

        if (!timeUp && !capsReached)
            return;

        // ⚠️ Neither of the two `is capping` reads is about the *player* doing
        // the capturing: `redIsCapping` is true when blue's briefcase is not
        // sitting on its own base — carried by anybody, or lying anywhere else.
        // It is "a capture is in progress", and it is what holds a drawn match
        // open instead of ending it.
        var redIsCapping = IsAwayFromBase(state.BlueIntel);
        var blueIsCapping = IsAwayFromBase(state.RedIntel);

        if (state.RedCaps > state.BlueCaps)
        {
            Decide(ref state, Team.Red);
        }
        else if (state.BlueCaps > state.RedCaps)
        {
            Decide(ref state, Team.Blue);
        }
        else if (!redIsCapping && !blueIsCapping)
        {
            // A draw. TEAM_SPECTATOR is the reference's spelling of "nobody
            // won", and it goes on the wire in MAP_END as a 2.
            //
            // The third clause of the reference's `else if` is
            // `global.blueCaps == global.redCaps`, which the two branches above
            // have already established. Kept out rather than written as a
            // tautology.
            Decide(ref state, Team.Spectator);
        }

        // Nothing decided and the clock is out: overtime. A tied match with a
        // briefcase off its base runs until somebody scores or returns it,
        // which is what makes the flag being *out* worth something at 0:00.
        if (timeUp && !state.RoundDecided)
            state.Overtime = true;
    }

    /// <summary>
    /// <c>KothHUD.events/Step.xml</c>: the hill, the two countdowns, and the
    /// pregame lock.
    /// </summary>
    /// <remarks>
    /// A different shape from CTF's: there is no cap limit, the clock counts
    /// <em>down per team</em> rather than for the round, and the winner is
    /// whoever runs their own timer out while holding the point.
    /// <para>
    /// ⚠️ <b>Winning needs the point uncontested as well as held.</b>
    /// <c>redTimer == 0 and team == TEAM_RED and capping == 0 and
    /// bluePresence == 0</c> — so a blue player standing on red's hill at 0:00
    /// stops red winning for as long as they survive, even though they are not
    /// capturing (a contested point does not move the meter). That is the mode's
    /// whole endgame.
    /// </para>
    /// <para>
    /// ⚠️ <b>The timers only run while the point is unlocked</b>, and the point
    /// is locked for the first 900 ticks. So a KotH round is thirty seconds of
    /// pregame and then at least three minutes.
    /// </para>
    /// <para>
    /// The <c>hostTimer</c> — which ends an idle round as a draw after the time
    /// limit — is transcribed too, because it is the only thing that stops an
    /// empty KotH server sitting on one map for ever.
    /// </para>
    /// </remarks>
    private static void StepKoth(ref WorldState state)
    {
        if (state.ControlPointCount == 0)
            return;

        ref var hill = ref state.ControlPoints[0];

        // `if (global.isHost and hostTimer > 0 and !locked and team != -1 and
        // capping == 0 and bluePresence == 0 and redPresence == 0)`. Note it
        // needs the hill *owned* as well as quiet: a pregame server does not
        // burn its host timer.
        if (state.TimeLimitMinutes > 0
            && state.RoundTimer > 0
            && !hill.Locked
            && hill.Owned
            && hill.Capping == 0
            && hill.RedPresence == 0
            && hill.BluePresence == 0)
        {
            state.RoundTimer--;

            if (state.RoundTimer == 0)
                Decide(ref state, Team.Spectator);
        }

        if (!hill.Locked)
        {
            if (hill.Owned && hill.Team == Team.Red && state.RedTimer > 0)
                state.RedTimer--;

            if (hill.Owned && hill.Team == Team.Blue && state.BlueTimer > 0)
                state.BlueTimer--;
        }

        // ⚠️ `redTimer == 0`, exactly — not `<= 0`. The timer is only ever
        // decremented from above zero, so the two agree; written as the
        // reference writes it.
        if (state.RedTimer == 0 && hill.Owned && hill.Team == Team.Red
            && hill.Capping == 0 && hill.BluePresence == 0)
        {
            Decide(ref state, Team.Red);
        }

        if (state.BlueTimer == 0 && hill.Owned && hill.Team == Team.Blue
            && hill.Capping == 0 && hill.RedPresence == 0)
        {
            Decide(ref state, Team.Blue);
        }

        // The pregame unlock. `doEventUnlockCP` is `with ControlPoint locked =
        // 0` — every point at once, which in KotH is the one hill.
        //
        // ⚠️ The `cpUnlock == 150` arm is an `else if`: five seconds before the
        // hill opens, clients are owed a CAPS_UPDATE so their countdown is right
        // when it does. Both are inside `if (locked and !mapchanging)`, so a
        // round that ends during pregame never unlocks.
        if (hill.Locked && !state.MapChanging)
        {
            if (state.ControlPointUnlock == 0)
            {
                for (var i = 0; i < state.ControlPointCount; i++)
                    state.ControlPoints[i].Locked = false;

                state.UnlockedControlPointsThisTick = true;
            }
            else if (state.ControlPointUnlock == 150)
            {
                state.ScoreboardSyncRequested = true;
            }
        }

        if (state.ControlPointUnlock > 0)
            state.ControlPointUnlock--;
    }

    /// <summary>
    /// The setup countdown both gated modes share.
    /// </summary>
    /// <remarks>
    /// <c>ControlPointHUD</c> and <c>InvasionHUD</c> run the same five lines.
    /// ⚠️ <b>The round clock is reset at 30, not at 0</b> — one second before
    /// the gates actually open, which is the siren. Everything above 30 in the
    /// reference is a countdown sound.
    /// </remarks>
    private static void StepSetupTimer(ref WorldState state, int timeLimitTicks)
    {
        if (state.SetupTimer > 0)
            state.SetupTimer--;

        if (state.SetupTimer == 30)
            state.RoundTimer = timeLimitTicks;
    }

    /// <summary>
    /// <c>ControlPointHUD.events/Step.xml</c>: symmetrical control points, and
    /// attack/defend.
    /// </summary>
    /// <remarks>
    /// <b>Two modes, one HUD.</b> Symmetrical is won by holding <em>both ends</em>
    /// — the reference tests only points 1 and n, so the middle can belong to
    /// anybody — and drawn if the clock runs out with nobody capturing.
    /// Attack/defend is won by red taking the <b>last</b> point, and by blue
    /// simply surviving the clock.
    /// <para>
    /// ⚠️ <b>Overtime here is not CTF's.</b> It is recomputed from scratch every
    /// tick as "the clock is out <em>and</em> some point has a capture in
    /// progress", so it switches off again the moment the capture is reverted.
    /// CTF's is a latch. And it is what stops the clock ending a match while a
    /// point is being taken.
    /// </para>
    /// <para>
    /// ⚠️ <b>Attack/defend's clock is capped at 9,000 ticks</b> — five minutes —
    /// because each capture adds 5,400 to it
    /// (<c>ControlPoint.events/Step.xml:222</c>) and it would otherwise
    /// overflow the <c>uint</c> it is sent in. The reference credits the two
    /// players who found and fixed that.
    /// </para>
    /// </remarks>
    private static void StepControlPoints(ref WorldState state)
    {
        var count = state.ControlPointCount;
        if (count == 0)
            return;

        if (state.RoundTimer > 0)
            state.RoundTimer--;

        if (state.AttackDefend)
            StepSetupTimer(ref state, AttackDefendTimeLimit);

        // `overtime = 0` then re-derived: not a latch.
        state.Overtime = false;

        if (state.RoundTimer == 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (state.ControlPoints[i].Capping > 0)
                    state.Overtime = true;
            }
        }

        ref readonly var first = ref state.ControlPoints[0];
        ref readonly var last = ref state.ControlPoints[count - 1];

        if (state.AttackDefend)
        {
            if (last.Owned && last.Team == Team.Red)
                Decide(ref state, Team.Red);
            else if (state.RoundTimer == 0 && !state.Overtime)
                Decide(ref state, Team.Blue);

            if (state.RoundTimer > 9000)
            {
                state.RoundTimer = 9000;
                state.ScoreboardSyncRequested = true;
            }

            return;
        }

        // ⚠️ Both ends the same team — including both *neutral*, which the
        // reference guards against by testing for a real team afterwards rather
        // than by excluding it from the equality.
        if (first.Owned && last.Owned && first.Team == last.Team)
            Decide(ref state, first.Team);
        else if (state.RoundTimer == 0 && !state.Overtime)
            Decide(ref state, Team.Spectator);
    }

    /// <summary>
    /// <c>InvasionHUD.events/Step.xml</c>: red attacks the briefcase, blue
    /// defends the clock.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not CTF's rules, and the port used to run it through them.</b>
    /// Invasion is one-sided: only blue's briefcase can be taken (the red one is
    /// scenery — see <c>IntelState.Grabbable</c>), red wins on the cap limit,
    /// and blue wins simply by running the clock out.
    /// <para>
    /// ⚠️ <b>Blue's win has a let-off for a red team that is nearly there</b>:
    /// <c>timer &lt;= 0 and (!redIsCapping or global.redCaps+1 &lt; caplimit)</c>.
    /// So a capture in progress only holds the match open when it would be the
    /// <em>winning</em> capture; at 1 of 5 with the flag out, blue still wins on
    /// the whistle.
    /// </para>
    /// <para>
    /// ⚠️ And <c>redIsCapping</c> is a different test from CTF's "away from
    /// base": it is <c>!instance_exists(IntelligenceBlue) or alarm[0] &gt; 0</c>
    /// — carried, or dropped with its return timer still running. A briefcase
    /// dropped with a dead timer does not count.
    /// </para>
    /// </remarks>
    private static void StepInvasion(ref WorldState state)
    {
        StepSetupTimer(ref state, TimerFor(state.TimeLimitMinutes));

        var redIsCapping = !state.BlueIntel.OnField || state.BlueIntel.RecoverTimer > 0;

        if (state.RoundTimer > 0)
            state.RoundTimer--;

        if (state.CapLimit > 0 && state.RedCaps >= state.CapLimit)
        {
            Decide(ref state, Team.Red);
        }
        else if (state.TimeLimitMinutes > 0 && state.RoundTimer <= 0
                 && (!redIsCapping || (state.CapLimit > 0 && state.RedCaps + 1 < state.CapLimit)))
        {
            Decide(ref state, Team.Blue);
        }

        if (state.TimeLimitMinutes > 0 && state.RoundTimer <= 0 && !state.RoundDecided)
            state.Overtime = true;
    }

    /// <summary>
    /// <c>TeamDeathmatchHUD.events/Step.xml</c>: the shortest round rule in the
    /// game.
    /// </summary>
    /// <remarks>
    /// Five lines, and the kills it reads are <see cref="WorldState.RedCaps"/>
    /// and <see cref="WorldState.BlueCaps"/> — Team Deathmatch has no counter of
    /// its own. See <see cref="Deaths"/>.
    /// <para>
    /// ⚠️ <b>The win condition is not in an <c>else</c>.</b> Unlike every other
    /// mode, the decrement and the decision are the same statement sequence, so
    /// the tick a kill limit is reached is the tick the round ends — no waiting
    /// for the next one. The clock's own arm still has to reach zero first.
    /// </para>
    /// <para>
    /// ⚠️ <b>Reaching the kill limit does not mean you win.</b> The three-way
    /// comparison below runs whatever triggered it, so a team that hits the
    /// limit by team-killing... cannot, because a team kill scores nothing. But
    /// a simultaneous double kill that puts both teams over on one tick is a
    /// <em>draw</em>, and so is the clock running out level.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>overtime</c> is dead here.</b> <c>Create.xml</c> sets it false
    /// and nothing in the mode ever writes it again — the only gamemode with no
    /// overtime at all. A capture cannot be in progress, so there is nothing to
    /// hold a round open for.
    /// </para>
    /// </remarks>
    private static void StepTeamDeathmatch(ref WorldState state)
    {
        if (state.RoundTimer > 0)
            state.RoundTimer--;

        // Both limits use this file's "0 means off" escape, which the reference
        // reaches by flooring each at 1 (game_init.gml:96, 103). Without them a
        // default world would end its round on the first tick, twice over.
        var killsReached = state.KillLimit > 0
                           && (state.RedCaps >= state.KillLimit || state.BlueCaps >= state.KillLimit);

        var timeUp = state.TimeLimitMinutes > 0 && state.RoundTimer <= 0;

        if (!killsReached && !timeUp)
            return;

        if (state.RedCaps > state.BlueCaps)
            Decide(ref state, Team.Red);
        else if (state.BlueCaps > state.RedCaps)
            Decide(ref state, Team.Blue);
        else
            Decide(ref state, Team.Spectator);
    }

    /// <summary>
    /// <c>GeneratorHUD.events/Step.xml</c>: the clock, and the damage-stage
    /// tie-break it is decided by.
    /// </summary>
    /// <remarks>
    /// <b>The round proper is not decided here at all.</b> A generator reaching
    /// zero ends the match from its own Begin Step
    /// (<see cref="Generators.BeginStep"/>); this method only handles the clock
    /// running out with both still standing, and it does it by comparing how
    /// badly damaged each one is.
    /// <para>
    /// ⚠️ <b>The comparison is by <em>stage</em>, not by hit points.</b> Each
    /// generator is 3 above two thirds, 2 above one third and 1 below — the
    /// same three sprite ranges the Begin Step picks between — so a generator on
    /// 1,400 hp and one on 2,100 are tied, and whoever has done the most damage
    /// can still draw. The stages are the drawn ones and this is the one place
    /// the simulation cares what is drawn.
    /// </para>
    /// <para>
    /// ⚠️ <b>A generator hit in the last three seconds forces overtime</b>, and
    /// it is the generator of the team that is about to <em>win</em> whose
    /// alarm is read — <c>redStage &gt; blueStage &amp;&amp;
    /// GeneratorRed.alarm[0] &gt; -1</c>, red ahead and <b>red's</b> generator
    /// being hit — or either one's when the stages are level. So the match
    /// stays open exactly while the trailing team is still pushing, and the
    /// leader being attacked is what keeps it open. Reading it the other way
    /// round inverts every case.
    /// </para>
    /// <para>
    /// Both generators must still exist for any of it: <c>instance_exists</c>
    /// on each, which is <see cref="GeneratorState.Exists"/>. If one has gone
    /// the round is already decided.
    /// </para>
    /// </remarks>
    private static void StepGenerator(ref WorldState state)
    {
        if (state.RoundTimer > 0)
        {
            state.RoundTimer--;
            return;
        }

        // `else if (timer <= 0 and instance_exists(GeneratorRed) and
        // instance_exists(GeneratorBlue))` — note the `else`: the tie-break
        // cannot run on the same tick the clock reaches zero, only on the next
        // one.
        if (!state.RedGenerator.Exists || !state.BlueGenerator.Exists)
            return;

        // A configured limit of 0 means "no limit" here — see
        // WorldState.TimeLimitMinutes. Without this a default world, which is
        // every unit test and every bot rollout, would reach the tie-break on
        // its first tick with a clock that never started.
        if (state.TimeLimitMinutes <= 0)
            return;

        var redStage = DamageStage(state.RedGenerator);
        var blueStage = DamageStage(state.BlueGenerator);

        // ⚠️ Each `exit`s, so overtime is a latch here as it is in CTF: nothing
        // in this method ever writes it back to false, and only a map change
        // clears it.
        var redUnderFire = state.RedGenerator.RegenerationAlarm > Timers.Inactive;
        var blueUnderFire = state.BlueGenerator.RegenerationAlarm > Timers.Inactive;

        if (redStage > blueStage && redUnderFire)
        {
            state.Overtime = true;
            return;
        }

        if (redStage < blueStage && blueUnderFire)
        {
            state.Overtime = true;
            return;
        }

        if (redStage == blueStage && (redUnderFire || blueUnderFire))
        {
            state.Overtime = true;
            return;
        }

        // ⚠️ The winner is the team whose generator is in *better* shape, which
        // reads backwards until you notice that `redStage` is red's own
        // generator: red wins by having taken less damage, not by having dealt
        // more.
        if (redStage > blueStage)
            Decide(ref state, Team.Red);
        else if (redStage < blueStage)
            Decide(ref state, Team.Blue);
        else
            Decide(ref state, Team.Spectator);
    }

    /// <summary>
    /// Which of the three damage bands a generator is in: 3 healthy, 2 hurt,
    /// 1 critical.
    /// </summary>
    /// <remarks>
    /// The same thresholds <c>Generator.events/Begin Step.xml:25-37</c> picks
    /// its sprite range with, and written the same way — <c>&gt;=</c> against
    /// two thirds and one third of <c>maxHp</c>. GML's comparisons are fuzzy, so
    /// a generator exactly on 1,386 (two thirds of 2,100) is healthy.
    /// </remarks>
    private static int DamageStage(in GeneratorState generator)
    {
        if (Gml.GreaterOrEqual(generator.Hp, Generators.MaxHp * 0.66))
            return 3;

        return Gml.GreaterOrEqual(generator.Hp, Generators.MaxHp * 0.33) ? 2 : 1;
    }

    /// <summary>
    /// <c>DKothHUD.events/Step.xml</c>: two hills, and each team's clock runs on
    /// the <em>other</em> team's.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The timers are crossed, and that is the whole mode.</b>
    /// <c>redTimer</c> counts down while <b>KothBlueControlPoint</b> is red's —
    /// you win by holding the enemy's hill, not your own. Reading it as KotH
    /// with two points gets every condition in this method backwards.
    /// <para>
    /// ⚠️ <b>The draw rule is a margin, not a tie.</b> When the idle timer runs
    /// out the round is a draw if the two clocks are within
    /// <c>30*30</c> ticks — thirty seconds — of each other, and otherwise goes
    /// to whichever is <em>lower</em>, because a lower clock means that team has
    /// held the enemy hill longer.
    /// </para>
    /// <para>
    /// ⚠️ <b>The idle timer resets to full whenever either hill is locked</b>,
    /// which includes the whole pregame — so it only ever starts counting once
    /// both hills are in play and both are home. And it fires on
    /// <c>hostTimer &lt;= 0</c> where KotH uses <c>== 0</c>; the two agree
    /// because it is only decremented from above zero, and both are written as
    /// the reference writes them.
    /// </para>
    /// </remarks>
    private static void StepDkoth(ref WorldState state)
    {
        if (state.ControlPointCount < 2)
            return;

        ref var red = ref state.ControlPoints[0];
        ref var blue = ref state.ControlPoints[1];

        if (state.TimeLimitMinutes > 0 && state.RoundTimer > 0)
        {
            // ⚠️ The reference's comments call the last two clauses "blu
            // uncontested" and "red uncontested", but each checks the presence
            // of the hill's *own* team, not the enemy's. Together with the two
            // `capping == 0` clauses — which an enemy on a hill would break —
            // it means "nobody at all is on either hill", which is what an idle
            // timer wants. The comments are the misleading part.
            if ((!red.Locked || !blue.Locked)
                && red.Owned && red.Team == Team.Red
                && blue.Owned && blue.Team == Team.Blue
                && blue.Capping == 0 && blue.BluePresence == 0
                && red.Capping == 0 && red.RedPresence == 0)
            {
                state.RoundTimer--;

                if (state.RoundTimer <= 0)
                {
                    if (Math.Abs(state.RedTimer - state.BlueTimer) < 30 * 30)
                        Decide(ref state, Team.Spectator);
                    else if (state.RedTimer < state.BlueTimer)
                        Decide(ref state, Team.Red);
                    else if (state.BlueTimer < state.RedTimer)
                        Decide(ref state, Team.Blue);
                }
            }

            if (red.Locked || blue.Locked)
                state.RoundTimer = TimerFor(state.TimeLimitMinutes);
        }

        // Either hill unlocked runs both clocks — and each team's runs on the
        // hill it has taken from the other.
        if (!red.Locked || !blue.Locked)
        {
            if (blue.Owned && blue.Team == Team.Red && state.RedTimer > 0)
                state.RedTimer--;

            if (red.Owned && red.Team == Team.Blue && state.BlueTimer > 0)
                state.BlueTimer--;
        }

        if (state.RedTimer == 0 && blue.Owned && blue.Team == Team.Red
            && blue.Capping == 0 && blue.BluePresence == 0)
        {
            Decide(ref state, Team.Red);
        }

        if (state.BlueTimer == 0 && red.Owned && red.Team == Team.Blue
            && red.Capping == 0 && red.RedPresence == 0)
        {
            Decide(ref state, Team.Blue);
        }

        // ⚠️ Both hills, not either. And in practice this is never true on the
        // tick cpUnlock reaches zero: the points unlock themselves in their own
        // Step, which runs first — see ControlPoints.Relock. So DKotH sends the
        // five-second scoreboard update and never an UNLOCKCP.
        if (red.Locked && blue.Locked && !state.MapChanging)
        {
            if (state.ControlPointUnlock == 0)
            {
                for (var i = 0; i < state.ControlPointCount; i++)
                    state.ControlPoints[i].Locked = false;

                state.UnlockedControlPointsThisTick = true;
            }
            else if (state.ControlPointUnlock == 150)
            {
                state.ScoreboardSyncRequested = true;
            }
        }

        if (state.ControlPointUnlock > 0)
            state.ControlPointUnlock--;
    }

    /// <summary>
    /// <c>IntelligenceBlue.x != IntelligenceBaseBlue.x or ...y != ...y</c>, with
    /// the <c>!instance_exists</c> that precedes it.
    /// </summary>
    /// <remarks>
    /// A carried briefcase has no instance, and the reference reads that as
    /// "capping" too — <c>redIsCapping = !instance_exists(IntelligenceBlue)</c>
    /// is the <em>first</em> assignment, before the position test.
    /// <para>
    /// A map with no intel has no base either, so it is never away from one.
    /// </para>
    /// </remarks>
    private static bool IsAwayFromBase(in IntelState intel)
    {
        if (!intel.HasBase)
            return false;

        if (!intel.OnField)
            return true;

        // Raw inequality, not Gml.Equal: the reference writes `!=` and both
        // sides of a returned flag are assigned from the same base coordinates,
        // so they are bit-identical or genuinely different.
        return intel.X != intel.HomeX || intel.Y != intel.HomeY;
    }

    /// <summary>
    /// Ends the round with <paramref name="winner"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Public only so an operator or a plugin can end a round the way the
    /// clock does</b> — <c>global.winners = ...</c> is what the reference's own
    /// <c>votenextround</c> plugin writes, and a server that ended a round by
    /// any other route would produce a sequence no client has ever been sent.
    /// <see cref="Team.Spectator"/> is "nobody won", which is what a vote to
    /// skip the map means.
    /// </remarks>
    public static void Decide(ref WorldState state, Team winner)
    {
        state.Winner = winner;
        state.RoundDecided = true;
    }

    /// <summary>
    /// What <c>global.mapchanging</c> does to the world while the win is on
    /// screen.
    /// </summary>
    /// <remarks>
    /// Set by <c>GameServerBeginStep.gml:118</c> the tick after the round is
    /// decided, and cleared by the map change ten seconds later. Everything it
    /// changes is here or named here:
    /// <list type="bullet">
    /// <item>
    /// <b>Every gate opens.</b> <c>charSetSolids.gml:4</c> wraps both the
    /// <c>IntelGate</c> and the <c>TeamGate</c> blocks in
    /// <c>if(!global.mapchanging)</c>, and <c>gunSetSolids.gml:5</c> wraps its
    /// <c>TeamGate</c> block — but <em>not</em> its <c>IntelGate</c> one. So to
    /// a bullet an intel gate stays solid through the victory screen and a team
    /// gate does not. <see cref="CollisionFilter.MapChanging"/>.
    /// </item>
    /// <item>
    /// <b>A carrier drops the briefcase</b> and nobody may pick one up —
    /// <c>Begin Step.xml:366</c> and the two intel collision events'
    /// <c>!global.mapchanging</c>. <see cref="Intel"/>.
    /// </item>
    /// <item>
    /// <b>Everybody gets full ammo</b>, <c>Begin Step.xml:404</c>, the same body
    /// as the uber's. <see cref="CharacterPhysics.BeginStepUberEffects"/>.
    /// </item>
    /// <item>
    /// <b>Nobody respawns.</b> <c>PlayerSpawn.gml:2</c>. The alarm still counts
    /// down and simply spawns nothing when it fires, which is the reference's
    /// behaviour and not a special case — the guard is inside the script.
    /// </item>
    /// </list>
    /// <para>
    /// ✅ <b><c>player.humiliated</c> IS modelled</b> —
    /// <see cref="PlayerState.Humiliated"/>, set by <see cref="Arena"/> and read
    /// by <see cref="CharacterPhysics"/> for the control penalty, the suppressed
    /// attack, taunt, cloak and sandvich, and the withheld ammo refill. ⚠️ <b>This
    /// paragraph said "not modelled" until 2026-09-03</b>, three phases after it
    /// landed. Its reasoning — that only a drawing object sets it — was true of
    /// the reference and never the whole story here.
    /// </para>
    /// </remarks>
    public static void BeginMapChange(ref WorldState state) => state.MapChanging = true;

    /// <summary>
    /// Clears the round and puts the world back to a fresh map's state, keeping
    /// the players.
    /// </summary>
    /// <remarks>
    /// The simulation half of <c>GameServerBeginStep.gml:133-179</c>: every
    /// character, weapon, sentry and projectile is destroyed with the room, and
    /// what survives is the <c>Player</c> instances — which are persistent
    /// objects. Their stats are wiped and (on the first area only) their team is
    /// reset to spectator, and each is given <c>alarm[5] = 1</c> so it spawns in
    /// the same tick's alarm stage.
    /// <para>
    /// ⚠️ <b>Only on area 1.</b> A multi-area map (Invasion) keeps its teams
    /// between areas, so <paramref name="firstArea"/> false leaves everybody on
    /// the team they had and merely respawns them.
    /// </para>
    /// <para>
    /// The caller places the new map's briefcases (<see cref="Intel.Place"/>)
    /// and sets the new round's limits; this clears what the old map left.
    /// </para>
    /// </remarks>
    public static void ResetForNewMap(ref WorldState state, bool firstArea)
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            // Clear before the sentry, because Clear runs the Destroy event —
            // which drops the briefcase, and the briefcase is about to be
            // replaced anyway.
            state.Clear(slot);
            state.Sentries[slot] = default;
            state.HasSentry[slot] = false;

            if (!state.HasPlayer[slot])
                continue;

            if (firstArea)
                state.Players[slot].Team = Team.Spectator;

            // `alarm[5] = 1` — spawn in this same tick's alarm stage, for
            // everybody at once. A spectator's PlayerSpawn does nothing, which
            // is what makes the line safe to run unconditionally.
            state.Players[slot].RespawnTimer = 1;
        }

        // GameServerBeginStep.gml:144-169, inside the same
        // `global.currentMapArea == 1` guard the team reset uses — so a
        // multi-area Invasion map carries its scoreboard from one area to the
        // next.
        if (firstArea)
            Scoring.ResetAll(ref state);

        while (state.LiveProjectileCount > 0)
            state.FreeProjectile(state.LiveProjectileHandle(0));

        state.RedCaps = 0;
        state.BlueCaps = 0;

        // The generators belong to the old map; the caller places the new map's
        // with Generators.Place, exactly as it does the briefcases.
        state.RedGenerator = default;
        state.BlueGenerator = default;

        // The control points and their timers belong to the old map; the caller
        // places the new map's with ControlPoints.Place and sets the mode's own
        // starting values.
        state.ControlPointCount = 0;
        state.ControlPointUnlock = 0;
        state.SetupTimer = 0;
        state.AttackDefend = false;
        state.ArenaState = ArenaState.Waiting;
        state.ArenaRedWins = 0;
        state.ArenaBlueWins = 0;
        state.ArenaEndCount = 0;
        state.ArenaRoundStart = 0;
        state.ArenaWinner = Team.Spectator;
        state.RedTimer = 0;
        state.BlueTimer = 0;
        state.UnlockedControlPointsThisTick = false;
        state.ScoreboardSyncRequested = false;
        state.Winner = Team.Spectator;
        state.RoundDecided = false;
        state.Overtime = false;
        state.MapChanging = false;
    }
}

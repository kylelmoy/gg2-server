using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The stuck-carrier failsafe: a bot that stops making headway while holding a
/// briefcase puts it down.
/// </summary>
/// <remarks>
/// <para>
/// <b>A failsafe, not a fix for any known bug.</b>
/// <c>HeadlessMatchTests.ACarrierDoesNotWedge</c> is the regression test for the
/// underlying path-follower defect this guards against. This exists because a
/// briefcase held by a motionless bot is the worst failure the objective has: it
/// does not end the round, it does not read as a loss, and it removes the
/// objective from play until the map changes. Measured against the bug
/// deliberately re-introduced: the longest carry fell from <b>22,700 ticks to
/// 1,726</b>, and the flag came back into play 37 times instead of never.
/// </para>
/// <para>
/// <b>It is in the bot, not the simulation, and that is a hard line.</b> A
/// bot pressing "drop intel" is something a player can do, through the same
/// command dispatch a socket reaches. A rule in <c>MatchSimulation</c> that took
/// a briefcase off a motionless carrier would be behaviour the stock game does
/// not have — wire-visible divergence, which CLAUDE.md's "compatible in
/// observable simulation" forbids.
/// </para>
/// </remarks>
public sealed class BotCarrierFailsafeTests
{
    private static (ScriptedBot Bot, WorldState State) Carrier()
    {
        var state = default(WorldState);
        state.GameMode = GameMode.CaptureTheFlag;
        state.AddPlayer(0);
        state.AddPlayer(1);
        state.Players[1].Team = Team.Red;
        state.Players[1].Class = PlayerClass.Scout;
        // CharacterState.Fresh, never new(): six of its fields name a roster
        // slot and a zeroed one claims slot 0 hurt it, assisted and is healing
        // it — none of which throws, because slot 0 always exists.
        state.Characters[1] = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        state.Characters[1].X = 100;
        state.Characters[1].Y = 100;
        state.Alive[1] = true;
        state.Characters[1].CarryingIntel = true;

        // No nav graph, so the bot cannot go anywhere — which is exactly the
        // condition being tested, and needs no broken follower to produce.
        var bot = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(3), stagger: 1);

        return (bot, state);
    }

    private static PlayerCommandBuffer Run(ScriptedBot bot, ref WorldState state, CollisionWorld world, int ticks)
    {
        var commands = default(PlayerCommandBuffer);

        for (var tick = 0; tick < ticks; tick++)
        {
            bot.Poll(new WorldView(in state, world), 1, tick);

            commands.Clear();
            bot.PollCommands(new WorldView(in state, world), 1, tick, ref commands);

            if (commands.Count > 0)
                return commands;
        }

        return commands;
    }

    /// <summary>A carrier that has not moved for ten seconds asks to drop it.</summary>
    [Fact]
    public void AStalledCarrierAsksToDropTheIntel()
    {
        var (bot, state) = Carrier();
        var world = ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, null).Collision;

        var commands = Run(bot, ref state, world, ScriptedBot.CarrierStuckTicks + 60);

        Assert.Equal(1, commands.Count);
        Assert.Equal(PlayerCommandKind.DropIntel, commands[0].Kind);
    }

    /// <summary>
    /// And it does NOT ask before the timeout — the control that stops this
    /// becoming "bots drop the intel constantly".
    /// </summary>
    /// <remarks>
    /// Ten seconds is long enough that a firefight, a regroup hold or a jump
    /// window never trips it; a healthy carry on <c>ctf_truefort</c> is about
    /// 1,760 ticks of continuous movement.
    /// </remarks>
    [Fact]
    public void ACarrierDoesNotDropBeforeTheTimeout()
    {
        var (bot, state) = Carrier();
        var world = ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, null).Collision;

        var commands = Run(bot, ref state, world, ScriptedBot.CarrierStuckTicks - 30);

        Assert.Equal(0, commands.Count);
    }

    /// <summary>
    /// A carrier that keeps MOVING is still made to drop it eventually — the
    /// headway check alone is not enough.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case that proves it: a carrier can circle a pair of surfaces
    /// indefinitely, genuinely covering ground, so the 24px test never fires.
    /// Measured on <c>ctf_truefort</c> — three of ten uncontested seeds, the
    /// carrier ranging up to 1,666px around the <em>enemy</em> base for ~26,000
    /// ticks, capturing nothing.
    /// </para>
    /// <para>
    /// <b>That loop is faithful to the reference, not a port bug.</b>
    /// <c>botPathKeys.gml:160-195</c> has the identical off-route rule, and its
    /// own comment describes this failure — "the pair of surfaces becomes a loop
    /// the bot never leaves… the anti-thrash distance check never fires, because
    /// the bot is genuinely covering ground" — with the blacklist as its remedy.
    /// The remedy is defeated because the blacklist wants two
    /// <em>consecutive</em> misses of one edge, and the misses alternate with the
    /// <c>(-1,-1)</c> "no edge running" sentinel, which resets the count. So the
    /// ceiling is a failsafe over reference behaviour rather than a fix for a
    /// defect.
    /// </para>
    /// <para>
    /// Measured effect: uncontested captures a match went from 5.4 to
    /// <b>7.3</b>, and contested play did not move at all (6v2 5.8, 6v4 3.4,
    /// 6v6 0.4 either way) — the ceiling cannot fire on a real attempt, because
    /// the longest contested carry measured was 3,180 ticks.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMovingCarrierThatNeverDeliversStillDropsIt()
    {
        var (bot, state) = Carrier();
        var world = ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, null).Collision;

        var commands = default(PlayerCommandBuffer);
        var dropped = -1;

        for (var tick = 0; tick < ScriptedBot.CarrierHoldCeiling + 200; tick++)
        {
            // Moving far every tick, so the headway test can never fire.
            state.Characters[1].X = 100 + (tick % 40) * 20;

            bot.Poll(new WorldView(in state, world), 1, tick);

            commands.Clear();
            bot.PollCommands(new WorldView(in state, world), 1, tick, ref commands);

            if (commands.Count > 0)
            {
                dropped = tick;
                break;
            }
        }

        Assert.True(dropped > 0, "a carrier that moved for ever never dropped the intel");
        Assert.Equal(PlayerCommandKind.DropIntel, commands[0].Kind);

        // At the ceiling, not before it — this must not fire on a healthy carry.
        Assert.InRange(dropped, ScriptedBot.CarrierHoldCeiling, ScriptedBot.CarrierHoldCeiling + 60);
    }

    /// <summary>A bot carrying nothing never asks, however still it is.</summary>
    [Fact]
    public void ABotCarryingNothingNeverAsks()
    {
        var (bot, state) = Carrier();
        state.Characters[1].CarryingIntel = false;

        var world = ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, null).Collision;

        var commands = Run(bot, ref state, world, ScriptedBot.CarrierStuckTicks * 2);

        Assert.Equal(0, commands.Count);
    }
}

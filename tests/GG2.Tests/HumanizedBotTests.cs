using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="HumanizedBot"/>: what it changes inside a fight, and that it changes
/// nothing outside one.
/// </summary>
/// <remarks>
/// The brain under the filter is scripted by hand, so each test controls exactly
/// what the filter is given. The "fight" is the scripted layer's combat target,
/// set directly, because the gate reads that and nothing else.
/// </remarks>
public sealed class HumanizedBotTests
{
    private const string Map = "ctf_truefort";
    private const double StandX = 3570;
    private const double StandY = 325;

    /// <summary>A brain whose output is a function of the tick, over a real scripted layer.</summary>
    private sealed class HandBrain(ScriptedBot bot, Func<int, PlayerInput> input) : IInputSource, IWrapsScriptedBot
    {
        public ScriptedBot Scripted => bot;

        public PlayerInput Poll(in WorldView world, int slot, int tick) => input(tick);
    }

    private sealed record Rig(ScenarioWorld World, int Slot, ScriptedBot Bot, HumanizedBot Filter);

    private static Rig Build(
        Func<int, PlayerInput> input, bool fight, uint seed = 7, double enemyDx = 120, HumanizeOptions? options = null)
    {
        var world = ScenarioWorld.Load(Map, 1, ReferenceTree.MapsDirectory, navDirectory: null);
        var slot = world.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);
        var enemy = world.Place(PlayerClass.Heavy, Team.Blue, StandX + enemyDx, StandY);
        var bot = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1);

        if (fight)
        {
            bot.Combat.Target = new BotTarget(BotTargetKind.Character, enemy, StandX + enemyDx, StandY, Visible: true);
            bot.Combat.Visible = true;
        }

        return new Rig(world, slot, bot, new HumanizedBot(new HandBrain(bot, input), options ?? HumanizeOptions.Default, seed));
    }

    private static PlayerInput[] Run(Rig rig, int ticks)
    {
        var view = new WorldView(in rig.World.State, rig.World.Collision);
        var outputs = new PlayerInput[ticks];

        for (var t = 0; t < ticks; t++)
            outputs[t] = rig.Filter.Poll(in view, rig.Slot, t);

        return outputs;
    }

    private static double Degrees(ushort wire) => wire * 360.0 / 65536.0;

    private static double Gap(double a, double b) => Math.Abs((((a - b) % 360) + 540) % 360 - 180);

    private static PlayerInput Flicker(int tick) => new(
        (tick % 2 == 0 ? PlayerKeys.Left : PlayerKeys.Right) | (tick % 3 == 0 ? PlayerKeys.Attack : PlayerKeys.None),
        (ushort)(tick * 1500 % 65536),
        (byte)(tick % 200));

    [Fact]
    public void OutsideAFightTheInputPassesThroughUnchanged()
    {
        var rig = Build(Flicker, fight: false);
        var outputs = Run(rig, 300);

        for (var t = 0; t < outputs.Length; t++)
            Assert.Equal(Flicker(t), outputs[t]);

        Assert.False(rig.Filter.Engaged);
    }

    /// <summary>The control for the test above: the same input inside a fight IS changed.</summary>
    [Fact]
    public void InsideAFightTheSameInputIsChanged()
    {
        var rig = Build(Flicker, fight: true);
        var outputs = Run(rig, 300);

        Assert.True(rig.Filter.Engaged);
        Assert.Contains(Enumerable.Range(0, outputs.Length), t => outputs[t] != Flicker(t));
    }

    /// <summary>A target beyond <see cref="HumanizeOptions.Range"/> is not a fight.</summary>
    [Fact]
    public void ATargetOutOfRangeIsNotAFight()
    {
        var rig = Build(Flicker, fight: true, enemyDx: HumanizeOptions.Default.Range + 50);
        var outputs = Run(rig, 120);

        for (var t = 0; t < outputs.Length; t++)
            Assert.Equal(Flicker(t), outputs[t]);
    }

    /// <summary>
    /// Every key passes through inside a fight too, a flickering strafe included.
    /// A held key was v2 and made the visible motion less human, not more.
    /// </summary>
    [Fact]
    public void KeysPassThroughInAFight()
    {
        static PlayerInput Input(int tick) => new(
            Flicker(tick).Keys
            | (tick % 2 == 0 ? PlayerKeys.Jump : PlayerKeys.None)
            | (tick % 3 == 0 ? PlayerKeys.Attack : PlayerKeys.None)
            | (tick % 5 == 0 ? PlayerKeys.Special : PlayerKeys.None),
            0,
            40);

        var outputs = Run(Build(Input, fight: true), 200);

        for (var t = 0; t < outputs.Length; t++)
        {
            Assert.Equal(Input(t).Keys, outputs[t].Keys);
            Assert.Equal(Input(t).AimDistance, outputs[t].AimDistance);
        }
    }

    /// <summary>
    /// With <see cref="HumanizeOptions.Default"/>, a large turn is a one-tick flick,
    /// not a constant slew, and it arrives where the brain wants the aim.
    /// </summary>
    [Fact]
    public void ByDefaultALargeTurnIsAFlickThatArrives()
    {
        static PlayerInput Input(int tick) => new(PlayerKeys.None, tick < 30 ? (ushort)0 : (ushort)(120 * 65536 / 360), 40);

        var outputs = Run(Build(Input, fight: true), 120);
        var biggest = 0.0;

        for (var t = 31; t < outputs.Length; t++)
            biggest = Math.Max(biggest, Gap(Degrees(outputs[t].NetAimDirection), Degrees(outputs[t - 1].NetAimDirection)));

        Assert.True(biggest > 60, $"the largest single turn was {biggest:0.0} deg: a slew, not a flick");

        var final = Degrees(outputs[^1].NetAimDirection);
        Assert.True(Gap(final, 120) < 5, $"settled at {final:0.0} deg, wanted 120");
    }

    /// <summary>
    /// <see cref="HumanizeOptions.Default"/> reproduces v3, the measured shape,
    /// exactly: the same output on every tick of a long, varied input.
    /// </summary>
    /// <remarks>
    /// v4 generalised the aim into strokes and smoothed corrections, and v3 is
    /// the special case with one-tick strokes and no smoothing. Every number
    /// measured for v3 is about the
    /// default only while this holds. <see cref="V3"/> is v3's aim, copied from
    /// the commit that was measured (8593cab) and kept here as the reference.
    /// </remarks>
    [Fact]
    public void TheDefaultReproducesV3Exactly()
    {
        var random = new Random(11);
        var wanted = new ushort[3000];
        var at = 0.0;

        for (var t = 0; t < wanted.Length; t++)
        {
            // Mostly small drifts, sometimes a jump: both of the filter's branches.
            at += random.NextDouble() < 0.03 ? random.NextDouble() * 300 - 150 : random.NextDouble() * 6 - 3;
            wanted[t] = (ushort)((int)Math.Round(((at % 360) + 360) % 360 * 65536.0 / 360.0) & 0xFFFF);
        }

        var outputs = Run(Build(t => new PlayerInput(PlayerKeys.None, wanted[t], 40), fight: true, seed: 5), wanted.Length);
        var reference = new V3(5);

        for (var t = 0; t < wanted.Length; t++)
            Assert.Equal(reference.Next(wanted[t], first: t == 0), outputs[t].NetAimDirection);
    }

    /// <summary>v3's aim, verbatim but for the class around it: the reference for <see cref="TheDefaultReproducesV3Exactly"/>.</summary>
    private sealed class V3(uint seed)
    {
        private GmlRandom _random = new(seed ^ 0x9E3779B9u);
        private double _aim;
        private int _reactLeft = -1;

        public ushort Next(ushort wire, bool first)
        {
            if (first)
                _aim = wire * 360.0 / 65536.0;

            var error = Wrap((wire * 360.0 / 65536.0) - _aim);
            var size = Math.Abs(error);

            if (size >= 20)
            {
                if (_reactLeft < 0)
                    _reactLeft = 2 + _random.NextInclusive(2);

                if (_reactLeft > 0)
                {
                    _reactLeft--;
                    return Wire(_aim);
                }

                _reactLeft = -1;
                _aim = Wrap(_aim + (error * (1 + (0.08 * Gaussian()))));
                return Wire(_aim);
            }

            _reactLeft = -1;

            if (size < 1.0)
                return Wire(_aim);

            _aim = Wrap(_aim + (0.4 * error * (1 + (0.3 * Gaussian()))));
            return Wire(_aim);
        }

        private double Gaussian()
        {
            var u1 = 1.0 - _random.Next(1);
            var u2 = _random.Next(1);
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static ushort Wire(double degrees)
        {
            var d = degrees % 360.0;
            if (d < 0)
                d += 360.0;

            return (ushort)((int)Math.Round(d * 65536.0 / 360.0) & 0xFFFF);
        }

        private static double Wrap(double degrees)
        {
            var d = (degrees + 180.0) % 360.0;
            if (d < 0)
                d += 360.0;

            return d - 180.0;
        }
    }

    /// <summary>
    /// With <see cref="HumanizeOptions.Strokes"/>, a large turn is one stroke: a
    /// few ticks long, fastest in the middle, and it arrives where the brain wants
    /// the aim.
    /// </summary>
    /// <remarks>
    /// Not a one-tick jump (v3, whose turn the next tick could not predict) and not
    /// a constant slew (the brain's own 24-degree <see cref="BotSkill.TurnRate"/>
    /// plateau). The stroke is the run of ticks turning more than 5 degrees after
    /// the reaction delay. The small corrections that follow its landing are not
    /// part of it.
    /// </remarks>
    /// <summary>
    /// <b>v5 (<see cref="HumanizeOptions.Lazy"/>) lets the aim drift between
    /// shots and closes the same gap once the brain fires.</b>
    /// </summary>
    /// <remarks>
    /// A 30° error is inside the idle dead zone (45°), so with the attack key up
    /// the aim stays where it was; held down, it is v3's correction and arrives.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LazyAimDriftsBetweenShotsAndClosesWhenFiring(bool firing)
    {
        var keys = firing ? PlayerKeys.Attack : PlayerKeys.None;
        PlayerInput Input(int tick) => new(keys, tick < 10 ? (ushort)0 : (ushort)(30 * 65536 / 360), 40);

        var outputs = Run(Build(Input, fight: true, options: HumanizeOptions.Lazy), 90);
        var final = Degrees(outputs[^1].NetAimDirection);

        if (firing)
            Assert.True(Gap(final, 30) < 3, $"firing, settled at {final:0.0} deg, wanted 30");
        else
            Assert.True(Gap(final, 0) < 0.01, $"between shots, drifted to {final:0.0} deg; a 30 deg error is inside the idle dead zone");
    }

    /// <summary>
    /// <b>D2 (<see cref="HumanizeOptions.Hold"/>): with no target and an enemy
    /// close, a walk reversal does not swing the aim</b>, and v3 passes it through.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithNoTargetTheHoldPresetKeepsTheAimThroughAWalkReversal(bool hold)
    {
        // The brain points along its walk: right, then left from tick 20.
        static PlayerInput Input(int tick) => new(PlayerKeys.None, tick < 20 ? (ushort)0 : (ushort)(180 * 65536 / 360), 40);

        var outputs = Run(Build(Input, fight: false, enemyDx: 100, options: hold ? HumanizeOptions.Hold : HumanizeOptions.Default), 40);
        var final = Degrees(outputs[^1].NetAimDirection);

        if (hold)
            Assert.True(Gap(final, 0) < 0.01, $"held, the aim swung to {final:0.0} deg");
        else
            Assert.True(Gap(final, 180) < 0.01, $"v3 should pass the walk through, got {final:0.0} deg");
    }

    /// <summary>With no enemy in range, the hold preset follows the walk like v3.</summary>
    [Fact]
    public void WithNoEnemyCloseTheHoldPresetFollowsTheWalk()
    {
        static PlayerInput Input(int tick) => new(PlayerKeys.None, tick < 20 ? (ushort)0 : (ushort)(180 * 65536 / 360), 40);

        var final = Degrees(Run(Build(Input, fight: false, enemyDx: 600, options: HumanizeOptions.Hold), 40)[^1].NetAimDirection);

        Assert.True(Gap(final, 180) < 0.01, $"no enemy within range, yet the aim held at {final:0.0} deg");
    }

    /// <summary>
    /// <b>D3, the difficulty knob:</b> tiers 1-4 have <see cref="BotSkill.HumanAim"/>
    /// and tier 5 does not.
    /// </summary>
    [Fact]
    public void OnlyTheTopTierTracksExactly()
    {
        for (var tier = 1; tier <= 4; tier++)
            Assert.True(BotSkill.ForTier(tier).HumanAim, $"tier {tier}");

        Assert.False(BotSkill.ForTier(5).HumanAim);
    }

    /// <summary>
    /// <b><see cref="HumanizedBot.ByTier"/> is an exact pass-through at tier 5</b>,
    /// filters at tier 4, and follows a live difficulty change.
    /// </summary>
    [Fact]
    public void ByTierFiltersTheLowerTiersAndPassesTheTopOneThrough()
    {
        var random = new Random(3);
        var wanted = new ushort[300];
        var at = 0.0;

        for (var t = 0; t < wanted.Length; t++)
        {
            at += random.NextDouble() < 0.05 ? random.NextDouble() * 200 - 100 : random.NextDouble() * 8 - 4;
            wanted[t] = (ushort)((int)Math.Round(((at % 360) + 360) % 360 * 65536.0 / 360.0) & 0xFFFF);
        }

        var rig = Build(t => new PlayerInput(PlayerKeys.Attack, wanted[t], 40), fight: true);
        var filter = HumanizedBot.ByTier(new HandBrain(rig.Bot, t => new PlayerInput(PlayerKeys.Attack, wanted[t], 40)), 7);
        var view = new WorldView(in rig.World.State, rig.World.Collision);

        rig.Bot.Skill = BotSkill.ForTier(5);
        for (var t = 0; t < 100; t++)
            Assert.Equal(wanted[t], filter.Poll(in view, rig.Slot, t).NetAimDirection);

        rig.Bot.Skill = BotSkill.ForTier(4);
        var changed = 0;
        for (var t = 100; t < 200; t++)
            changed += filter.Poll(in view, rig.Slot, t).NetAimDirection != wanted[t] ? 1 : 0;
        Assert.True(changed > 50, $"tier 4 changed only {changed} of 100 ticks");

        rig.Bot.Skill = BotSkill.ForTier(5);
        for (var t = 200; t < 300; t++)
            Assert.Equal(wanted[t], filter.Poll(in view, rig.Slot, t).NetAimDirection);
    }

    /// <summary>
    /// <b>D4 (<see cref="HumanizeOptions.Tracking2"/>)</b>: a large turn is closed
    /// in partial strokes, and a moving correction reverses now and then.
    /// </summary>
    [Fact]
    public void Tracking2ClosesALargeTurnInPartsAndTrembles()
    {
        static PlayerInput Input(int tick) => new(PlayerKeys.Attack, tick < 10 ? (ushort)0 : (ushort)(90 * 65536 / 360), 40);

        var outputs = Run(Build(Input, fight: true, options: HumanizeOptions.Tracking2), 200);
        var turns = new double[outputs.Length];

        for (var t = 1; t < outputs.Length; t++)
            turns[t] = Wrap180(Degrees(outputs[t].NetAimDirection) - Degrees(outputs[t - 1].NetAimDirection));

        var biggest = turns.Skip(10).Max(Math.Abs);
        Assert.True(biggest < 60, $"one tick turned {biggest:0.0} deg: a 90 deg error should be closed in parts");

        var reversals = Enumerable.Range(11, turns.Length - 11).Count(t => turns[t] * turns[t - 1] < 0);
        Assert.True(reversals > 0, "a moving correction never turned back");

        var final = Degrees(outputs[^1].NetAimDirection);
        Assert.True(Gap(final, 90) < 12, $"settled at {final:0.0} deg, more than the dead zone and a tremor from 90");
    }

    private static double Wrap180(double degrees) => ((degrees % 360) + 540) % 360 - 180;

    /// <summary>
    /// <b>D5 (<see cref="HumanizeOptions.EngageWithoutTarget"/>)</b>: with no target
    /// and an enemy close, a walk reversal is filtered, not passed through or held.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EngagingWithoutATargetFiltersAWalkReversal(bool engage)
    {
        static PlayerInput Input(int tick) => new(PlayerKeys.None, tick < 20 ? (ushort)0 : (ushort)(180 * 65536 / 360), 40);

        var options = HumanizeOptions.Tracking2 with { EngageWithoutTarget = engage };
        var outputs = Run(Build(Input, fight: false, enemyDx: 100, options: options), 23);
        var turn = Gap(Degrees(outputs[^1].NetAimDirection), Degrees(outputs[19].NetAimDirection));

        if (engage)
            Assert.True(turn < 120, $"engaged, a walk reversal still turned {turn:0.0} deg in three ticks");
        else
            Assert.True(Gap(Degrees(outputs[^1].NetAimDirection), 180) < 0.01, "not engaged, the walk should pass through");
    }

    /// <summary>Past the idle flick threshold, a lazy aim still turns.</summary>
    [Fact]
    public void LazyAimStillFlicksAtALargeError()
    {
        static PlayerInput Input(int tick) => new(PlayerKeys.None, tick < 10 ? (ushort)0 : (ushort)(120 * 65536 / 360), 40);

        var final = Degrees(Run(Build(Input, fight: true, options: HumanizeOptions.Lazy), 90)[^1].NetAimDirection);

        Assert.True(Gap(final, 120) < 45, $"settled at {final:0.0} deg, more than the idle dead zone from 120");
    }

    [Fact]
    public void ALargeTurnIsAStrokeThatArrives()
    {
        // Still at 0 degrees, then the brain wants 120 degrees and holds it.
        static PlayerInput Input(int tick) => new(PlayerKeys.None, tick < 30 ? (ushort)0 : (ushort)(120 * 65536 / 360), 40);

        var outputs = Run(Build(Input, fight: true, options: HumanizeOptions.Strokes), 120);
        var turns = Enumerable.Range(1, outputs.Length - 1)
            .Select(t => Gap(Degrees(outputs[t].NetAimDirection), Degrees(outputs[t - 1].NetAimDirection)))
            .ToArray();

        var start = Array.FindIndex(turns, 30, t => t > 5);
        var end = Array.FindIndex(turns, start, t => t <= 5);
        var stroke = turns[start..end];
        var shown = string.Join(", ", stroke.Select(t => t.ToString("0.0")));

        Assert.InRange(stroke.Length, HumanizeOptions.Strokes.MinStrokeTicks - 2, HumanizeOptions.Strokes.MaxStrokeTicks);
        Assert.True(stroke.Max() < 0.6 * 120, $"the stroke's turns were {shown}: a jump");
        Assert.True(stroke.Max() > 24, $"the stroke's turns were {shown}: no faster than the brain's own slew");
        Assert.True(stroke.Max() > stroke[0] && stroke.Max() > stroke[^1], $"the stroke's turns were {shown}: not fastest in the middle");

        var final = Degrees(outputs[^1].NetAimDirection);
        Assert.True(Gap(final, 120) < 5, $"settled at {final:0.0} deg, wanted 120");
    }

    /// <summary>
    /// Arriving on a target, the aim comes to rest close to it but not exactly on
    /// it: the people's best-case miss is several pixels, the bots' under two.
    /// </summary>
    /// <remarks>
    /// The target moves every 60 ticks, alternately by a flick-sized and a
    /// correction-sized step, and the error is read over each segment's last 20
    /// ticks, once it has settled. A target the aim already sits on stays exact,
    /// which is a person keeping still, so the test has to approach one.
    /// </remarks>
    [Fact]
    public void AnAcquiredTargetIsHeldCloselyButNotExactly()
    {
        static ushort Wanted(int tick) => (ushort)((tick / 60 % 2 == 0 ? 0 : (tick / 120 % 2 == 0 ? 45 : 8)) * 65536 / 360 + (tick / 60 * 1000));

        var outputs = Run(Build(t => new PlayerInput(PlayerKeys.None, Wanted(t), 40), fight: true), 1200);
        var settled = Enumerable.Range(60, 1140).Where(t => t % 60 >= 40).ToArray();
        var errors = settled.Select(t => Gap(Degrees(outputs[t].NetAimDirection), Degrees(Wanted(t)))).ToArray();

        Assert.True(errors.Max() < 6, $"settled up to {errors.Max():0.00} deg off a still target");
        Assert.True(errors.Count(e => e > 0.05) > errors.Length / 2, "the aim came to rest exactly on the target");
    }

    [Fact]
    public void TheSameSeedGivesTheSameOutputAndAnotherSeedDoesNot()
    {
        var a = Run(Build(Flicker, fight: true, seed: 11), 300);
        var b = Run(Build(Flicker, fight: true, seed: 11), 300);
        var c = Run(Build(Flicker, fight: true, seed: 12), 300);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    /// <summary>
    /// The filter has its own stream. Drawing from the scripted layer's would
    /// change the brain's decisions as well as its output.
    /// </summary>
    [Fact]
    public void TheScriptedLayersStreamIsNotDrawnFrom()
    {
        var rig = Build(Flicker, fight: true);
        var before = rig.Bot.Random.Seed;

        Run(rig, 300);

        Assert.Equal(before, rig.Bot.Random.Seed);
    }

    [Fact]
    public void BothUnwrappersSeeThroughTheFilter()
    {
        var bot = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1);
        var filtered = new HumanizedBot(bot, HumanizeOptions.Default, 1);

        Assert.Same(bot, ScriptedBot.Of(filtered));
        Assert.Null(HybridBot.Of(filtered));
    }

    [Fact]
    public void TheServerWrapsEveryBotWhenAskedAndNoneOtherwise()
    {
        // (Humanize, HumanAim): Humanize wins; HumanAim alone is the by-tier
        // default; neither leaves the bot unwrapped.
        foreach (var (humanize, humanAim) in new[] { (true, true), (false, true), (false, false) })
        {
            using var harness = new ServerHarness(o =>
            {
                o.Map = Map;
                o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
                o.Bots = new BotPopulationOptions(Enabled: true, FillToPlayers: 4, MaxBots: 4, MinHumans: 0, Difficulty: 5);
                o.BotHumanize = humanize;
                o.BotHumanAim = humanAim;
            });

            harness.Tick(10);

            var bots = harness.Server.BotSlots;
            Assert.NotEmpty(bots);

            foreach (var slot in bots)
            {
                var source = harness.Server.SourceOf(slot);
                Assert.Equal(humanize || humanAim, source is HumanizedBot);

                if (source is HumanizedBot filter)
                    Assert.Equal(!humanize, filter.OnlyWhenSkillAsks);
            }
        }
    }
}

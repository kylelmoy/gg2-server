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

    private static Rig Build(Func<int, PlayerInput> input, bool fight, uint seed = 7, double enemyDx = 120)
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

        return new Rig(world, slot, bot, new HumanizedBot(new HandBrain(bot, input), HumanizeOptions.Default, seed));
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
    /// A large turn is one stroke: a few ticks long, fastest in the middle, and it
    /// arrives where the brain wants the aim.
    /// </summary>
    /// <remarks>
    /// Not a one-tick jump (v3, whose turn the next tick could not predict) and not
    /// a constant slew (the brain's own 24-degree <see cref="BotSkill.TurnRate"/>
    /// plateau). The stroke is the run of ticks turning more than 5 degrees after
    /// the reaction delay. The small corrections that follow its landing are not
    /// part of it.
    /// </remarks>
    [Fact]
    public void ALargeTurnIsAStrokeThatArrives()
    {
        // Still at 0 degrees, then the brain wants 120 degrees and holds it.
        static PlayerInput Input(int tick) => new(PlayerKeys.None, tick < 30 ? (ushort)0 : (ushort)(120 * 65536 / 360), 40);

        var outputs = Run(Build(Input, fight: true), 120);
        var turns = Enumerable.Range(1, outputs.Length - 1)
            .Select(t => Gap(Degrees(outputs[t].NetAimDirection), Degrees(outputs[t - 1].NetAimDirection)))
            .ToArray();

        var start = Array.FindIndex(turns, 30, t => t > 5);
        var end = Array.FindIndex(turns, start, t => t <= 5);
        var stroke = turns[start..end];
        var shown = string.Join(", ", stroke.Select(t => t.ToString("0.0")));

        Assert.InRange(stroke.Length, HumanizeOptions.Default.MinStrokeTicks - 2, HumanizeOptions.Default.MaxStrokeTicks);
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
        foreach (var humanize in new[] { true, false })
        {
            using var harness = new ServerHarness(o =>
            {
                o.Map = Map;
                o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
                o.Bots = new BotPopulationOptions(Enabled: true, FillToPlayers: 4, MaxBots: 4, MinHumans: 0, Difficulty: 5);
                o.BotHumanize = humanize;
            });

            harness.Tick(10);

            var bots = harness.Server.BotSlots;
            Assert.NotEmpty(bots);

            foreach (var slot in bots)
                Assert.Equal(humanize, harness.Server.SourceOf(slot) is HumanizedBot);
        }
    }
}

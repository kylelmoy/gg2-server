using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The handover chat bubbles: a debugging aid that lets a brain be watched from
/// inside the game.
/// </summary>
/// <remarks>
/// <b>Three claims, and the third is the one nothing else could contradict.</b>
/// The mapping is arithmetic; the wrapper's transitions are the seam; and
/// whether a bubble actually reaches the wire is a question only a decoded
/// broadcast answers — a bot layer that pushed a command nothing executed would
/// look identical from inside <c>Gg2.Bots</c>.
/// <para>
/// ⚠️ <b>Every assertion of an absence here states its own control.</b> "No
/// bubble was sent" passes on a bot that never engaged, so the off-by-default
/// test asserts a handover happened in the same run.
/// </para>
/// </remarks>
public sealed class HandoverBubbleTests
{
    /// <summary>A controller that always decides, named whatever a test needs.</summary>
    private sealed class Stub(string name) : IMicroController
    {
        public string Name { get; } = name;

        public bool DecidedThisPoll => true;

        internal int Polls { get; private set; }

        public bool Drives(PlayerClass playerClass) => true;

        public bool AtDecisionBoundary(int slot) => true;

        public void OnHandoverIn(in WorldView world, int slot) { }

        public void OnHandoverOut() { }

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            Polls++;
            return new PlayerInput(PlayerKeys.Attack, 0, 0);
        }
    }

    /// <summary>
    /// The three bubbles are the client's own <c>z</c> menu.
    /// </summary>
    /// <remarks>
    /// <c>BubbleMenuZ.events/Key {1,2,3} pressed.xml</c> set <c>bubbleImage</c> to
    /// 20, 21 and 22. Pinned as literals rather than read back off the constants,
    /// because the constants are what could drift: a test asserting
    /// <c>TookPolicy == HandoverBubble.TookPolicy</c> passes on any value.
    /// </remarks>
    [Fact]
    public void TheBubblesAreTheClientsOwnZMenu()
    {
        Assert.Equal(20, HandoverBubble.TookPolicy);
        Assert.Equal(21, HandoverBubble.LeftAController);
        Assert.Equal(22, HandoverBubble.TookSearch);
    }

    /// <summary>Which bubble each handover asks for.</summary>
    /// <remarks>
    /// <b>The labelled rows are the ones that matter.</b> A controller may answer
    /// <c>policy:&lt;checkpoint&gt;</c> rather than the bare word — the shape
    /// <c>MicroControllerSpec</c> already uses — and an equality match would give
    /// it no bubble while looking perfectly correct on the two shipped names.
    /// </remarks>
    [Theory]
    [InlineData("policy", HandoverBubble.TookPolicy)]
    [InlineData("policy:tier0_combat4_s2", HandoverBubble.TookPolicy)]
    [InlineData("search", HandoverBubble.TookSearch)]
    [InlineData("search:wide", HandoverBubble.TookSearch)]
    public void AControllerTakingTheWheelIsNamedByItsBubble(string name, byte expected) =>
        Assert.Equal(expected, HandoverBubble.For(new Stub(name)));

    /// <summary>The scripted layer taking it back is one bubble, whatever it took it from.</summary>
    [Fact]
    public void LeavingAControllerIsAlwaysTheSameBubble() =>
        Assert.Equal(HandoverBubble.LeftAController, HandoverBubble.For(null));

    /// <summary>
    /// A controller nobody has given a bubble gets silence, not a wrong one.
    /// </summary>
    /// <remarks>
    /// <b>A gap, and gated so it is a decided one.</b> Silence is the safe
    /// failure for a debugging aid — it cannot mislabel a brain — but a fourth
    /// controller arriving without a line in <see cref="HandoverBubble.For"/> is
    /// invisible to this aid. This test is where that is written down.
    /// </remarks>
    [Fact]
    public void AnUnrecognisedControllerGetsNoBubble() =>
        Assert.Null(HandoverBubble.For(new Stub("rollout-mcts")));

    /// <summary>
    /// A bot puts up the taking bubble on the way in and the leaving bubble on
    /// the way out.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The bubble is decided in <c>Poll</c> and sent from
    /// <c>PollCommands</c>, which the server calls FIRST.</b> So each one is a
    /// tick behind the handover that asked for it, and this drives the two in the
    /// server's own order to hold that property still.
    /// </remarks>
    [Theory]
    [InlineData("policy", HandoverBubble.TookPolicy)]
    [InlineData("search", HandoverBubble.TookSearch)]
    public void TakingAndLeavingTheWheelEachPutUpTheirBubble(string name, byte taking)
    {
        var world = World();
        var tier = new Stub(name);
        var bot = Wrap(world, tier, bubbles: true);

        // In range: the controller takes the wheel.
        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var blue = world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        Assert.Equal([taking], Drain(bot, world, red, ticks: 6));
        Assert.True(tier.Polls > 0);

        // Out of range: the scripted layer takes it back. Moved rather than
        // killed, because a dead character is the scripted layer's by a
        // different branch and would prove less.
        world.State.Characters[blue].X = 3000;
        world.Step();

        Assert.Equal([HandoverBubble.LeftAController], Drain(bot, world, red, ticks: 6));
    }

    /// <summary>
    /// The spacing clears the server's bubble throttle, so no bubble a bot sends
    /// is dropped there.
    /// </summary>
    /// <remarks>
    /// At 30 Hz, with one <c>Environment.TickCount64</c> step (~15.6 ms) of
    /// slack. If the throttle's default is raised, this goes red rather than the
    /// aid quietly losing bubbles again.
    /// </remarks>
    [Fact]
    public void TheSpacingClearsTheServersBubbleThrottle() =>
        Assert.True(
            HybridBot.BubbleSpacingPolls * (1000.0 / 30) - 16 >= ConnectionLimits.Default.ChatBubbleIntervalMs,
            $"{HybridBot.BubbleSpacingPolls} polls against a {ConnectionLimits.Default.ChatBubbleIntervalMs} ms throttle");

    /// <summary>
    /// A release right after a take is delayed to clear the throttle, not lost.
    /// </summary>
    /// <remarks>
    /// The search's own pattern at the edge of the range: the wheel changes hands
    /// a couple of ticks apart. Each bubble that goes out must be at least
    /// <see cref="HybridBot.BubbleSpacingPolls"/> after the last, and the final one
    /// must describe where the bot ended up.
    /// </remarks>
    [Fact]
    public void AHandoverInsideTheWindowIsDelayedNotLost()
    {
        var world = World();
        var bot = Wrap(world, new Stub("search"), bubbles: true);

        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var blue = world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        // In range for 2 polls, then out for good.
        var (sent, _) = Schedule(bot, world, red, blue, tick => tick < 2, ticks: 30);

        Assert.Equal([HandoverBubble.TookSearch, HandoverBubble.LeftAController], sent.Select(s => s.Image));
        Assert.True(sent[1].Poll - sent[0].Poll >= HybridBot.BubbleSpacingPolls, $"{sent[0].Poll} then {sent[1].Poll}");
    }

    /// <summary>
    /// A wheel that flaps is described, not narrated: every bubble clears the
    /// throttle, none repeats the last, and the last names who is driving.
    /// </summary>
    /// <remarks>
    /// <b>Properties rather than tick positions</b>, because when the scripted
    /// layer next wants to engage is its own cadence and a schedule pinned to it
    /// breaks on any change there. The control is the handover count: more
    /// changes of hands than bubbles sent is what says coalescing happened, rather
    /// than a wheel that changed hands slowly enough to need none.
    /// </remarks>
    [Fact]
    public void AFlappingWheelEndsOnTheBubbleForWhoeverDrives()
    {
        var world = World();
        var bot = Wrap(world, new Stub("search"), bubbles: true);

        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var blue = world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        // In and out every other tick for 3 s, then in range for 2 s to settle.
        var (sent, handovers) = Schedule(bot, world, red, blue, tick => tick >= 90 || tick % 4 < 2, ticks: 150);

        for (var i = 1; i < sent.Count; i++)
        {
            Assert.True(sent[i].Poll - sent[i - 1].Poll >= HybridBot.BubbleSpacingPolls, $"{sent[i - 1].Poll} then {sent[i].Poll}");
            Assert.NotEqual(sent[i - 1].Image, sent[i].Image);
        }

        Assert.NotNull(bot.Driving);
        Assert.Equal(HandoverBubble.TookSearch, sent[^1].Image);
        Assert.True(handovers > sent.Count, $"{handovers} handovers, {sent.Count} bubbles");
    }

    /// <summary>
    /// Off by default, and the control is that a handover really happened.
    /// </summary>
    [Fact]
    public void NoBubblesUnlessAskedFor()
    {
        var world = World();
        var tier = new Stub("policy");
        var bot = Wrap(world, tier, bubbles: false);

        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        Assert.Empty(Drain(bot, world, red, ticks: 6));

        // THE CONTROL. Without it this passes on a bot that engaged nobody, on a
        // gate that never opened, and on the feature having been deleted.
        Assert.True(tier.Polls > 0);
        Assert.True(bot.MicroTicks > 0);
    }

    /// <summary>
    /// The bubble reaches the wire as a CHAT_BUBBLE naming the bot's slot.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The claim the rest of this file cannot make.</b> A
    /// <c>PlayerCommand</c> the bot layer pushes and <c>GameServer.Execute</c>
    /// has no case for is dropped in silence — every test above still passes, and
    /// nothing the operator is watching for ever appears. This decodes the
    /// server's own broadcast through <see cref="MirrorClient"/>, which is the
    /// only thing here that reads what a client would read.
    /// </remarks>
    [Fact]
    public void TheBubbleReachesTheWire()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = MapName;
            options.MapRotation = [MapName];
            options.ShuffleRotation = ShuffleMode.None;
        });

        var socket = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(socket, Load, MapName);

        // A bot whose only command is the bubble, pushed through the same
        // ICommandSource seam a HybridBot uses. The wrapper's own transitions are
        // the subject above; what is under test here is the command path.
        var slot = harness.Server.AddBot("[BOT] 1", Team.Red, PlayerClass.Soldier, new Bubbler());

        harness.Tick(3);
        mirror.Pump();

        var bubble = Assert.Single(mirror.Of(MessageType.ChatBubble));

        Assert.Equal((byte)slot, bubble.PlayerId);
        Assert.Equal(HandoverBubble.TookPolicy, bubble.Payload[1]);
    }

    /// <summary>A bot that asks for one bubble and then nothing.</summary>
    private sealed class Bubbler : IInputSource, ICommandSource
    {
        private bool _sent;

        public PlayerInput Poll(in WorldView world, int slot, int tick) => default;

        public void PollCommands(
            in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
        {
            if (_sent)
                return;

            _sent = commands.Push(PlayerCommand.ChatBubble(HandoverBubble.TookPolicy));
        }
    }

    private const string MapName = "ctf_truefort";

    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static ScenarioWorld World() =>
        ScenarioWorld.Load(MapName, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

    private static HybridBot Wrap(ScenarioWorld world, IMicroController tier, bool bubbles) =>
        new([tier], new ScriptedBot(world.Nav, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 0))
        {
            Bubbles = bubbles,
        };

    /// <summary>
    /// Runs the bot for a few ticks in the server's own order and returns every
    /// bubble it asked for.
    /// </summary>
    /// <remarks>
    /// <b><c>PollCommands</c> BEFORE <c>Poll</c>, which is <c>GameServer.Tick</c>'s
    /// order</b> — <c>ServiceBots</c> runs above <c>GatherInputs</c>. Polling them
    /// the other way round would hide the one-tick lag this aid has, and a test
    /// that hid it would let a change to the tick order go unnoticed.
    /// </remarks>
    /// <summary>
    /// Runs the bot in the server's order with the opponent in or out of range
    /// per tick, and returns every bubble sent and how many times the wheel
    /// actually changed hands.
    /// </summary>
    private static (List<(int Poll, byte Image)> Sent, int Handovers) Schedule(
        HybridBot bot, ScenarioWorld world, int red, int blue, Func<int, bool> inRange, int ticks)
    {
        var sent = new List<(int, byte)>();
        var commands = default(PlayerCommandBuffer);
        var handovers = 0;
        var driving = bot.Driving;

        for (var tick = 0; tick < ticks; tick++)
        {
            // Placed before the polls, so the Poll on this tick sees it. Y is held
            // too: out of range is far along the floor, not a fall.
            world.State.Characters[blue].X = inRange(tick) ? 680 : 3000;

            var view = new WorldView(in world.State, world.Collision);

            commands.Clear();
            bot.PollCommands(in view, red, tick, ref commands);

            for (var i = 0; i < commands.Count; i++)
            {
                if (commands[i].Kind == PlayerCommandKind.ChatBubble)
                    sent.Add((tick, commands[i].Argument));
            }

            bot.Poll(in view, red, tick);
            world.Step();

            if (!ReferenceEquals(bot.Driving, driving))
            {
                handovers++;
                driving = bot.Driving;
            }
        }

        return (sent, handovers);
    }

    private static List<byte> Drain(HybridBot bot, ScenarioWorld world, int slot, int ticks)
    {
        var seen = new List<byte>();
        var commands = default(PlayerCommandBuffer);

        for (var tick = 0; tick < ticks; tick++)
        {
            var view = new WorldView(in world.State, world.Collision);

            commands.Clear();
            bot.PollCommands(in view, slot, tick, ref commands);

            for (var i = 0; i < commands.Count; i++)
            {
                if (commands[i].Kind == PlayerCommandKind.ChatBubble)
                    seen.Add(commands[i].Argument);
            }

            bot.Poll(in view, slot, tick);
            world.Step();
        }

        return seen;
    }
}

using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// <c>SIM_UUID</c> — <c>docs/BOTAI.md</c> item 9.
/// </summary>
/// <remarks>
/// ⚠️ <b>A digest is the easiest thing in the world to gate uselessly.</b>
/// "It equals itself" passes against a stamp that covers nothing, and
/// "it is sixteen characters" passes against a constant. So every test here is
/// about what the stamp <em>covers</em> and what it deliberately does not, and the
/// surface is exposed for exactly that reason.
/// </remarks>
public sealed class SimulationStampTests(ITestOutputHelper output)
{
    /// <summary>
    /// This build's stamp, pinned.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A derived value, pinned on purpose.</b> It does two things no
    /// self-comparison can: it fails if the hash is not deterministic across runs
    /// or machines, and it makes <em>any</em> contract change loud. The document's
    /// own expectation is that it is "bumped whenever any of those changes — which
    /// will be often and early, and every bump invalidates every checkpoint. That
    /// is correct." A change here is therefore not a broken test — it is the bump,
    /// and the question to answer before editing this line is which checkpoints it
    /// just invalidated.
    /// <para>
    /// ✅ <b>Bumped 2026-08-28</b>, from <c>99b7ab3141e31fb4</c>, by
    /// <c>ObservationLayoutVersion</c> going 0 → 1: the v0 observation layout
    /// exists now (<c>Gg2.Env.ObservationWriter</c>). <b>It invalidated no
    /// checkpoints, because none exist yet</b> — which is precisely why
    /// <c>docs/ENV.md</c> chose to do this before <c>gg2-rl</c> phase 2 rather
    /// than after. The bump is free exactly once.
    /// </para>
    /// <para>
    /// ✅ <b>Bumped again 2026-08-28</b>, from <c>08dc7b09303b3843</c>, by the
    /// <c>NAV_SJ_*</c> family arriving in <see cref="NavConstants"/> — the fork
    /// grew sticky-jump tuning and this port carries it. ⚠️ <b>This is the stamp
    /// working, not a nuisance</b>: the surface deliberately includes the
    /// <c>NAV_*</c> block, because a policy's behaviour depends on where the bots
    /// it played against could go.
    /// <para>
    /// ⚠️ <b>It still invalidated no checkpoints — but the window is closing.</b>
    /// <c>gg2-rl</c> is in phase 1 and its client asserts this digest at the
    /// handshake, so a bump is now a refused connection until it updates, which
    /// is the designed behaviour and the reason it was told rather than left to
    /// find out.
    /// </para>
    /// </para>
    /// <para>
    /// ✅ <b>Bumped again 2026-08-29</b>, from <c>61edbca3899d1b07</c>, by
    /// <c>ActionEncodingVersion</c> going 1 → 2: the action gained a sixth head,
    /// <c>Zoom</c>. Until it, <b>no bot in this port could zoom at all</b> —
    /// <c>toggleZoom</c> is a client command rather than a key — so every bot
    /// sniper fired at the unscoped 35 against a weapon that reaches 75, and a
    /// distilled policy would have inherited that permanently.
    /// <para>
    /// ⚠️ <b>THE LAST FREE BUMP, and it was spent deliberately.</b>
    /// <c>gg2-rl</c> HELD phase 2 — its first training run — for this, precisely
    /// so no checkpoint would exist to invalidate. A sixth head added afterwards
    /// invalidates every checkpoint before it and every number derived from them.
    /// The next bump is not free, and this remark is the record of who agreed to
    /// spend this one.
    /// </para>
    /// </para>
    /// </remarks>
    /// <remarks>
    /// ⛔ <b>Bumped again 2026-08-30</b>, from <c>c526b96e34184138</c>, by
    /// <see cref="SimulationStamp.SimulationBehaviourVersion"/> existing at all —
    /// a third hand-bumped number, asked for by <c>gg2-rl</c> after
    /// <c>3bba987</c> changed control flow in the physics and this digest could
    /// not move. Every other entry on the surface is a constant.
    /// <para>
    /// ⛔ <b>THE FIRST BUMP THAT IS NOT FREE, and it was spent on the machinery
    /// rather than on a change.</b> <c>gg2-rl</c> holds checkpoints — two
    /// 600-iteration runs among them — and this refuses every one of them until
    /// they are re-stamped. That cost was accepted in the ask rather than
    /// discovered afterwards, and it buys the field being present the first time
    /// somebody changes what the simulation does, instead of being added in the
    /// same commit and bumped in the same breath.
    /// </para>
    /// <para>
    /// ⚠️ <b>The behaviour version is 2 as of 2026-09-01: the moving-platform
    /// CARRY.</b> A platform now moves the character standing on it, so the stage
    /// moves trajectories and the bump is unconditional. ⚠️ <b>Updated AS the
    /// bump, which is what this pin's own contract asks for — not repaired as a
    /// broken test.</b> The sealed-chamber argument that might once have avoided
    /// it was already dead: <c>gg2-rl</c> seeds duels from nav nodes and 18 of
    /// <c>gg_debug2</c>'s 70 sit inside sealed regions.
    /// </para>
    /// <para>
    /// ⚠️ <b>It did NOT count <c>3bba987</c>.</b> That change was demonstrated
    /// unobservable — two independent 90-iteration training runs, per-iteration
    /// numbers identical to the digit over 1.66M agent-steps — rather than argued
    /// to be. The distinction is the whole rule: see the constant's remarks.
    /// </para>
    /// <para>
    /// ⚠️ <b>Moved 2026-09-03, <c>9de9dc341dd95af9</c> → <c>9e0d931e1106a1bf</c>,
    /// and NOT because behaviour changed.</b> <c>NavConstants</c> left the gating
    /// surface for <c>SimulationStamp.RoutingUuid</c>, on the user's
    /// authorisation, after <c>gg2-rl</c> was stranded twice in two days by nav
    /// edits their own world fingerprint said had changed nothing. ⛔ <b>Done at
    /// this moment on purpose</b>: the only checkpoint that exists is already
    /// stamp-invalid and is a test fixture, so a one-off surface change costs
    /// nothing today and would cost a training run after their next one.
    /// </para>
    /// </remarks>
    private const string Pinned = "9e0d931e1106a1bf";

    /// <summary>
    /// The stamp is stable, and not a per-process hash.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>string.GetHashCode()</c> is randomised per process in .NET.</b> A
    /// stamp built on it changes every time the server restarts, so it would
    /// refuse every policy — including one this very build had just trained. The
    /// stamp is pinned to <see cref="Pinned"/>, which is what makes that failure
    /// loud rather than invisible: if it moves without a contract change, the hash
    /// is not deterministic.
    /// </remarks>
    [Fact]
    public void TheStampIsDeterministic()
    {
        output.WriteLine($"SIM_UUID {SimulationStamp.Uuid} over {SimulationStamp.Surface().Count} entries");

        Assert.Equal(16, SimulationStamp.Uuid.Length);
        Assert.Equal(Pinned, SimulationStamp.Uuid);

        // ⚠️ Recomputed from the surface the same way the type does, in this
        // process, and it must agree. A hash seeded from anything ambient — a
        // clock, a process-wide seed, reflection order — fails here.
        Assert.Equal(SimulationStamp.Uuid, Fnv(SimulationStamp.Surface()));
    }

    /// <summary>
    /// The stamp covers the things a policy's behaviour actually depends on.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the test that matters.</b> A digest that quietly stopped
    /// covering gravity looks exactly like one that covers it and has not changed
    /// — the failure is silent by construction, which is the whole reason the item
    /// exists. So the surface is asserted entry by entry for the load-bearing
    /// ones.
    /// <para>
    /// ⚠️ <c>ClassStats</c> is in the list because that is where the <b>physics
    /// constants</b> live — gravity, the control and friction factors, terminal
    /// velocity — not only the per-class table. A first draft left it out and a
    /// changed gravity would have been invisible to the stamp.
    /// </para>
    /// </remarks>
    /// <summary>
    /// ⛔ The routing constants are on exactly ONE side of the split.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>NavConstants</c> moved out of the gating surface 2026-09-03 because it
    /// changes <em>which states a policy is asked about</em>, not <em>what any
    /// action does</em> — a distribution shift is a reason to re-measure, not a
    /// reason to refuse to run, and <c>gg2-rl</c> was stranded twice in two days
    /// by nav edits their own world fingerprint said had changed nothing.
    /// </para>
    /// <para>
    /// ⚠️ <b>Both directions, because each failure is silent in its own way.</b>
    /// Nav creeping back into the gating surface strands checkpoints again for
    /// nothing. Nav falling out of the routing surface loses the record entirely,
    /// and "the bots play differently and both digests match" is a completely
    /// different investigation from "the bots play differently and the routing
    /// digest moved".
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRoutingSurfaceIsSeparateAndNotGated()
    {
        var gating = SimulationStamp.Surface();
        var routing = SimulationStamp.RoutingSurface();

        Assert.Contains($"NavConstants.CellSize={NavConstants.CellSize}", routing);
        Assert.DoesNotContain($"NavConstants.CellSize={NavConstants.CellSize}", gating);

        // ⚠️ And the physics is NOT in the routing surface. A split that quietly
        // moved gravity to the un-gated side would look exactly like this one.
        Assert.DoesNotContain($"ClassStats.Gravity={ClassStats.Gravity:R}", routing);

        // Two different digests over two different surfaces. Equal ones would mean
        // the split exists in the source and not in the arithmetic.
        Assert.NotEqual(SimulationStamp.Uuid, SimulationStamp.RoutingUuid);
    }

    /// <summary>The routing digest is stable across processes, like the stamp.</summary>
    /// <remarks>
    /// ⚠️ It is reported rather than gated, but a digest that changed per process
    /// would make every report of it useless — and useless in the quiet way, since
    /// nothing compares it and nothing would fail.
    /// </remarks>
    [Fact]
    public void TheRoutingDigestIsStable()
    {
        Assert.Equal(SimulationStamp.RoutingUuid, SimulationStamp.RoutingUuid);
        Assert.Matches("^[0-9a-f]{16}$", SimulationStamp.RoutingUuid);
    }

    [Fact]
    public void TheSurfaceCoversThePhysicsTheBotConstantsAndTheTables()
    {
        var surface = SimulationStamp.Surface();

        foreach (var entry in new[]
        {
            // The physics.
            $"ClassStats.Gravity={ClassStats.Gravity:R}",
            $"ClassStats.BaseControl={ClassStats.BaseControl:R}",
            $"ClassStats.BaseFriction={ClassStats.BaseFriction:R}",
            $"ClassStats.TerminalVSpeed={ClassStats.TerminalVSpeed:R}",

            // GML's own arithmetic, which every trajectory runs through.
            $"Gml.Epsilon={Gml.Epsilon:R}",
            $"Gml.PointSix={Gml.PointSix:R}",

            // The BOT_* block. ⚠️ NAV_* is NOT here any more — see
            // TheRoutingSurfaceIsSeparateAndNotGated below.
            $"BotConstants.EngageTicks={BotConstants.EngageTicks}",

            // The action encoding and the (undesigned) observation layout.
            $"action={SimulationStamp.ActionEncodingVersion}",
            $"observation={SimulationStamp.ObservationLayoutVersion}",

            // ⚠️ And what the simulation DOES, which nothing else here covers:
            // every other entry is a constant, so a changed line of physics
            // moved no digest until this existed. See the constant's remarks.
            $"behaviour={SimulationStamp.SimulationBehaviourVersion}",
        })
        {
            Assert.Contains(entry, surface);
        }

        // Every class's table, and every blast.
        foreach (var playerClass in Enum.GetValues<PlayerClass>())
            Assert.Contains(surface, e => e.StartsWith($"class.{playerClass}=", StringComparison.Ordinal));

        Assert.Contains(surface, e => e.StartsWith("blast.Rocket=", StringComparison.Ordinal));
        Assert.Contains(surface, e => e.StartsWith("blast.Mine=", StringComparison.Ordinal));

        output.WriteLine(string.Join('\n', surface.Where(e => e.StartsWith("class.", StringComparison.Ordinal))));
    }

    /// <summary>
    /// Changing any covered value changes the stamp.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Down to one ULP</b>, which is not a detail here: <c>Gml.PointSix</c>
    /// and its four siblings exist because GM8 parses some decimals one ULP above
    /// the IEEE nearest, and those five ARE gravity and the friction factors. A
    /// stamp that printed to fifteen digits could not tell <c>0.6</c> from the
    /// value the engine actually holds, and would miss the change the whole family
    /// is about.
    /// </remarks>
    [Fact]
    public void AOneUlpChangeToAnyCoveredValueMovesTheStamp()
    {
        var surface = SimulationStamp.Surface().ToList();
        var here = Fnv(surface);

        Assert.Equal(SimulationStamp.Uuid, here);

        var gravity = surface.FindIndex(e => e.StartsWith("ClassStats.Gravity=", StringComparison.Ordinal));
        Assert.True(gravity >= 0);

        var nudged = surface.ToList();
        nudged[gravity] = $"ClassStats.Gravity={Math.BitIncrement(ClassStats.Gravity):R}";

        output.WriteLine($"{surface[gravity]} -> {nudged[gravity]}");
        Assert.NotEqual(surface[gravity], nudged[gravity]);
        Assert.NotEqual(here, Fnv(nudged));

        // The same for a bot constant and for a class's hit points, so the claim
        // is about the whole surface rather than about one entry.
        foreach (var name in new[] { "BotConstants.EngageTicks=", "class.Heavy=" })
        {
            var at = surface.FindIndex(e => e.StartsWith(name, StringComparison.Ordinal));
            Assert.True(at >= 0, name);

            var changed = surface.ToList();
            changed[at] = surface[at] + "x";

            Assert.NotEqual(here, Fnv(changed));
        }
    }

    /// <summary>
    /// Configuration and protocol are deliberately outside the stamp.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A port number moving must not invalidate every checkpoint.</b> The
    /// surface is chosen rather than swept, and the cost of sweeping is not a
    /// false sense of safety — it is a stamp that churns, and a stamp that churns
    /// gets ignored. <c>GameConstants</c> is where that line was drawn.
    /// </remarks>
    [Fact]
    public void ConfigurationAndProtocolAreOutsideTheStamp()
    {
        var surface = SimulationStamp.Surface();

        Assert.DoesNotContain(surface, e => e.StartsWith("GameConstants.", StringComparison.Ordinal));

        // The premise: those constants exist and would have been swept in.
        Assert.True(GameConstants.DefaultPort > 0);
        Assert.True(GameConstants.MaxPlayerNameLength > 0);
    }

    /// <summary>
    /// A mismatched policy is refused, not warned about.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Refuse, do not warn</b> is the item's own instruction and the reason
    /// it exists: a policy trained against physics that has since changed does not
    /// error and does not warn on its own. It plays slightly worse for ever, and
    /// the cause is invisible. GG2 has the idiom one layer up — the reference
    /// refuses a client whose <c>PROTOCOL_UUID</c> does not match rather than
    /// letting it connect and desync.
    /// </remarks>
    [Fact]
    public void AMismatchedPolicyIsRefusedRatherThanWarnedAbout()
    {
        SimulationStamp.Require(SimulationStamp.Uuid);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => SimulationStamp.Require("0000000000000000"));

        // The message has to name both, or the operator cannot tell which side is
        // stale.
        Assert.Contains("0000000000000000", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(SimulationStamp.Uuid, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>FNV-1a 64, written from the algorithm rather than from the port.</summary>
    /// <remarks>
    /// ⚠️ Deliberately not calling the implementation: a recomputation that shared
    /// the code would agree with a wrong implementation as happily as with a right
    /// one, which is the same trap <c>NavCacheTests.WriteGrid</c> avoids by
    /// writing the format rather than inverting the reader.
    /// </remarks>
    private static string Fnv(IEnumerable<string> lines)
    {
        var hash = 14695981039346656037UL;

        foreach (var line in lines)
        {
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(line + "\n"))
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
        }

        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}

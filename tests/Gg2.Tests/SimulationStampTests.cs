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
    /// </remarks>
    private const string Pinned = "99b7ab3141e31fb4";

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

            // The BOT_* and NAV_* blocks.
            $"BotConstants.EngageTicks={BotConstants.EngageTicks}",
            $"NavConstants.CellSize={NavConstants.CellSize}",

            // The action encoding and the (undesigned) observation layout.
            $"action={SimulationStamp.ActionEncodingVersion}",
            $"observation={SimulationStamp.ObservationLayoutVersion}",
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

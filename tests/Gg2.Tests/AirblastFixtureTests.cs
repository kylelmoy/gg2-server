using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The first recording in the project in which a pyro can airblast at all.
/// </summary>
/// <remarks>
/// <b>Three pyros against three soldiers on <c>ctf_truefort</c></b>,
/// spawn-anchored by the map-reload recipe.
/// <para>
/// <b>The roster is the whole design.</b> The reflectable set is
/// <c>Rocket</c>, <c>Flare</c> and <c>Mine</c>
/// (<c>Flamethrower.events/User Event 2.xml</c>), so a pyro with nothing of
/// those in front of it never presses the key. <c>closedsupport</c> has two
/// pyros in it and contains <b>no rockets and no mines</b> — medic, pyro,
/// engineer — so <em>its</em> pyros cannot airblast however long it runs.
/// </para>
/// <para>
/// <b>An unseeded Flamethrower does NOT cap a trajectory window, only an hp
/// one</b> — unlike the Minigun. <c>Projectiles.BurningHitCharacter</c> deals
/// damage and calls no <c>motion_add</c> at all, so where the port's flames land
/// cannot move anybody. The soldier's rockets are seeded, so every rocket in
/// this recording is one the port reproduces exactly. That combination is what
/// makes a trajectory gate possible here and impossible in a heavy recording.
/// </para>
/// </remarks>
public sealed class AirblastFixtureTests
{
    private const string Airblast = "reference-ctf_truefort-closedairblast.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Airblast);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{Airblast}: {stopped}");
        return (trace, ticks);
    }

    private static int Count(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Count(t => t.Events.Any(m => m.Type == type));

    private static (int Exact, int Anchor) ExactWindow(Fields compare)
    {
        var (trace, ticks) = Load();

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Airblast}: some slot holds a character it never spawned into");

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(Airblast, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        return (firstBad - anchor.Value, anchor.Value);
    }

    /// <summary>
    /// The recording holds pyros, soldiers, and enough seeded rocket fire for a
    /// reflect to be possible.
    /// </summary>
    /// <remarks>
    /// A re-recording whose roster forcing failed would otherwise pass the
    /// window test below by containing no rockets — and would do it while making
    /// the window <em>longer</em>, which is the trap this project has fallen
    /// into before.
    /// </remarks>
    [Fact]
    public void TheRecordingHoldsPyrosAndTheRocketsTheyCanReflect()
    {
        var (_, ticks) = Load();

        var classes = ticks
            .SelectMany(t => t.Events)
            .Where(m => m.Type == MessageType.PlayerChangeClass && m.Payload.Length >= 2)
            .Select(m => (PlayerClass)m.Payload[1])
            .ToHashSet();

        // The recorder is a spectator and its class byte is the default Scout,
        // so the set is always the roster plus Scout.
        Assert.Contains(PlayerClass.Pyro, classes);
        Assert.Contains(PlayerClass.Soldier, classes);
        Assert.Equal(3, classes.Count);

        // Every one of these is a *rocket*. The Flamethrower broadcasts no
        // seed, so a pyro's own fire contributes nothing to this count however
        // much of it there is — which is what makes the number meaningful as a
        // measure of how much reflectable ammunition was in the air.
        Assert.True(Count(ticks, MessageType.WeaponFire) >= 30, "not enough rocket fire to reflect");
        Assert.True(Count(ticks, MessageType.PlayerDeath) >= 4, "not enough deaths");
        Assert.True(ticks.Count > 2400, $"only {ticks.Count} ticks");
    }

    /// <summary>
    /// It is closed-loop, so it carries an exactness claim.
    /// </summary>
    [Fact]
    public void EveryCharacterIsSpawnAnchored()
    {
        var (_, ticks) = Load();
        Assert.True(FixtureAnchors.LastExactAnchor(ticks) is not null, $"{Airblast}: not closed-loop");
    }

    /// <summary>
    /// Pyros and soldiers move exactly like the reference from their spawn
    /// anchors — <b>and this window is the airblast's first fixture gate.</b>
    /// </summary>
    /// <remarks>
    /// The airblast is deterministic — no seed, no draw — so unlike every other
    /// pyro behaviour it can be held to a trajectory exactly. What it does is
    /// move things: a shove on every character in a rotated box plus a
    /// per-victim circle, and a reversed <c>direction</c> on every reflectable
    /// projectile in a five-pixel one. Both are trajectory, and both are in this
    /// window.
    /// <para>
    /// <b>Trajectory only, deliberately.</b> The Flamethrower is unseeded, so
    /// the port's flames land somewhere else and the hp window is far shorter —
    /// docs/PARITY.md, "an unseeded weapon caps a fixture's hp window". The
    /// flames cannot move anybody, so they do not touch this number.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePyrosAndSoldiersTrajectoriesAreExactFromTheirSpawnAnchors()
    {
        var (exact, anchor) = ExactWindow(Fields.Trajectory);

        // 1,266 today, t471 to t1737.
        //
        // THIS IS THE AIRBLAST'S GATE, and it is a sharp one: stubbing
        // Weapons.AirBlast out to a no-op takes the same fixture from 1,266
        // ticks to 286 — a factor of 4.4. Nothing else in the port is touched
        // by that stub, so the whole of the difference is the shove and the
        // reflect.
        Assert.True(
            exact >= 1200,
            $"{Airblast}: exact for only {exact} ticks after the last spawn anchor (t{anchor})");
    }

    /// <summary>
    /// And it carries a damage claim nearly as long, which a pyro recording had
    /// no right to.
    /// </summary>
    /// <remarks>
    /// <b>1,133 ticks with hp compared, against 1,266 for trajectory alone.</b>
    /// The unseeded Flamethrower costs only 133 ticks here — far less than the
    /// usual, because this recording's damage is overwhelmingly seeded rocket
    /// damage and a pyro has to be close to burn anybody. Compare
    /// <c>closedsupport</c>, where the same weapon caps hp at 328.
    /// <para>
    /// <b>Do not raise this floor by implementing something.</b> The ceiling
    /// here is a property of the recording — the first flame that connects and
    /// lands differently ends it, whatever the port does. docs/PARITY.md, "an
    /// unseeded weapon caps a fixture's hp window".
    /// </para>
    /// </remarks>
    [Fact]
    public void ItAlsoReproducesTheDamageForNearlyAsLong()
    {
        var (exact, anchor) = ExactWindow(Fields.Trajectory | Fields.Hp);

        // 1,133 today, t471 to t1604.
        Assert.True(
            exact >= 1050,
            $"{Airblast}: hp exact for only {exact} ticks after the last spawn anchor (t{anchor})");
    }
}

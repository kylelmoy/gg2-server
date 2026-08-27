using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The four recordings of the four classes no fixture had ever contained.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27.</b> The heavy, the sniper, Quote and the spy all
/// landed on 2026-08-26 and not one of them appeared in any checked-in
/// recording — every earlier fixture is some mixture of scout, soldier,
/// demoman, medic, pyro and engineer, which is what <c>botPickClass</c> picks.
/// docs/PARITY.md, "four classes now exist in the port that no fixture has ever
/// contained", is the standing argument for these.
/// <para>
/// <b>Six of one class each, three a side</b>, rather than one mixed roster.
/// That is CLAUDE.md's bisect-the-roster shape: a framing error never points at
/// its cause, so a stream that desynchronises in a single-class recording names
/// the class by construction. The roster is forced with <c>botAdd</c> after
/// <c>global.botsEnabled = 0</c> — <c>[Classlimits]</c> narrows what a bot
/// <em>may</em> pick and does not decide what it does.
/// </para>
/// <para>
/// ✅ <b>All four frame cleanly to the last byte</b>, which is the headline
/// result and a genuine negative: there is no <c>INPUTSTATE</c>-style layout bug
/// for any of the four. <see cref="FixtureFramingTests"/> is a Theory over the
/// directory, so it covers these without being told about them; what is here is
/// the content and the closed-loop trajectory windows.
/// </para>
/// <para>
/// ⚠️ <b>Three of the four record no <c>WEAPON_FIRE</c> at all, and that is
/// correct.</b> The Minigun and the Blade call <c>randomize()</c> and broadcast
/// no seed, so a heavy-only or Quote-only recording contains none however much
/// shooting happens — see docs/SIMULATION.md, "an unseeded weapon caps a
/// fixture's hp window". Only the sniper's Rifle is seeded. <b>Do not read a
/// zero here as a quiet recording</b>; read the hp movement instead.
/// </para>
/// </remarks>
public sealed class NewClassFixtureTests
{
    private const string Heavy = "reference-ctf_truefort-closedheavy.trace";
    private const string Sniper = "reference-ctf_truefort-closedsniper.trace";
    private const string Quote = "reference-ctf_truefort-closedquote.trace";
    private const string Spy = "reference-ctf_truefort-closedspy.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{fixture}: {stopped}");
        return (trace, ticks);
    }

    private static int Count(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Count(t => t.Events.Any(m => m.Type == type));

    /// <summary>Every class a <c>PLAYER_CHANGECLASS</c> in the recording names.</summary>
    private static HashSet<PlayerClass> Classes(IReadOnlyList<TraceTick> ticks) =>
        ticks
            .SelectMany(t => t.Events)
            .Where(m => m.Type == MessageType.PlayerChangeClass && m.Payload.Length >= 2)
            .Select(m => (PlayerClass)m.Payload[1])
            .ToHashSet();

    /// <summary>
    /// How long a closed-loop replay stays exact after the last spawn anchor.
    /// </summary>
    private static (int Exact, int Anchor) ExactWindow(string fixture, Fields compare)
    {
        var (trace, ticks) = Load(fixture);

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{fixture}: some slot holds a character it never spawned into");

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        return (firstBad - anchor.Value, anchor.Value);
    }

    /// <summary>The lowest hp the recording ever observes on anybody.</summary>
    private static int MinObservedHp(IReadOnlyList<TraceTick> ticks)
    {
        var min = int.MaxValue;

        foreach (var tick in ticks)
        {
            for (var slot = 0; slot < tick.Slots.Length; slot++)
            {
                if (tick.Slots[slot].Observed is { HasPosition: true } observed)
                    min = Math.Min(min, observed.Hp);
            }
        }

        return min;
    }

    /// <summary>Did the recording ever observe anybody with the cloak bit set?</summary>
    private static bool EverCloaked(IReadOnlyList<TraceTick> ticks)
    {
        foreach (var tick in ticks)
        {
            for (var slot = 0; slot < tick.Slots.Length; slot++)
            {
                if (tick.Slots[slot].Observed is { HasPosition: true, Cloaked: true })
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Each recording holds six of the class it was made for, and nothing else.
    /// </summary>
    /// <remarks>
    /// A re-recording whose roster forcing silently failed would otherwise pass
    /// every other test here by being an ordinary bot match — which is exactly
    /// the failure the <c>botAdd</c> recipe exists to prevent, since the first
    /// attempt at a medic/pyro/engineer fixture produced no engineer at all.
    /// </remarks>
    [Theory]
    [InlineData(Heavy, PlayerClass.Heavy)]
    [InlineData(Sniper, PlayerClass.Sniper)]
    [InlineData(Quote, PlayerClass.Quote)]
    [InlineData(Spy, PlayerClass.Spy)]
    public void EachRecordingIsSixOfOneClass(string fixture, PlayerClass expected)
    {
        var (_, ticks) = Load(fixture);
        var classes = Classes(ticks);

        // ⚠️ The recorder itself contributes one PLAYER_CHANGECLASS. It joins as
        // a passive spectator and its class byte is the default Scout, so the
        // set is always {the class, Scout} and never {the class} alone. None of
        // these four is a Scout fixture, so that stays unambiguous — and it is
        // asserted rather than filtered out, because a *seventh* class turning
        // up here would mean the roster forcing had not held.
        Assert.Equal(new HashSet<PlayerClass> { expected, PlayerClass.Scout }, classes);
        Assert.True(ticks.Count > 2000, $"{fixture}: only {ticks.Count} ticks");
    }

    /// <summary>
    /// All four are spawn-anchored, so each carries an exactness claim rather
    /// than only a bound.
    /// </summary>
    /// <remarks>
    /// This is the property the map-reload-under-a-connected-recorder recipe
    /// buys, and it is worth asserting rather than assuming: one slot that was
    /// alive before the recording started makes the whole thing open-loop.
    /// </remarks>
    [Theory]
    [InlineData(Heavy)]
    [InlineData(Sniper)]
    [InlineData(Quote)]
    [InlineData(Spy)]
    public void EveryCharacterIsSpawnAnchored(string fixture)
    {
        var (_, ticks) = Load(fixture);
        Assert.True(FixtureAnchors.LastExactAnchor(ticks) is not null, $"{fixture}: not closed-loop");
    }

    /// <summary>
    /// The four classes move exactly like the reference from their spawn
    /// anchors.
    /// </summary>
    /// <remarks>
    /// <b>The first closed-loop evidence any of these four has ever had.</b>
    /// Their physics is not shared: each has its own run power, jump strength
    /// and bounding box (<c>ClassStats</c>, <c>CharacterBox</c>), and Quote's
    /// box is a different shape from every other class's — it ends at +11 where
    /// the others end at +23. A wrong box changes where a character stops
    /// against a wall, which is a trajectory error and not a drawing one.
    /// <para>
    /// ⚠️ <b>Trajectory only, and for two different reasons.</b> Three of the
    /// four weapons here are unseeded or hitscan, so the port cannot be held to
    /// this recording's hp: the Minigun and the Blade broadcast no seed and the
    /// Rifle is a binary search rather than a projectile. The thresholds below
    /// are the measured windows less a small margin.
    /// </para>
    /// </remarks>
    [Theory]
    // Re-measured 2026-08-27 after the alarm-stage spawn seam landed. The
    // heavy's and the sniper's are the short ones and that is the weapons, not
    // the classes: an unseeded Minigun's spread cannot be reproduced, so the port
    // lands its pellets somewhere else and shoves people somewhere else. Quote's
    // and the spy's run nearly the whole recording because a bubble barely pushes
    // and a cloaked spy is not fighting.
    //
    // ⚠️ The heavy's is the one number in this table that has moved: 270 before
    // the seam and 396 after it, attributed by measurement rather than guessed
    // (switching the queueing off reproduces 270 exactly). The other three are
    // unchanged to the tick, which is the control — a seam that shifted every
    // fixture by a tick would have moved all four.
    [InlineData(Heavy, 350)]    // 396 today, t499
    [InlineData(Sniper, 340)]   // 362 today, t613
    [InlineData(Quote, 1700)]   // 1756 today, t486
    [InlineData(Spy, 1750)]     // 1829 today, t406
    public void TheNewClassesTrajectoriesAreExactFromTheirSpawnAnchors(string fixture, int floor)
    {
        var (exact, anchor) = ExactWindow(fixture, Fields.Trajectory);

        Assert.True(
            exact >= floor,
            $"{fixture}: exact for only {exact} ticks after the last spawn anchor (t{anchor})");
    }

    /// <summary>
    /// Each recording reaches the state it was made to reach.
    /// </summary>
    /// <remarks>
    /// <b>A re-recording that came out quiet would otherwise pass every other
    /// test here by having nothing to get wrong</b> — and would do it while
    /// making the trajectory windows <em>longer</em>, which is the trap:
    /// <c>reference-ctf_truefort-combat.trace</c> misled a session exactly this
    /// way. Each assertion below names a state no earlier fixture in the project
    /// could reach.
    /// </remarks>
    [Fact]
    public void TheHeavyRecordingHoldsHeavyDoingHeavyThings()
    {
        var (_, ticks) = Load(Heavy);

        // ⚠️ Zero WEAPON_FIRE is correct and must stay asserted: the Minigun
        // calls randomize() and broadcasts no seed. Damage is the only evidence
        // a heavy shot anything, so that is what is asserted.
        Assert.Equal(0, Count(ticks, MessageType.WeaponFire));
        Assert.True(MinObservedHp(ticks) < 100, "nobody was hurt — a heavy's 200 hp barely moved");
        Assert.True(Count(ticks, MessageType.PlayerDeath) >= 3, "not enough deaths");

        // The sandvich. No earlier recording contains one.
        Assert.True(Count(ticks, MessageType.Omnomnomnom) >= 1, "no eating");
    }

    /// <summary>
    /// The sniper's recording is the only one of the four whose weapon is
    /// seeded, and the first fixture anywhere to hold a <c>TOGGLE_ZOOM</c>.
    /// </summary>
    [Fact]
    public void TheSniperRecordingHoldsScopingAndSeededFire()
    {
        var (_, ticks) = Load(Sniper);

        Assert.True(Count(ticks, MessageType.WeaponFire) >= 25, "not enough rifle fire");
        Assert.True(Count(ticks, MessageType.PlayerDeath) >= 4, "not enough deaths");

        // ⚠️ Zoom is never serialised as a state — a client infers it from the
        // charge byte and from a TOGGLE_ZOOM that does not say which way. This
        // is the project's first recorded evidence that the message exists on
        // the wire at all.
        Assert.True(Count(ticks, MessageType.ToggleZoom) >= 15, "nobody scoped");
    }

    /// <summary>
    /// Quote's recording: no seed, little damage, and an objective event.
    /// </summary>
    [Fact]
    public void TheQuoteRecordingHoldsBubblesAndAnIntelReturn()
    {
        var (_, ticks) = Load(Quote);

        // The Blade is the third unseeded weapon, like the Minigun above.
        Assert.Equal(0, Count(ticks, MessageType.WeaponFire));

        // A bubble deals 0.75, so hp moves in very small steps rather than not
        // at all — which is the whole difference between "Quote fought" and
        // "Quote wandered".
        Assert.True(MinObservedHp(ticks) < 140, "nobody took a single point of damage");

        // ⚠️ An intel event inside a recording. docs/PARITY.md's "the objective
        // is the one thing four fixtures observe and none anchors" is why this
        // is worth keeping.
        Assert.True(Count(ticks, MessageType.ReturnIntel) >= 1, "no intel event");
    }

    /// <summary>
    /// The spy's recording, and the one state it exists for.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The cloak bit is the point of this fixture.</b> A spy's <c>cloak</c>
    /// rides in bit 0 of the <c>moveStatus</c> flags byte of every
    /// <c>QUICK_UPDATE</c>, and until this recording nothing in the project had
    /// ever read one back from a real server — the bit was written by the port
    /// and checked only against the port's own reader. If this assertion fails
    /// on a re-recording, the bots never cloaked and the fixture must be
    /// re-taken; it is not a licence to delete the assertion.
    /// </remarks>
    [Fact]
    public void TheSpyRecordingHoldsACloakedSpy()
    {
        var (_, ticks) = Load(Spy);

        Assert.True(EverCloaked(ticks), "no spy ever cloaked — the fixture misses the state it exists for");
    }
}

using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The first recording containing a <c>DROP_INTEL</c> — the last gameplay
/// message the corpus could reach and did not.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27</b>: six red scouts and no blue team on
/// <c>ctf_2dfort</c>, steered to blue's briefcase, and the carrier then made to
/// drop it.
/// <para>
/// ⚠️ <b>How, and why that is the reference's own behaviour rather than a
/// contrivance.</b> <c>processClientCommands.gml:221-229</c> handles a client's
/// <c>DROP_INTEL</c> with exactly two calls behind one guard —
/// <c>sendEventDropIntel(player); doEventDropIntel(player);</c> — and those two
/// lines are what was invoked over the bridge for the carrying bot. A driven
/// client pressing drop reaches the identical pair. What is bypassed is only the
/// client-to-server half, and the recording is of the server's broadcast.
/// </para>
/// <para>
/// ⚠️ <b>A death-caused drop is a different thing and broadcasts nothing.</b>
/// <c>sendEventDropIntel.gml</c>'s own docstring says the event is skipped on
/// death "to avoid issues with order of execution", and the client infers it from
/// the <c>PLAYER_DEATH</c> it already has — which is why no earlier fixture had
/// one despite dozens of deaths with the flag in play.
/// </para>
/// </remarks>
public sealed class DropIntelFixtureTests
{
    private const string Fixture = "reference-ctf_2dfort-dropintel.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks, int Anchor) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{Fixture}: {stopped}");

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Fixture}: not closed-loop");

        return (trace, ticks, anchor!.Value);
    }

    /// <summary>It holds a grab and the drop that follows it.</summary>
    [Fact]
    public void ItHoldsADropIntel()
    {
        var (_, ticks, anchor) = Load();

        var drops = ticks
            .Where(t => t.Events.Any(m => m.Type == MessageType.DropIntel))
            .Select(t => t.Index)
            .ToArray();

        Assert.Single(drops);
        Assert.True(drops[0] > anchor, $"the drop at t{drops[0]} is before the anchor t{anchor}");

        // A drop needs a grab first, and the recording has two grabs — the flag
        // is picked up again after the ten-second cooldown.
        Assert.True(
            ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.GrabIntel)) >= 2,
            "the recording does not grab the flag twice");
    }

    /// <summary>
    /// ✅ <b>The port drops it too</b> — the injected client command reaches
    /// <c>Intel.Drop</c> and the briefcase leaves the carrier.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A <c>DROP_INTEL</c> has to be injected, like a <c>BUILD_SENTRY</c>
    /// and a <c>PLAYER_SPAWN</c>.</b> It is a client command: nothing in an
    /// <c>INPUTSTATE</c> says the player pressed drop, so a replay cannot derive
    /// it and a port that ignored the message would carry the flag on for ever
    /// while the reference put it down.
    /// <para>
    /// ⚠️ <b>And it is applied BEFORE the tick, not after</b> — the opposite of a
    /// grab. <c>processClientCommands</c> runs inside the server's Begin Step,
    /// ahead of <c>serializeState</c>, so the message lands in its <em>own</em>
    /// tick's block; a <c>GRAB_INTEL</c> comes out of the collision stage, after
    /// the update, and lands in the next one's. <b>The two halves of the same
    /// objective use opposite tick conventions.</b>
    /// </para>
    /// <para>
    /// The control is the assertion that the port is carrying the flag on the
    /// tick before: without it this passes against a port that never picked the
    /// briefcase up at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortPutsTheBriefcaseDownOnTheRecordedTick()
    {
        var (trace, ticks, _) = Load();

        var drop = ticks.First(t => t.Events.Any(m => m.Type == MessageType.DropIntel)).Index;

        var watcher = new CarryWatcher(new PortedSimulation(MapLoader));

        new Replayer(watcher) { Compare = Fields.None }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        // The seam was reached at all — the control against a `when` guard that
        // silently never matches, which is exactly how the first attempt at this
        // failed (ServerStream frames DROP_INTEL through Fixed(), so PlayerId is
        // null and a guard on it matches nothing).
        Assert.Equal(1, watcher.Drops);

        // The named slot was carrying, and is not afterwards.
        Assert.Contains(watcher.NamedSlot, watcher.CarriersBefore);

        Assert.DoesNotContain(watcher.NamedSlot, watcher.CarriersAfter);

        // ⚠️ But SOMEBODY still is, and that is the reference's own behaviour
        // rather than a failed drop. A flag taken off its base gives its carrier
        // a return timer of 0; a drop writes that straight into the new
        // instance's alarm[0], and the grab guard's `alarm[0] <= 1` arm means
        // anyone may pick it straight back up (docs/SIMULATION.md, "Settled: the
        // intel"). Measured here: slot 5 drops it and slot 2, standing on the
        // same spot, has it by the end of the same tick.
        //
        // The recording corroborates it — that instant re-grab is the second of
        // its two GRAB_INTELs.
        Assert.NotEmpty(watcher.CarriersAfter);
        Assert.DoesNotContain(watcher.NamedSlot, watcher.CarriersAfter);
    }

    /// <summary>
    /// Watches whether any slot carries the briefcase either side of one tick.
    /// </summary>
    private sealed class CarryWatcher(PortedSimulation inner) : ITraceSimulation
    {
        private bool _armed;

        /// <summary>How many times the replayer reached the drop seam.</summary>
        public int Drops { get; private set; }

        /// <summary>Whether anybody held the briefcase when the drop was injected.</summary>
        public bool CarryingBefore { get; private set; }

        /// <summary>And after the tick that injection preceded.</summary>
        public bool CarryingAfter { get; private set; } = true;

        public int NamedSlot { get; private set; } = -1;

        public List<int> CarriersBefore { get; } = [];

        public List<int> CarriersAfter { get; } = [];

        public void LoadMap(string map) => inner.LoadMap(map);

        public void SetCharacter(int slot, in CharacterBlock observed) => inner.SetCharacter(slot, observed);

        public bool SpawnCharacter(int slot, int spawnpointId, int group) =>
            inner.SpawnCharacter(slot, spawnpointId, group);

        public bool QueueSpawn(int slot, int spawnpointId, int group) => inner.QueueSpawn(slot, spawnpointId, group);

        public bool HasPendingSpawn(int slot) => inner.HasPendingSpawn(slot);

        public void ClearCharacter(int slot) => inner.ClearCharacter(slot);

        public void RemoveSlot(int slot) => inner.RemoveSlot(slot);

        public void SetPlayerTeam(int slot, Team team) => inner.SetPlayerTeam(slot, team);

        public void SetPlayerClass(int slot, PlayerClass playerClass) => inner.SetPlayerClass(slot, playerClass);

        public void SetFireSeed(int slot, ushort seed) => inner.SetFireSeed(slot, seed);

        public bool TryGetControlPoint(int index, out ControlPointBlock point) =>
            inner.TryGetControlPoint(index, out point);

        public void BuildSentry(int slot, double x, double y, int direction) =>
            inner.BuildSentry(slot, x, y, direction);

        public void DropIntel(int slot)
        {
            // Sampled here rather than by tick index: the injection happens in
            // ApplyEvents, before the tick runs, so this is the exact "before".
            CarryingBefore = Carrying();
            NamedSlot = slot;
            CarriersBefore.AddRange(Carriers());
            Drops++;
            _armed = true;

            inner.DropIntel(slot);
        }

        public void SetControlPointUnlock(int ticks) => inner.SetControlPointUnlock(ticks);

        public void SetTdmInvulnerability(int ticks) => inner.SetTdmInvulnerability(ticks);

        public bool TryGetCharacter(int slot, out CharacterBlock character) =>
            inner.TryGetCharacter(slot, out character);

        public IReadOnlyList<DeathRecord> DeathsLastTick => inner.DeathsLastTick;

        public IReadOnlyList<UberRecord> UbersLastTick => inner.UbersLastTick;

        public IReadOnlyList<IntelRecord> IntelLastTick => inner.IntelLastTick;

        public IReadOnlyList<SentryRecord> SentriesLastTick => inner.SentriesLastTick;

        public void Tick(ReadOnlySpan<PlayerInput> inputs)
        {
            inner.Tick(inputs);

            if (!_armed)
                return;

            CarryingAfter = Carrying();
            CarriersAfter.AddRange(Carriers());
            _armed = false;
        }

        private IEnumerable<int> Carriers()
        {
            for (var slot = 0; slot < 32; slot++)
            {
                if (inner.TryGetState(slot, out var state) && state.CarryingIntel)
                    yield return slot;
            }
        }

        private bool Carrying()
        {
            for (var slot = 0; slot < 32; slot++)
            {
                if (inner.TryGetState(slot, out var state) && state.CarryingIntel)
                    return true;
            }

            return false;
        }
    }
}

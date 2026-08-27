using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// How long a death lasts, measured against every recording in the project and
/// then reproduced by the port.
/// </summary>
/// <remarks>
/// <b>This closes an open question that had been carried for several
/// sessions</b>: "respawn timing untested against a recording". The timing was
/// gated only by unit tests that asserted the port against itself — a counter
/// set to <c>RespawnTime</c> and then counted down — which cannot catch the
/// counter being armed in the wrong event, read in the wrong stage, or off by
/// one against the engine.
/// <para>
/// The fixtures can answer it because a <c>PLAYER_DEATH</c> and a
/// <c>PLAYER_SPAWN</c> are both real messages with real tick indices, so the gap
/// between them is the reference's own answer and needs no simulation at all.
/// </para>
/// </remarks>
public sealed class RespawnTimingTests
{
    /// <summary>
    /// The reference's default respawn: <c>Server_Respawntime</c> of 5 seconds
    /// at 30 Hz.
    /// </summary>
    private const int RecordedGap = 150;

    /// <summary>
    /// The other rule, and it is one tick.
    /// </summary>
    /// <remarks>
    /// ✅ <b>Measured 2026-08-27</b>, and it is the first fixture evidence in
    /// the project for <c>Character.events/Destroy.xml:37</c>. That arm writes
    /// <c>player.alarm[5] = 1</c> rather than the respawn time, and an alarm set
    /// to <c>n</c> fires <c>n</c> ticks later — so the death and the
    /// <c>PLAYER_SPAWN</c> answering it land on <b>consecutive</b> ticks.
    /// </remarks>
    private const int RecordedSpawnRoomGap = 1;

    /// <summary>Every fixture that contains at least one death and a respawn.</summary>
    private static readonly string[] Fixtures =
    [
        "reference-ctf_truefort-closedcombat.trace",
        "reference-ctf_truefort-hotcombat.trace",
        "reference-ctf_truefort-closedsupport.trace",
        "reference-ctf_truefort-hotsupport.trace",
        "reference-ctf_truefort-bots.trace",

        // Recorded 2026-08-27. Added here rather than left to the older five, so
        // that a rule this file states about "every recorded death" keeps
        // meaning every one of them: the pair count goes from 28 to 60.
        "reference-ctf_truefort-closedheavy.trace",
        "reference-ctf_truefort-closedsniper.trace",
        "reference-ctf_truefort-closedairblast.trace",
        "reference-ctf_truefort-closedsentrykill.trace",
        "reference-ctf_2dfort-closedrespawn.trace",
        "reference-ctf_truefort-spawnroomdeath.trace",
    ];

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    /// <summary>One recorded death and the respawn that answered it.</summary>
    private readonly record struct Pair(string Fixture, int Slot, int Death, int Spawn, bool InRoom)
    {
        public int Gap => Spawn - Death;
    }

    /// <summary>
    /// Every death-to-respawn pair in every fixture, with where the body was.
    /// </summary>
    private static List<Pair> Pairs()
    {
        var pairs = new List<Pair>();

        foreach (var fixture in Fixtures)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
            Assert.True(File.Exists(path), $"missing {path}");

            var trace = Trace.Decode(File.ReadAllBytes(path));
            var map = MapLoader(trace.Header.Map);
            var world = new CollisionWorld(map);
            var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _);

            var pending = new Dictionary<int, int>();

            foreach (var tick in ticks)
            {
                foreach (var message in tick.Events)
                {
                    if (message.Payload.Length < 1)
                        continue;

                    var slot = message.Payload[0];

                    if (message.Type == MessageType.PlayerDeath)
                    {
                        // The victim's own slot is the first byte.
                        pending[slot] = tick.Index;
                    }
                    else if (message.Type == MessageType.PlayerSpawn
                             && pending.TryGetValue(slot, out var died))
                    {
                        pairs.Add(new Pair(fixture, slot, died, tick.Index, InRoom(world, ticks, died, slot)));
                        pending.Remove(slot);
                    }
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// Whether the body was inside a spawn room, by the last position the
    /// recording observed for it.
    /// </summary>
    /// <remarks>
    /// The last <em>observed</em> position rather than the death tick's, because
    /// updates come every seventh tick and a death rarely lands on one. It is
    /// within a few ticks of movement of the truth, which is far finer than a
    /// spawn room.
    /// </remarks>
    private static bool InRoom(
        CollisionWorld world, IReadOnlyList<TraceTick> ticks, int deathTick, int slot)
    {
        for (var i = ticks.Count - 1; i >= 0; i--)
        {
            var tick = ticks[i];

            if (tick.Index > deathTick || !tick.HasPositions || slot >= tick.Slots.Length)
                continue;

            if (tick.Slots[slot].Observed is not { } observed)
                continue;

            return world.PointInSpawnRoom(observed.X / 5.0, observed.Y / 5.0);
        }

        return false;
    }

    /// <summary>
    /// ⚠️ <b>Every recorded death in the project is answered exactly 150 ticks
    /// later</b> — twenty-eight of them, across five recordings, with no
    /// variance at all.
    /// </summary>
    /// <remarks>
    /// The reference's own answer to "how long is a death", and the number the
    /// port has to reproduce. Zero variance is what makes it a gate rather than
    /// a statistic: a rule that armed the counter in the wrong event or read it
    /// in the wrong stage would show up as an off-by-one on every pair at once.
    /// </remarks>
    [Fact]
    public void EveryRecordedDeathIsAnsweredAfterExactlyOneHundredAndFiftyTicks()
    {
        // ⚠️ Outside a spawn room. Since 2026-08-27 one recording contains a
        // death inside one, and that death is answered in a single tick by a
        // different rule — see
        // <see cref="ADeathInsideASpawnRoomIsAnsweredOnTheVeryNextTick"/>.
        // Filtered rather than tolerated, so a spawn-room death cannot hide a
        // genuinely wrong gap.
        var pairs = Pairs().Where(p => !p.InRoom).ToList();

        // The fixtures contain the deaths this is about. A re-recording that
        // came out quiet would otherwise pass by having nothing to measure.
        // 59 outside a spawn room today, of 60 pairs across eleven recordings.
        Assert.True(pairs.Count >= 50, $"only {pairs.Count} death-to-respawn pairs");

        // And they span more than one recording, so this is not one server's
        // configuration being asserted five times.
        Assert.True(
            pairs.Select(p => p.Fixture).Distinct().Count() >= 4,
            "the pairs come from too few recordings");

        foreach (var pair in pairs)
        {
            Assert.True(
                pair.Gap == RecordedGap,
                $"{pair.Fixture} slot {pair.Slot}: died t{pair.Death}, respawned t{pair.Spawn} "
                + $"— {pair.Gap} ticks, not {RecordedGap}");
        }
    }

    /// <summary>
    /// ✅ <b>A death inside a spawn room is answered on the very next tick</b>,
    /// and a recording finally says so.
    /// </summary>
    /// <remarks>
    /// <b>This replaces <c>NoRecordedDeathHappensInASpawnRoom</c></b>, which
    /// asserted that no fixture contained such a death and was written to fail
    /// the day one did. It did, on 2026-08-27:
    /// <c>reference-ctf_truefort-spawnroomdeath.trace</c> was recorded for
    /// exactly this, and the fast-respawn arm of
    /// <c>Character.events/Destroy.xml:37</c> now has fixture evidence instead
    /// of unit tests asserting the port against itself.
    /// <para>
    /// ⚠️ <b>The death had to be forced, and the first attempt failed for a
    /// reason worth keeping.</b> Setting a character's <c>hp</c> to 0 over the
    /// bridge produced no death at all: a character standing in a spawn room is
    /// on a <b>healing cabinet</b>, which tops it back above zero before End
    /// Step ever tests <c>hp &lt;= 0</c>. Killing one in there needs a value the
    /// cabinet cannot undo in a single tick.
    /// </para>
    /// <para>
    /// <b>The recording carries its own control</b> — see
    /// <see cref="OneRecordingHoldsBothRules"/>.
    /// </para>
    /// <para>
    /// It also confirms the alarm rule end to end through the wire: that arm
    /// writes <c>player.alarm[5] = 1</c>, an alarm set to <c>n</c> fires
    /// <c>n</c> ticks later, and the two messages land on consecutive ticks.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADeathInsideASpawnRoomIsAnsweredOnTheVeryNextTick()
    {
        var inRoom = Pairs().Where(p => p.InRoom).ToArray();

        Assert.True(
            inRoom.Length >= 1,
            "no fixture contains a death inside a spawn room any more — the fast respawn has "
            + "lost its only recorded evidence, so re-record one rather than deleting this test");

        foreach (var pair in inRoom)
        {
            Assert.True(
                pair.Gap == RecordedSpawnRoomGap,
                $"{pair.Fixture} slot {pair.Slot}: died in a spawn room at t{pair.Death}, "
                + $"respawned t{pair.Spawn} — {pair.Gap} ticks, not {RecordedSpawnRoomGap}");
        }
    }

    /// <summary>
    /// And the two rules really are different, inside one recording.
    /// </summary>
    /// <remarks>
    /// The control that stops the test above being satisfied by a server whose
    /// respawn time simply <em>is</em> one tick: the same ninety seconds that
    /// hold the spawn-room death also hold two ordinary deaths answered after
    /// the full 150.
    /// </remarks>
    [Fact]
    public void OneRecordingHoldsBothRules()
    {
        const string Fixture = "reference-ctf_truefort-spawnroomdeath.trace";

        var pairs = Pairs().Where(p => p.Fixture == Fixture).ToArray();

        Assert.Contains(pairs, p => p.InRoom && p.Gap == RecordedSpawnRoomGap);
        Assert.Contains(pairs, p => !p.InRoom && p.Gap == RecordedGap);
    }

    /// <summary>
    /// The port answers a death after the same 150 ticks, driven through whole
    /// ticks rather than by reading the counter back.
    /// </summary>
    /// <remarks>
    /// <b>The half the recordings cannot supply.</b> A replay never respawns
    /// anybody by itself — it injects the recording's own <c>PLAYER_SPAWN</c>s
    /// and its <c>RespawnTime</c> is 0 (docs/PARITY.md, "a replay never respawns
    /// anybody by itself") — so the fixtures give the number and this gives the
    /// port's answer to it. Between them the claim is end to end.
    /// <para>
    /// ⚠️ Driven through <see cref="MatchSimulation.Tick"/> and read off
    /// <c>Alive</c>, not off <c>RespawnTimer</c>: the counter reaching zero and
    /// a character existing again are two different events, an alarm stage
    /// apart, and only the second is what a client sees.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortAnswersADeathAfterTheSameOneHundredAndFiftyTicks()
    {
        const string Map = "ctf_truefort";
        var level = MapLoader(Map);
        var world = new CollisionWorld(level);
        var spawnPoints = new SpawnPoints(level);

        var state = default(WorldState);
        state.RespawnTime = RecordedGap;

        // ⚠️ Outside a spawn room, or the fast arm answers in one tick. Asserted,
        // so this cannot quietly start measuring the other rule.
        const double X = 2280;
        const double Y = 200;

        var character = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        character.X = X;
        character.Y = Y;
        character.Hp = 0;

        state.Characters[1] = character;
        state.Alive[1] = true;
        state.AddPlayer(1);
        state.Players[1].Team = Team.Red;
        state.Players[1].Class = PlayerClass.Scout;

        Assert.False(world.MeetsSpawnRoom(character.Box, X, Y),
            "the test position is inside a spawn room");

        var events = default(TickEvents);

        // The death lands in this tick's End Step.
        MatchSimulation.Tick(world, ref state, [], ref events, spawnPoints);

        Assert.True(events.Died[1], "the character did not die");
        Assert.False(state.IsAlive(1));

        var gap = 0;

        for (var tick = 1; tick <= RecordedGap + 5 && !state.IsAlive(1); tick++)
        {
            MatchSimulation.Tick(world, ref state, [], ref events, spawnPoints);
            gap = tick;
        }

        Assert.True(state.IsAlive(1), "the port never respawned the character");
        Assert.Equal(RecordedGap, gap);
    }

    /// <summary>
    /// The control: a death <em>inside</em> a spawn room is answered on the next
    /// tick instead, through the same whole-tick path.
    /// </summary>
    /// <remarks>
    /// Without it the test above passes for a port that ignores the spawn room
    /// entirely — which is what the port did until iteration 43.
    /// </remarks>
    [Fact]
    public void ADeathInASpawnRoomIsAnsweredOnTheNextTick()
    {
        const string Map = "ctf_truefort";
        var level = MapLoader(Map);
        var world = new CollisionWorld(level);
        var spawnPoints = new SpawnPoints(level);

        Assert.True(spawnPoints.TryGet(Team.Red, 0, 0, out var x, out var y));

        var state = default(WorldState);
        state.RespawnTime = RecordedGap;

        var character = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        character.X = x;
        character.Y = y;
        character.Hp = 0;

        state.Characters[1] = character;
        state.Alive[1] = true;
        state.AddPlayer(1);
        state.Players[1].Team = Team.Red;
        state.Players[1].Class = PlayerClass.Scout;

        Assert.True(world.MeetsSpawnRoom(character.Box, x, y),
            "the spawn point is not inside a spawn room");

        var events = default(TickEvents);

        MatchSimulation.Tick(world, ref state, [], ref events, spawnPoints);
        Assert.True(events.Died[1]);

        MatchSimulation.Tick(world, ref state, [], ref events, spawnPoints);

        Assert.True(state.IsAlive(1), "a spawn-room death did not come back on the next tick");
    }
}

using System.Globalization;
using System.Text.Json.Serialization;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Phase 4's tier-1 check: does this port's character update agree with the
/// engine, tick for tick and bit for bit?
/// </summary>
/// <remarks>
/// The fixture is whole trajectories driven inside the <b>running game</b>: a
/// character placed at a state we chose exactly, then stepped through its own
/// <c>Begin Step</c>, <c>Step</c> and <c>End Step</c> events for forty-eight
/// ticks of scripted input, with its position and velocity read back to the
/// last bit. Regenerate with <c>node gg2-samplers/sample-physics.js</c> against a
/// running dedicated server.
/// <para>
/// <b>Why this exists alongside the trace replay.</b> A recorded trace carries
/// position to a fifth of a pixel and velocity to <c>1/8.5</c> px/tick, so a
/// replay never starts on the state the reference was actually in — which means
/// a residual difference of one wire unit cannot be attributed. It is either a
/// wrong sum or a differently-rounded anchor, and the trace cannot tell you
/// which. This can: there is no anchor here, only arithmetic, and the gate is
/// exact equality of the doubles.
/// </para>
/// <para>
/// It is also what caught the thing the trace could not. GM8 does not parse
/// every decimal literal to the IEEE-nearest double — <c>0.6</c>, <c>0.85</c>,
/// <c>1.15</c>, <c>0.65</c> and <c>0.35</c> all land one ULP high — and those
/// are gravity, <c>baseControl</c>, <c>baseFriction</c> and two of the rocket
/// jump's control factors. See <see cref="Gml"/> and docs/SIMULATION.md.
/// </para>
/// </remarks>
public sealed class PhysicsSampleTests
{
    /// <summary>One tick's result, exactly as the engine had it.</summary>
    private sealed record SampleTick
    {
        [JsonPropertyName("x")] public required double X { get; init; }

        [JsonPropertyName("y")] public required double Y { get; init; }

        [JsonPropertyName("hspeed")] public required double HSpeed { get; init; }

        [JsonPropertyName("vspeed")] public required double VSpeed { get; init; }
    }

    private sealed record SampleStart
    {
        [JsonPropertyName("x")] public required double X { get; init; }

        [JsonPropertyName("y")] public required double Y { get; init; }

        [JsonPropertyName("hspeed")] public required double HSpeed { get; init; }

        [JsonPropertyName("vspeed")] public required double VSpeed { get; init; }
    }

    private sealed record SampleScenario
    {
        [JsonPropertyName("map")] public required string Map { get; init; }

        [JsonPropertyName("class")] public required string Class { get; init; }

        [JsonPropertyName("team")] public required int Team { get; init; }

        [JsonPropertyName("start")] public required SampleStart Start { get; init; }

        /// <summary>The <c>keyState</c> byte fed in on each tick.</summary>
        [JsonPropertyName("keys")] public required int[] Keys { get; init; }

        /// <summary><c>aimDirection</c> in wire units on each tick.</summary>
        [JsonPropertyName("aims")] public required int[] Aims { get; init; }

        [JsonPropertyName("ticks")] public required SampleTick[] Ticks { get; init; }

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture,
                $"{Class} on {Map} from ({Start.X}, {Start.Y}) at ({Start.HSpeed}, {Start.VSpeed})");
    }

    private sealed record Document(
        [property: JsonPropertyName("scenarios")] SampleScenario[] Scenarios);

    private static readonly SampleScenario[] Scenarios = Load();

    private static SampleScenario[] Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "physics-samples.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"missing {path}; run gg2-samplers/sample-physics.js", path);

        return System.Text.Json.JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!.Scenarios;
    }

    public static TheoryData<string> MapNames()
    {
        var data = new TheoryData<string>();
        foreach (var map in Scenarios.Select(s => s.Map).Distinct())
            data.Add(map);
        return data;
    }

    private static readonly Dictionary<string, CollisionWorld> Worlds = [];

    private static CollisionWorld WorldFor(string map)
    {
        if (!Worlds.TryGetValue(map, out var world))
            Worlds[map] = world = new CollisionWorld(LevelMap.FromPng(map, ReferenceTree.ReadMap(map)));

        return world;
    }

    /// <summary>
    /// Every scenario reproduces the engine's own doubles, on every tick.
    /// </summary>
    /// <remarks>
    /// Exact equality, deliberately. There is no tolerance that is right here:
    /// the movement solver crawls back out of an obstruction in eighths of a
    /// pixel until a position is free, so a difference far below any sensible
    /// epsilon lands on the other side of a boundary and stays there as a
    /// sixteenth of a pixel. The whole point of phase 4 is that "close" is not a
    /// property this simulation can have.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MapNames))]
    public void EveryScenarioMatchesTheEngineExactly(string map)
    {
        var world = WorldFor(map);
        var compared = 0;

        foreach (var scenario in Scenarios.Where(s => s.Map == map))
        {
            var character = Anchor(scenario);

            for (var tick = 0; tick < scenario.Ticks.Length; tick++)
            {
                character.ApplyInput(new PlayerInput
                {
                    Keys = (PlayerKeys)scenario.Keys[tick],
                    NetAimDirection = (ushort)scenario.Aims[tick],
                    AimDistance = 0,
                });

                CharacterPhysics.Tick(world, ref character);

                var expected = scenario.Ticks[tick];
                compared++;

                if (character.X == expected.X && character.Y == expected.Y
                    && character.HSpeed == expected.HSpeed && character.VSpeed == expected.VSpeed)
                {
                    continue;
                }

                Assert.Fail(
                    $"{scenario} diverged on tick {tick}:\n" +
                    $"  engine x={R(expected.X)} y={R(expected.Y)} hspeed={R(expected.HSpeed)} vspeed={R(expected.VSpeed)}\n" +
                    $"  port   x={R(character.X)} y={R(character.Y)} hspeed={R(character.HSpeed)} vspeed={R(character.VSpeed)}\n" +
                    $"  delta  dx={R(character.X - expected.X)} dy={R(character.Y - expected.Y)} " +
                    $"dhs={R(character.HSpeed - expected.HSpeed)} dvs={R(character.VSpeed - expected.VSpeed)}");
            }
        }

        Assert.True(compared > 0, $"no scenarios for {map}");
    }

    /// <summary>
    /// The fixture has real geometry in it — landings, walls, and jumps.
    /// </summary>
    /// <remarks>
    /// Without this a fixture regenerated from a broken setup could be forty-
    /// eight ticks of a character falling through empty sky on every scenario,
    /// and the test above would pass while proving nothing. A landing is the
    /// interesting case: it is the only one where the movement solver runs.
    /// </remarks>
    [Fact]
    public void TheFixtureExercisesTheMovementSolver()
    {
        var landed = 0;
        var moved = 0;

        foreach (var scenario in Scenarios)
        {
            var ticks = scenario.Ticks;

            // vspeed driven to exactly zero is the floor test in Step firing:
            // free fall never reaches it, because gravity is applied in halves.
            if (ticks.Any(t => t.VSpeed == 0))
                landed++;

            if (ticks.Any(t => Math.Abs(t.X - scenario.Start.X) > 1))
                moved++;
        }

        Assert.True(landed > Scenarios.Length / 4,
            $"only {landed} of {Scenarios.Length} scenarios ever hit a floor");
        Assert.True(moved > Scenarios.Length / 2,
            $"only {moved} of {Scenarios.Length} scenarios moved horizontally at all");
    }

    private static CharacterState Anchor(SampleScenario scenario) => new()
    {
        Class = Enum.Parse<PlayerClass>(scenario.Class),
        Team = (Team)scenario.Team,
        X = scenario.Start.X,
        Y = scenario.Start.Y,
        HSpeed = scenario.Start.HSpeed,
        VSpeed = scenario.Start.VSpeed,
        Hp = 1000,

        // The sampler sets keyState and lastKeyState to the first tick's keys
        // before the loop, so the first tick reports no fresh press. Facing
        // follows the first aim, and _last_xscale is seeded to match it — an
        // anchor that left it at zero would read as "just turned round" and
        // could hand out a free tick of anti-gravity.
        KeyState = (PlayerKeys)scenario.Keys[0],
        LastKeyState = (PlayerKeys)scenario.Keys[0],
        NetAimDirection = (ushort)scenario.Aims[0],
        Facing = Facing(scenario.Aims[0]),
        LastFacing = Facing(scenario.Aims[0]),

        // Both are per-character in the reference and both start at their
        // base — Create.xml:15 and :158, part of CharacterState's "zeroed is
        // not a valid character" list — and a hand-built anchor that left them
        // at 0 stands still and never jumps.
        RunPower = ClassStats.For(Enum.Parse<PlayerClass>(scenario.Class)).RunPower,
        JumpStrength = ClassStats.BaseJumpStrength,
    };

    private static int Facing(int netAim) => (netAim * 360.0 / 65536.0 + 270) % 360 > 180 ? 1 : -1;

    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

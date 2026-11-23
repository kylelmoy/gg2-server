using System.Text.Json;
using System.Text.Json.Serialization;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="Aabb"/> against the engine's own collision primitives.
/// </summary>
/// <remarks>
/// Phase 3 pinned <c>place_free</c> and deliberately stopped. Combat needs two
/// more, and both decide numbers a client can see: <c>distance_to_object</c>
/// carries every splash radius and its damage falloff, and <c>place_meeting</c>
/// decides whether a projectile is touching a character at all.
/// <para>
/// The fixture is a grid of offsets of a real <c>Rocket</c> and a real
/// <c>Shot</c> around a real character in the running game, with <b>both</b>
/// bounding boxes recorded per sample rather than assumed — so each row is a
/// self-contained statement about two rectangles the engine named. Regenerate
/// with a live-game sampler.
/// </para>
/// </remarks>
public sealed class PrimitiveSampleTests
{
    private sealed record Box
    {
        [JsonPropertyName("left")] public required int Left { get; init; }

        [JsonPropertyName("top")] public required int Top { get; init; }

        [JsonPropertyName("right")] public required int Right { get; init; }

        [JsonPropertyName("bottom")] public required int Bottom { get; init; }

        public Aabb ToAabb() => new(Left, Top, Right, Bottom);
    }

    private sealed record Sample
    {
        [JsonPropertyName("dx")] public required int Dx { get; init; }

        [JsonPropertyName("dy")] public required int Dy { get; init; }

        /// <summary>The character's box as the engine reported it for this row.</summary>
        [JsonPropertyName("character")] public required Box Character { get; init; }

        [JsonPropertyName("left")] public required int Left { get; init; }

        [JsonPropertyName("top")] public required int Top { get; init; }

        [JsonPropertyName("right")] public required int Right { get; init; }

        [JsonPropertyName("bottom")] public required int Bottom { get; init; }

        [JsonPropertyName("distance")] public double Distance { get; init; }

        [JsonPropertyName("meeting")] public bool Meeting { get; init; }

        public Aabb Projectile => new(Left, Top, Right, Bottom);
    }

    private sealed record Origin
    {
        [JsonPropertyName("x")] public required int X { get; init; }

        [JsonPropertyName("y")] public required int Y { get; init; }
    }

    private sealed record Document
    {
        /// <summary>The exact integer coordinate the probe character was pinned to.</summary>
        [JsonPropertyName("origin")] public required Origin Origin { get; init; }

        [JsonPropertyName("distance")] public required Dictionary<string, Sample[]> Distance { get; init; }

        [JsonPropertyName("meeting")] public required Dictionary<string, Sample[]> Meeting { get; init; }
    }

    private static Document Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "primitive-samples.json");
        Assert.True(File.Exists(path), $"missing {path}");
        return JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!;
    }

    /// <summary>
    /// <c>distance_to_object</c> agrees with the engine on every sample,
    /// exactly.
    /// </summary>
    /// <remarks>
    /// Exact equality of doubles. Both gaps are integers and the only operation
    /// is a square root, so there is no rounding rule to differ over — a
    /// mismatch here would be a wrong formula, not a wrong last bit.
    /// </remarks>
    [Fact]
    public void DistanceToObjectMatchesTheEngineExactly()
    {
        var document = Load();
        var checkedSamples = 0;

        foreach (var (projectile, samples) in document.Distance)
        {
            foreach (var sample in samples)
            {
                var actual = sample.Character.ToAabb().Distance(sample.Projectile);

                Assert.True(
                    actual == sample.Distance,
                    $"{projectile} at ({sample.Dx}, {sample.Dy}): engine {sample.Distance}, port {actual}");

                checkedSamples++;
            }
        }

        Assert.True(checkedSamples > 700, $"only {checkedSamples} samples");
    }

    /// <summary>
    /// <c>place_meeting</c> agrees with the engine on every sample.
    /// </summary>
    /// <remarks>
    /// <b>The fixture's character boxes are not all the same, and that is the
    /// point.</b> The probe character turned round partway through the run, and
    /// GM8 reports a box two pixels wider on the left for a character facing
    /// left. <c>place_meeting</c> ignores that inflation — it tests the precise
    /// mask — so the rows recorded with the wide box must still answer as if the
    /// box were the narrow one. Narrowing an inflated box back is what
    /// <see cref="MaskBox"/> does, and dropping it makes seven of these fail.
    /// </remarks>
    [Fact]
    public void PlaceMeetingMatchesTheEngine()
    {
        var document = Load();
        var checkedSamples = 0;

        foreach (var (projectile, samples) in document.Meeting)
        {
            foreach (var sample in samples)
            {
                var actual = MaskBox(sample.Character, document.Origin.X).Overlaps(sample.Projectile);

                Assert.True(
                    actual == sample.Meeting,
                    $"{projectile} at ({sample.Dx}, {sample.Dy}): engine {sample.Meeting}, port {actual}");

                checkedSamples++;
            }
        }

        Assert.True(checkedSamples > 700, $"only {checkedSamples} samples");
    }

    /// <summary>
    /// The two primitives disagree about the same character, and they are
    /// supposed to.
    /// </summary>
    /// <remarks>
    /// This is the finding worth a test of its own, because it does not look
    /// like a rule anyone would invent: the same character, at the same
    /// coordinates, in the same tick, is a different distance from a rocket
    /// depending on which way it is facing — while <c>place_meeting</c> against
    /// the same rocket is unmoved. Splash damage therefore depends on the
    /// victim's facing, and a port that "tidies up" the inflated box would be
    /// quietly wrong about every explosion.
    /// </remarks>
    [Fact]
    public void FacingChangesTheSplashDistanceButNotTheOverlap()
    {
        var box = CharacterBox.For(PlayerClass.Scout);

        var facingRight = Aabb.ForCharacter(box, 3000, 600, facing: 1);
        var facingLeft = Aabb.ForCharacter(box, 3000, 600, facing: -1);

        // The rocket the live A/B probe used: box 2979..2986.
        var rocket = new Aabb(2979, 599, 2986, 600);

        Assert.Equal(8, facingRight.Distance(rocket));
        Assert.Equal(6, facingLeft.Distance(rocket));

        // ... and neither is touching it.
        Assert.False(facingRight.Overlaps(rocket));
        Assert.False(facingLeft.Overlaps(rocket));
    }

    /// <summary>
    /// Undoes GM8's facing inflation, so a recorded box can be compared against
    /// the precise mask the overlap primitives actually use.
    /// </summary>
    /// <remarks>
    /// Every class's mask is symmetric about its origin
    /// (docs/SIMULATION.md's table: <c>left == -right</c> for all ten), so with
    /// the character pinned to a known integer coordinate the true left edge is
    /// the right edge mirrored through it. Anything wider is the inflation GM8
    /// reports for <c>image_xscale = -1</c>, and <c>place_meeting</c> does not
    /// use it.
    /// </remarks>
    private static Aabb MaskBox(Box reported, int originX) =>
        new((2 * originX) - reported.Right, reported.Top, reported.Right, reported.Bottom);
}

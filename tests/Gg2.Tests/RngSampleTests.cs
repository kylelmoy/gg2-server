using System.Text.Json;
using System.Text.Json.Serialization;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// <see cref="GmlRandom"/> against the engine's own generator.
/// </summary>
/// <remarks>
/// Phase 5's tier-1 check, and the first one phases 0–4 did not need: weapon
/// fire puts a <em>seed</em> on the wire rather than a result
/// (<c>Scripts/Events/sendEventFireWeapon.gml</c>), so every client re-derives
/// the shot spread with its own copy of GM8's generator. Getting this wrong
/// makes the server and the client disagree about which pellets hit, and makes a
/// recorded trace unreplayable for damage even though the seed is right there in
/// it.
/// <para>
/// The fixture is measured on the running game — the seed chain, exact
/// <c>random(x)</c> bits through the mantissa encoder, and the integer draws.
/// Regenerate with <c>node tools/sample-rng.js</c>.
/// </para>
/// </remarks>
public sealed class RngSampleTests
{
    private sealed record Chain
    {
        [JsonPropertyName("seed")] public required uint Seed { get; init; }

        /// <summary>The seed after each of <c>chainLength</c> draws.</summary>
        [JsonPropertyName("chain")] public required uint[] Values { get; init; }
    }

    private sealed record RandomValue
    {
        [JsonPropertyName("seed")] public required uint Seed { get; init; }

        [JsonPropertyName("arg")] public required double Arg { get; init; }

        /// <summary>What the engine returned, to the last bit.</summary>
        [JsonPropertyName("value")] public required double Value { get; init; }

        /// <summary>The seed the draw left behind.</summary>
        [JsonPropertyName("advanced")] public required uint Advanced { get; init; }
    }

    private sealed record ChooseDraw
    {
        [JsonPropertyName("index")] public required int Index { get; init; }

        [JsonPropertyName("advanced")] public required uint Advanced { get; init; }
    }

    private sealed record IntegerDraws
    {
        [JsonPropertyName("seed")] public required uint Seed { get; init; }

        /// <summary>Keyed by the inclusive ceiling passed to <c>irandom</c>.</summary>
        [JsonPropertyName("irandom")] public required Dictionary<string, int> IRandom { get; init; }

        /// <summary>Keyed by how many arguments <c>choose</c> was given.</summary>
        [JsonPropertyName("choose")] public required Dictionary<string, ChooseDraw> Choose { get; init; }
    }

    private sealed record Document
    {
        [JsonPropertyName("chains")] public required Chain[] Chains { get; init; }

        [JsonPropertyName("values")] public required RandomValue[] Values { get; init; }

        [JsonPropertyName("integers")] public required IntegerDraws[] Integers { get; init; }
    }

    private static Document Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rng-samples.json");
        Assert.True(File.Exists(path), $"missing {path}");
        return JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!;
    }

    /// <summary>
    /// The seed advances exactly as the engine's does, for every sampled seed.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing one. Everything else is a projection of the seed,
    /// so a generator that walks the same chain produces the same everything —
    /// and a generator that does not cannot be patched up downstream.
    /// </remarks>
    [Fact]
    public void SeedChainMatchesTheEngine()
    {
        foreach (var chain in Load().Chains)
        {
            var rng = new GmlRandom(chain.Seed);

            for (var i = 0; i < chain.Values.Length; i++)
            {
                rng.Next(1);
                Assert.Equal(chain.Values[i], rng.Seed);
            }
        }
    }

    /// <summary>
    /// <c>random(x)</c> returns the engine's double, <b>bit for bit</b>.
    /// </summary>
    /// <remarks>
    /// Exact equality, no tolerance: every product involved stays under 2^53 and
    /// the divisor is a power of two, so both sides are doing exact arithmetic
    /// and a difference would be a real one.
    /// </remarks>
    [Fact]
    public void RandomMatchesTheEngineExactly()
    {
        foreach (var sample in Load().Values)
        {
            var rng = new GmlRandom(sample.Seed);
            var value = rng.Next(sample.Arg);

            Assert.Equal(sample.Value, value);
            Assert.Equal(sample.Advanced, rng.Seed);
        }
    }

    /// <summary>
    /// <c>irandom(n)</c> and <c>choose(...)</c> pick what the engine picks, and
    /// cost what the engine costs.
    /// </summary>
    /// <remarks>
    /// The cost matters as much as the value. <c>Shot</c>'s Create draws a
    /// <c>choose(0, 0, 1)</c> inside the same seeded window as the pellet
    /// spread, so a <c>choose</c> that advanced the seed a different number of
    /// times would leave every pellet after the first somewhere else.
    /// </remarks>
    [Fact]
    public void IntegerDrawsMatchTheEngine()
    {
        foreach (var sample in Load().Integers)
        {
            foreach (var (ceiling, expected) in sample.IRandom)
            {
                var rng = new GmlRandom(sample.Seed);
                Assert.Equal(expected, rng.NextInclusive(int.Parse(ceiling)));
            }

            foreach (var (count, expected) in sample.Choose)
            {
                var rng = new GmlRandom(sample.Seed);

                Assert.Equal(expected.Index, rng.ChooseIndex(int.Parse(count)));
                Assert.Equal(expected.Advanced, rng.Seed);
            }
        }
    }
}

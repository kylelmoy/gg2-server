namespace GG2.Protocol;

/// <summary>
/// Game Maker 8's random number generator, reproduced exactly.
/// </summary>
/// <remarks>
/// <b>This is contract, not incidental, and it is the one place
/// docs/SIMULATION.md's "server-side RNG is free" does not apply.</b> A weapon's
/// <c>User Event 1</c> draws a seed, puts <em>the seed</em> on the wire
/// (<c>Scripts/Events/sendEventFireWeapon.gml</c>) and then both machines run
/// <c>random_set_seed(seed)</c> and derive the shot spread themselves
/// (<c>doEventFireWeapon.gml</c>). The server broadcasts no pellet positions at
/// all, so which pellets hit is decided by this generator running identically on
/// the server and on every client.
/// <para>
/// Free, and deliberately not routed through here: spawn point choice, chat
/// bubbles, particles, and the <c>Minigun</c> and <c>Blade</c>, which call
/// <c>randomize()</c> and fire without a seed or a
/// <c>sendEventFireWeapon</c> — their spread genuinely differs on every machine
/// and the server is simply authoritative.
/// </para>
/// <para>
/// A <c>struct</c> with no state but the seed, so a scoped generator is a local
/// and the reference's <c>random_get_seed</c> / <c>random_set_seed</c> save and
/// restore dance disappears: the outer stream is never touched.
/// </para>
/// <para>
/// Measured against the running game and checked in as
/// <c>tests/GG2.Tests/Fixtures/rng-samples.json</c>; regenerate with
/// <c>node gg2-samplers/sample-rng.js</c>.
/// </para>
/// </remarks>
public struct GmlRandom
{
    /// <summary>
    /// The multiplier in GM8's linear congruential generator.
    /// </summary>
    /// <remarks>
    /// <c>134775813</c>, which is Borland's: Game Maker 8 is a Delphi program
    /// and this is Delphi's own <c>Random</c>. Confirmed to the bit over 15
    /// seeds × 8 advances against the live engine.
    /// </remarks>
    private const uint Multiplier = 0x08088405;

    /// <summary>
    /// The scale from a 32-bit seed to a real in <c>[0, 1)</c>: exactly 2^32.
    /// </summary>
    /// <remarks>
    /// A power of two, so dividing by it is exact and never the source of a
    /// rounding difference. Every product below stays under 2^53 for the
    /// arguments GG2 passes, so the whole draw is exact arithmetic.
    /// </remarks>
    private const double Scale = 4294967296.0;

    private uint _seed;

    /// <summary>A generator seeded as <c>random_set_seed(seed)</c> would.</summary>
    public GmlRandom(uint seed) => _seed = seed;

    /// <summary>GML <c>random_get_seed()</c>.</summary>
    /// <remarks>
    /// GML has one numeric type, so the game reports this as a <em>signed</em>
    /// real and a seed above 2^31 reads back negative. That is a display
    /// artefact of the reference's own numeric type, not a different value.
    /// </remarks>
    public readonly uint Seed => _seed;

    /// <summary>
    /// Steps the generator and returns the new seed.
    /// </summary>
    /// <remarks>
    /// The seed advances <b>before</b> the value is taken, which is why
    /// <c>random_set_seed(s)</c> immediately followed by <c>random_get_seed()</c>
    /// returns <c>s</c> but the first draw does not use it. Unsigned arithmetic
    /// wraps at 2^32 in C# by default, which is the modulus.
    /// </remarks>
    private uint Advance() => _seed = (_seed * Multiplier) + 1;

    /// <summary>GML <c>random(x)</c>: a real in <c>[0, x)</c>.</summary>
    public double Next(double range) => range * Advance() / Scale;

    /// <summary>
    /// GML <c>irandom(n)</c>: an integer in <c>[0, n]</c>, <b>inclusive</b>.
    /// </summary>
    public int NextInclusive(int max) => (int)((max + 1.0) * Advance() / Scale);

    /// <summary>
    /// The index GML <c>choose()</c> would pick from <paramref name="count"/>
    /// arguments.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed, because it decides how far the stream moves
    /// as well as which value comes out: <c>choose</c> costs exactly one advance,
    /// the same as one <c>random()</c>. <c>Shot</c>'s Create runs
    /// <c>choose(0, 0, 1)</c> inside the seeded window, so a wrong cost here
    /// would desync every pellet after the first.
    /// </remarks>
    public int ChooseIndex(int count) => (int)(count * (double)Advance() / Scale);
}

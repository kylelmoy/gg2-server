using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The demoman's mine jump, measured — the manoeuvre a
/// <see cref="NavEdgeType.StickyJump"/> edge is an arc of.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> `gg2-nav-gen` built the sticky-jump edge fan on a
/// <em>derived</em> impulse (<c>NAV_SJ_IMPULSE = 10</c>, giving
/// <c>NAV_JUMP_V0 + NAV_SJ_IMPULSE = 18.3</c> px/tick and a claimed ~310 px
/// apex), flagged as the one thing to measure before trusting the edges to a
/// bot.
/// <para>
/// <b>It does not need the live game.</b> This port's mine and explosion
/// physics are measured exact against the engine (phase 5, nine oracles), so the
/// manoeuvre can be flown here and read off directly.
/// </para>
/// <para>
/// <b>The measurement, and it is the same on every map tried:</b> the launch is
/// <b>-14.460 px/tick</b> and the unobstructed apex is <b>193.6 px</b>, not 310.
/// The generator's envelope is roughly <b>60% too generous</b>.
/// </para>
/// </remarks>
public sealed class MineJumpTests
{
    /// <summary>Ticks between placing the mine and jumping on it.</summary>
    /// <remarks>
    /// <c>NAV_SJ_SETUP_TICKS</c> is 5; eight is used here so the mine has
    /// certainly settled and the measurement is of the manoeuvre rather than of
    /// the timing. The launch is identical at both.
    /// </remarks>
    private const int Setup = 8;

    /// <summary>Straight down, in GM8 degrees.</summary>
    private const double Down = 270.0;

    private sealed class Script(Func<int, PlayerInput> f) : IInputSource
    {
        private int _tick;

        public PlayerInput Poll(in WorldView world, int slot, int tick) => f(_tick++);
    }

    private static (double Launch, double Rise, double Damage) Fly(string map)
    {
        var world = ScenarioWorld.Load(map, 1, ReferenceTree.MapsDirectory, null);

        // A real spawn point, not a coordinate: an arbitrary point can land the
        // demoman inside terrain, sitting at terminal velocity going nowhere.
        // Same lesson as gg2-rl/docs/ENV.md's sampled placement.
        var slot = world.PlaceAtSpawn(PlayerClass.Demoman, Team.Red, 0);

        world.Drive(slot, null);
        world.Step(60);

        var startY = world.State.Characters[slot].Y;
        var hpBefore = world.State.Characters[slot].Hp;

        world.Drive(slot, new Script(t => t switch
        {
            0 => PlayerInput.FromAim(PlayerKeys.Attack, Down, 100),

            // Jump and detonate on ONE tick, and that is the manoeuvre.
            // Character.events/Begin Step.xml runs its jump branch (line 108)
            // BEFORE its right-click branch (line 137), so `vspeed =
            // -jumpStrength` is already in place when the blast's motion_add
            // lands on top of it. Detonating a tick later would waste the jump.
            Setup => PlayerInput.FromAim(PlayerKeys.Jump | PlayerKeys.Special, Down, 100),
            _ => PlayerInput.FromAim(PlayerKeys.None, Down, 100),
        }));

        var peak = startY;
        var launch = 0.0;

        for (var i = 0; i < 160 && world.State.IsAlive(slot); i++)
        {
            world.Step();

            if (!world.State.IsAlive(slot))
                break;

            ref readonly var c = ref world.State.Characters[slot];

            if (i == Setup)
                launch = c.VSpeed;

            peak = Math.Min(peak, c.Y);
        }

        var hpNow = world.State.IsAlive(slot) ? world.State.Characters[slot].Hp : 0;

        return (launch, startY - peak, hpBefore - hpNow);
    }

    /// <summary>
    /// <b>The launch is -14.460 px/tick, and it is the same on every map.</b>
    /// </summary>
    /// <remarks>
    /// A mine fired straight down settles at the demoman's feet, so the geometry
    /// that decides the impulse is the character's own and not the map's. Four
    /// maps agreeing to three decimals is what says so.
    /// <para>
    /// <b>Pinned exactly, because a generator is costing arcs against it.</b>
    /// If this moves, `gg2-nav-gen`'s sticky-jump envelope is stale and every
    /// type-7 edge it has emitted needs regenerating — that is the whole reason
    /// the number is written down rather than merely bounded.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort")]
    [InlineData("koth_valley")]
    [InlineData("ctf_2dfort")]
    [InlineData("gg_debug")]
    public void TheLaunchIsTheSameWhereverTheDemomanStands(string map)
    {
        var (launch, _, _) = Fly(map);

        Assert.Equal(-14.46, launch, 2);
    }

    /// <summary>
    /// <b>The unobstructed apex is 193.6 px, not the ~310 px the generator
    /// assumed.</b>
    /// </summary>
    /// <remarks>
    /// Computed from the launch rather than measured off a map, because every
    /// map tried has a ceiling or a slope inside 200 px of a spawn and the
    /// measured rise there (20.9 to 68.8 px) is geometry rather than the
    /// manoeuvre. Under <c>moveStatus</c> 1 the gravity is
    /// <see cref="ClassStats.RocketJumpGravity"/> and the apex is
    /// <c>v² / 2g</c> — the same arithmetic <c>RocketModel</c> uses.
    /// <para>
    /// <b>This is a finding for `gg2-nav-gen`, not a bug here.</b> Its
    /// <c>NAV_SJ_IMPULSE = 10</c> was derived, it says so, and it asked for this
    /// number before its edges were trusted to a bot. The answer is that the
    /// envelope is about 60% too generous, so some fraction of its 6,497 edges
    /// are arcs a demoman cannot fly.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheApexIsNowhereNearTheGeneratorsDerivedEnvelope()
    {
        var (launch, _, _) = Fly("ctf_truefort");

        var apex = launch * launch / (2 * ClassStats.RocketJumpGravity);

        Assert.Equal(193.6, apex, 1);

        // What the generator assumed, from NAV_JUMP_V0 + NAV_SJ_IMPULSE = 18.3.
        const double Assumed = 18.3 * 18.3 / (2 * 0.54);

        Assert.True(apex < Assumed * 0.7,
            $"the measured apex {apex:F1} is no longer far below the assumed {Assumed:F1}");
    }

    /// <summary>
    /// The self-damage is 25, which is the one constant the generator derived
    /// correctly.
    /// </summary>
    /// <remarks>
    /// <c>NAV_SJ_DAMAGE = 25</c>, and <c>45 * 5/9</c> is where it comes from.
    /// Asserted beside the impulse so that "the generator's numbers are wrong"
    /// cannot be read more broadly than it should be: one of the two is right.
    /// </remarks>
    [Fact]
    public void TheSelfDamageIsTwentyFive()
    {
        var (_, _, damage) = Fly("ctf_truefort");

        Assert.Equal(25, damage, 1);
    }
}

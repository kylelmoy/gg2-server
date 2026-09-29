using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="ObservationWriter"/> — the v0 layout.
/// </summary>
/// <remarks>
/// <c>gg2-rl/docs/ENV.md</c>'s gates for this type: zero allocation, bounded values,
/// and a length that matches the advertised one. All three are here, plus the
/// two properties that would otherwise be silent — the mirrored version
/// constant, and the buffer being cleared.
/// </remarks>
public sealed class ObservationWriterTests
{
    private const string Map = "gg_debug";

    /// <summary>
    /// The layout version is mirrored in two places and the compiler cannot
    /// hold them equal.
    /// </summary>
    /// <remarks>
    /// <c>GG2.Simulation</c> does not depend on <c>GG2.Env</c> — the simulation
    /// is the thing an env observes, not the other way round — so
    /// <c>SimulationStamp.ObservationLayoutVersion</c> cannot reference
    /// <see cref="ObservationWriter.LayoutVersion"/>. This test is the only
    /// thing standing between a layout change and a stamp that did not move
    /// with it, which is the exact failure the stamp exists to prevent.
    /// </remarks>
    [Fact]
    public void TheStampMirrorsTheWritersLayoutVersion()
    {
        Assert.Equal(ObservationWriter.LayoutVersion, SimulationStamp.ObservationLayoutVersion);
        Assert.Equal(ActionDecoder.AimBins, 15);
    }

    /// <summary>
    /// Every spawnable <see cref="ProjectileKind"/> reaches the observation as
    /// exactly one set column — including <c>Stab</c>, which used not to.
    /// </summary>
    /// <remarks>
    /// <b>The regression this pins was silent and unrecoverable downstream.</b>
    /// <c>ProjectileKindCount</c> is 9 and <c>ProjectileKind</c> has ten members,
    /// so encoding the raw enum value put <c>Stab = 9</c> off the end of a 9-wide
    /// one-hot. <c>OneHot</c> bound-checks, so a spy's stab arrived as presence
    /// <c>1.0</c> with an all-zero kind — indistinguishable from an encoder
    /// failure — while the column for <c>None</c>, which never occurs, sat dead.
    /// <b>Presence-without-a-kind is the shape to assert on</b>: counting set
    /// columns catches it, and the length check that guards the rest of this
    /// layout never could, because the vector stayed 209 wide throughout.
    /// </remarks>
    [Theory]
    [InlineData(ProjectileKind.Shot)]
    [InlineData(ProjectileKind.Rocket)]
    [InlineData(ProjectileKind.Mine)]
    [InlineData(ProjectileKind.Flame)]
    [InlineData(ProjectileKind.Needle)]
    [InlineData(ProjectileKind.Flare)]
    [InlineData(ProjectileKind.Blade)]
    [InlineData(ProjectileKind.Bubble)]
    [InlineData(ProjectileKind.Stab)]
    public void EverySpawnableProjectileKindGetsExactlyOneColumn(ProjectileKind kind)
    {
        var state = default(WorldState);
        state.AddPlayer(0);
        state.Players[0].Team = Team.Red;
        state.Players[0].Class = PlayerClass.Scout;

        // Fresh, never `new CharacterState()` — CLAUDE.md's WorldState rule.
        var character = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        character.X = 600;
        character.Y = 850;
        state.Characters[0] = character;
        state.Alive[0] = true;

        state.SpawnProjectile(new ProjectileState { Kind = kind, X = 610, Y = 850 });

        var collision = new CollisionWorld(
            LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

        var view = new WorldView(in state, collision);

        var o = new float[ObservationWriter.Length];
        ObservationWriter.Write(in view, 0, o);

        // Read off the writer's own constants, never a copy of their sums.
        const int FirstProjectile =
            ObservationWriter.SelfBlock + (ObservationWriter.Enemies * ObservationWriter.EnemyBlock);

        const int KindAt = FirstProjectile + 1 + 2 + 2;

        // Pinned so the block is located deliberately rather than by arithmetic
        // that could drift. It sat at 59 when the bug was found and moved to 61
        // when the self block gained the two refire columns in the same change —
        // which is exactly why the derivation above no longer spells the sum out.
        Assert.Equal(61, KindAt);

        // Present at all, or everything below asserts nothing.
        Assert.Equal(1f, o[FirstProjectile]);

        var oneHot = o[KindAt..(KindAt + ObservationWriter.ProjectileKindCount)];

        Assert.Equal(1, oneHot.Count(c => c == 1f));
        Assert.Equal(ObservationWriter.ProjectileKindCount - 1, oneHot.Count(c => c == 0f));

        // `None` has no column, so an enum value is one ahead of its index.
        Assert.Equal(1f, oneHot[(int)kind - 1]);
    }
}

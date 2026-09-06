using Gg2.Bots;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The one place that owns the checked-in checkpoint.
/// </summary>
/// <remarks>
/// <b><c>ppo_final.npz</c>, checked in from `gg2-rl`.</b> It carries
/// <b>this build's</b> <see cref="SimulationStamp.Uuid"/>, so it takes
/// <c>PolicyNetwork.Load</c>'s ordinary accept path with no override of any kind.
/// <para>
/// <b>It is a FIXTURE, not a policy anyone plays.</b> Its weights are an
/// untrained He-normal draw — real parameters and a real numpy oracle for
/// <c>PolicyNetworkTests</c>'s forward-pass comparison, and nothing more. Do not
/// read a behavioural result off it: an untrained network emits near-uniform
/// noise, and any test that asserted a pinned action or a quality threshold
/// against it would be measuring the draw.
/// </para>
/// <para>
/// ⚠️ <b>This type used to be built on the opposite premise and the inversion is
/// worth knowing about.</b> Until 2026-09-03 the shipped checkpoint was trained
/// against a <em>different</em> simulation, was refused by <c>Load</c>, and this
/// class existed mainly to <c>Mint()</c> a temporary re-stamped copy so that the
/// accept path could be tested at all. The layout v2 bump retired that trained
/// checkpoint, `gg2-rl` minted a fixture against this build, and the whole
/// apparatus — the minted copy, the hard-coded trained stamp, the test asserting
/// the shipped file is refused — became dead weight describing a state that no
/// longer existed. It was deleted rather than updated.
/// </para>
/// <para>
/// <b>A stale fixture is loud on its own now, which is why no guard replaces
/// it.</b> The old hard-coded stamp existed so that swapping the file re-opened
/// the question. <see cref="Load"/> no longer passes
/// <c>ignoreSimulationStamp</c>, so a fixture that stops matching this build
/// fails in <c>PolicyNetwork.Load</c> itself, naming both stamps — a better
/// signal than the constant was, and one that cannot drift out of date.
/// </para>
/// </remarks>
internal static class PolicyFixture
{
    /// <summary>The checked-in checkpoint, exactly as `gg2-rl` handed it over.</summary>
    internal static string Checkpoint =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "policy", "ppo_final.npz");

    /// <summary>
    /// A real <c>gg2-rl</c> checkpoint carrying an EIGHTH metadata field.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Here to prove this build ignores a field it does not yet read.</b>
    /// <c>gg2-rl</c> writes the class set a checkpoint was trained on as
    /// <c>meta[7]</c> from 2026-09-06 and has migrated its existing checkpoints to
    /// carry it; <see cref="PolicyNetwork.Load"/> refuses fewer than seven fields
    /// and reads seven by index, so an eighth passes through. That tolerance is
    /// what lets their side ship before this side reads the field, and until this
    /// file arrived nothing gated it.
    /// <para>
    /// ⚠️ <b>Real <c>Policy.save</c> output, and that is the whole point.</b> The
    /// metadata is a pickled object array, so a synthetic eight-field checkpoint
    /// would exercise a pickle written by this repository rather than one the
    /// trainer writes — and would pass whether or not the two agree. This one came
    /// off a 12,000-iteration run.
    /// </para>
    /// <para>
    /// ⚠️ <b>Its class set is <c>ENGINEER,SPY,PYRO</c> — indices 5, 7, 8 —
    /// deliberately.</b> That is non-prefix, non-contiguous and a different length
    /// from the four <c>PolicyController.TrainedClasses</c> hardcodes, so when the
    /// field IS read, an implementation that assumes "the first N", a fixed count
    /// or a contiguous range fails here and nowhere else.
    /// </para>
    /// </remarks>
    internal static string EightFieldCheckpoint =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "policy", "rest3_s1_iter12000.npz");

    /// <summary>
    /// A real <c>gg2-rl</c> checkpoint whose class set differs from
    /// <see cref="PolicyNetwork.DefaultTrainedClasses"/> by exactly two members.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>THE REGRESSION GATE for the whole per-checkpoint class set.</b> Its
    /// set is <c>SCOUT,SOLDIER,SNIPER,HEAVY</c>; the default is
    /// <c>SCOUT,SOLDIER,DEMOMAN,HEAVY</c>. Under the compile-time static this file
    /// would have DRIVEN its untrained demoman and REFUSED its trained sniper —
    /// the training lane measured those two cells at mirror 36.08% and 89.08%
    /// against a 50% null — with no stamp moving and no test failing. A test that
    /// loads this and asserts <c>IsTrained(Sniper)</c> is the one that would have
    /// caught it.
    /// </remarks>
    internal static string SniperTrainedCheckpoint =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "policy", "control_s0_iter12000.npz");

    /// <summary>
    /// A real <c>gg2-rl</c> checkpoint whose class set EQUALS the default.
    /// </summary>
    /// <remarks>
    /// <b>The no-op control.</b> Reading the field must give exactly what
    /// assuming the default gave, so if this one's behaviour ever differs from
    /// <see cref="PolicyNetwork.DefaultTrainedClasses"/> the change is wrong
    /// rather than the fixture. It is the arm that says the mechanism did not move
    /// anything it was not supposed to.
    /// </remarks>
    internal static string DefaultSetCheckpoint =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "policy", "combat4_s2_iter12000.npz");

    /// <summary>The fixture's directory, which also holds the numpy oracle.</summary>
    internal static string Directory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "policy");

    /// <summary>
    /// The fixture as a network, through the ordinary gate.
    /// </summary>
    /// <remarks>
    /// <b>No <c>ignoreSimulationStamp</c>.</b> Every caller now goes through the
    /// same refusal <c>EnvServer.LoadPolicy</c> performs, so the suite exercises
    /// the guard rather than routinely bypassing it — which is how a guard stops
    /// being one.
    /// </remarks>
    internal static PolicyNetwork Load() => PolicyNetwork.Load(Checkpoint);
}

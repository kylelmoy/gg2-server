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

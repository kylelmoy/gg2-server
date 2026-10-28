using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// A <see cref="HybridBot"/> that only drives while its bot is on one team; on
/// the other the bot is its bare <see cref="ScriptedBot"/>.
/// </summary>
/// <remarks>
/// What makes a live game "policy red against scripted blue", the shape every
/// gg2-sim match number is taken in. <b>It wraps the hybrid rather than living in
/// it</b>, so <see cref="HybridBot"/> — which gg2-sim is pinned against — is
/// unchanged.
/// <para>
/// <b>The team is read every tick, not at creation</b>: autobalance, a scramble
/// and the <c>team</c> verb all move a bot mid-life.
/// </para>
/// <para>
/// <b>The scripted layer is polled exactly once a tick on either side</b> —
/// through the hybrid, which polls it every tick whether or not it drives, or
/// directly. Commands always go through the hybrid, which adds nothing to the
/// scripted layer's own but its handover bubbles.
/// </para>
/// <para>
/// ⚠️ <b>A dead character is polled through the hybrid on either team</b>, and
/// that is what keeps a team change clean. A hybrid polled on a dead character
/// releases the wheel and returns the scripted input unchanged, and a team
/// change always kills (<c>GameServer.ApplyChangeTeam</c>). So a bot leaving the
/// gated team is released on its way out instead of keeping a stale
/// <see cref="HybridBot.MicroDrove"/> that <see cref="HumanizedBot"/> reads, and
/// one joining it starts with nobody holding the wheel. If a team change ever
/// stops killing, this has to release some other way.
/// </para>
/// </remarks>
public sealed class TeamGatedBot : IInputSource, ICommandSource, IWrapsScriptedBot
{
    public TeamGatedBot(HybridBot hybrid, Team team)
    {
        ArgumentNullException.ThrowIfNull(hybrid);

        if (team is not (Team.Red or Team.Blue))
            throw new ArgumentOutOfRangeException(nameof(team), team, "a brain is gated to red or blue");

        Hybrid = hybrid;
        Team = team;
    }

    /// <summary>The gate on a slot's source, through any output filter around it.</summary>
    public static TeamGatedBot? Of(IInputSource? source) => source switch
    {
        TeamGatedBot gated => gated,
        HumanizedBot humanized => Of(humanized.Inner),
        _ => null,
    };

    public HybridBot Hybrid { get; }

    /// <summary>The team whose bots the hybrid drives.</summary>
    public Team Team { get; }

    ScriptedBot IWrapsScriptedBot.Scripted => Hybrid.ScriptedBot;

    public PlayerInput Poll(in WorldView world, int slot, int tick) =>
        world.Player(slot).Team == Team || !world.IsAlive(slot)
            ? Hybrid.Poll(in world, slot, tick)
            : Hybrid.ScriptedBot.Poll(in world, slot, tick);

    public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands) =>
        Hybrid.PollCommands(in world, slot, tick, ref commands);
}

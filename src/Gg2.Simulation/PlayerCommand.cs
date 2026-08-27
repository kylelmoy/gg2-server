using System.Runtime.CompilerServices;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The things a player can do that are not a bit in the input byte.
/// </summary>
/// <remarks>
/// ⚠️ <b>Building a sentry is a client command, not a keypress</b>
/// (<c>processClientCommands.gml</c>'s <c>BUILD_SENTRY</c> case, extracted into
/// <c>serverBuildSentry.gml</c>) — and so are the zoom, the class and team
/// changes and <c>DROP_INTEL</c>. None of them flows through
/// <see cref="MatchSimulation.Tick"/> at all, so none of them can be expressed
/// as a <see cref="PlayerInput"/>.
/// <para>
/// The reference's bots reach them the same way this enum does: by naming the
/// server-side script the socket path calls.
/// <c>Scripts/Bots/botServerActions.gml</c> — "Zoom, build and eat are client
/// <em>commands</em> rather than bits in the input byte, so a bot performs them
/// by calling the same server-side scripts the network path calls …  each of
/// which re-checks its own preconditions." That re-check is the contract here
/// too: a command whose preconditions fail is silence, never an error.
/// </para>
/// <para>
/// The set is exactly the commands this port executes that change the world.
/// <c>CHAT_BUBBLE</c> and <c>PLAYER_CHANGENAME</c> are deliberately absent —
/// they are presentation, and a bot's name is settled when it is added.
/// <c>serverEatSandvich</c> is absent because the port has no sandvich.
/// </para>
/// </remarks>
public enum PlayerCommandKind : byte
{
    /// <summary>The default, and never executed.</summary>
    None = 0,

    /// <summary><c>BUILD_SENTRY</c>. Its own preconditions; refused in silence.</summary>
    BuildSentry,

    /// <summary><c>DESTROY_SENTRY</c>. Broadcasts nothing — clients notice by the subobject bit.</summary>
    DestroySentry,

    /// <summary><c>TOGGLE_ZOOM</c>. A live sniper only.</summary>
    ToggleZoom,

    /// <summary><c>PLAYER_CHANGECLASS</c>; the argument is a <see cref="PlayerClass"/>.</summary>
    ChangeClass,

    /// <summary><c>PLAYER_CHANGETEAM</c>; the argument is a <see cref="Team"/>.</summary>
    ChangeTeam,

    /// <summary><c>DROP_INTEL</c>. Unlike the death drop, this one is broadcast.</summary>
    DropIntel,
}

/// <summary>One command and its one byte of argument.</summary>
/// <remarks>
/// One byte because every command in the set carries at most one, and both that
/// carry anything carry an enum the wire sends as a <c>ubyte</c> — so the
/// payload here is byte-for-byte what the socket path reads out of the packet.
/// </remarks>
public readonly record struct PlayerCommand(PlayerCommandKind Kind, byte Argument)
{
    public static PlayerCommand BuildSentry() => new(PlayerCommandKind.BuildSentry, 0);

    public static PlayerCommand DestroySentry() => new(PlayerCommandKind.DestroySentry, 0);

    public static PlayerCommand ToggleZoom() => new(PlayerCommandKind.ToggleZoom, 0);

    public static PlayerCommand DropIntel() => new(PlayerCommandKind.DropIntel, 0);

    public static PlayerCommand ChangeClass(PlayerClass playerClass) =>
        new(PlayerCommandKind.ChangeClass, (byte)playerClass);

    public static PlayerCommand ChangeTeam(Team team) =>
        new(PlayerCommandKind.ChangeTeam, (byte)team);

    /// <summary>The argument read as a class. Meaningless for any other kind.</summary>
    public PlayerClass AsClass => (PlayerClass)Argument;

    /// <summary>The argument read as a team. Meaningless for any other kind.</summary>
    public Team AsTeam => (Team)Argument;
}

/// <summary>
/// Storage for one tick's worth of commands from one player.
/// </summary>
/// <remarks>
/// Inline for the reason every buffer in this project is
/// (<see cref="SlotBuffer{T}"/>): a source is polled every tick for every bot,
/// and a <c>List&lt;T&gt;</c> here would be an allocation on the steady-state
/// path that docs/BOTAI.md item 1 rules out.
/// <para>
/// <b>The capacity is the flood limit, on purpose.</b>
/// <c>processClientCommands.gml</c> completes at most
/// <see cref="ClientCommands.PerTickLimit"/> commands per player per tick and
/// leaves the rest buffered; a bot gets the same budget, enforced by there
/// being nowhere to put an eleventh. <see cref="Push"/> refuses rather than
/// throwing, because a source that asks for too much should be clipped exactly
/// as a client flooding the socket is — not killed.
/// </para>
/// </remarks>
public struct PlayerCommandBuffer
{
    [InlineArray(ClientCommands.PerTickLimit)]
    private struct Slots
    {
        private PlayerCommand _element0;
    }

    private Slots _commands;
    private int _count;

    /// <summary>How many commands this tick, at most <see cref="Capacity"/>.</summary>
    public readonly int Count => _count;

    public const int Capacity = ClientCommands.PerTickLimit;

    public readonly PlayerCommand this[int index] =>
        (uint)index < (uint)_count
            ? _commands[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Appends a command, or refuses when the tick's budget is spent.</summary>
    /// <returns>Whether it was taken.</returns>
    public bool Push(PlayerCommand command)
    {
        if (_count >= Capacity)
            return false;

        _commands[_count++] = command;
        return true;
    }

    /// <summary>Empties the buffer so it can be reused for the next slot.</summary>
    public void Clear() => _count = 0;
}

/// <summary>
/// A source that also issues the commands a key cannot express.
/// </summary>
/// <remarks>
/// Optional, and separate from <see cref="IInputSource"/>, because most sources
/// never need it: a network client's commands arrive on its socket, and a bot
/// that only walks and shoots issues none. A source implements this when it
/// wants to build, zoom, drop the intel or change what it is playing.
/// <para>
/// Polled on the same tick and in the same pass as
/// <see cref="IInputSource.Poll"/>, so the two see the same world — which is
/// what <c>botServerActions.gml</c> gets by being called from inside the bot's
/// own update.
/// </para>
/// </remarks>
public interface ICommandSource
{
    /// <summary>
    /// Fills <paramref name="commands"/> with this slot's commands for this tick.
    /// </summary>
    /// <param name="commands">
    /// Already cleared. Push into it; anything past
    /// <see cref="PlayerCommandBuffer.Capacity"/> is refused.
    /// </param>
    void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands);
}

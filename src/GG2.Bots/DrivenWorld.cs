using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// A <see cref="WorldState"/> with an <see cref="IInputSource"/> per slot and
/// the loop that feeds it — the half of a bot-driven world that
/// <see cref="HeadlessMatch"/> and <see cref="ScenarioWorld"/> share.
/// </summary>
/// <remarks>
/// One <see cref="Advance"/> is the server's tick without the server: the
/// commands that are not keys run first, every slot is polled for its keys,
/// then <see cref="MatchSimulation.Tick"/>. ⚠️ <b>That order is
/// <c>GameServer.Tick</c>'s</b> — <c>ServiceBots</c> before
/// <c>GatherInputs</c> — so a bot that asked to zoom decides its keys from a
/// view in which it is zoomed, here as there. Until 2026-09-03 the two drivers
/// disagreed on the order, and each dispatched only the command kinds it
/// expected to see.
/// <para>
/// ⚠️ <b>Every <see cref="PlayerCommandKind"/> is dispatched, against the same
/// simulation methods <c>GameServer.Execute</c> reaches.</b> A driver that
/// handles only the kinds it expects is measuring a different bot from the one
/// on the server: <c>ScriptedBot</c> pushes <see cref="PlayerCommand.ToggleZoom"/>,
/// and a headless sniper never zoomed. The broadcast half stays in the server;
/// nothing here is on the wire.
/// </para>
/// </remarks>
public abstract class DrivenWorld
{
    private readonly IInputSource?[] _sources = new IInputSource?[WorldState.MaxSlots];
    private readonly PlayerInput[] _inputs = new PlayerInput[WorldState.MaxSlots];
    private PlayerCommandBuffer _commands;
    private WorldState _state;
    private TickEvents _events;
    private int _slots;

    protected DrivenWorld(LevelMap map, CollisionWorld collision, NavGraph? nav)
    {
        Map = map;
        Collision = collision;
        Nav = nav;
    }

    public LevelMap Map { get; }

    public CollisionWorld Collision { get; }

    /// <summary>The nav graph bots route on, or null when the map has none.</summary>
    public NavGraph? Nav { get; }

    public ref WorldState State => ref _state;

    /// <summary>What the last <see cref="Advance"/> produced.</summary>
    public ref TickEvents Events => ref _events;

    /// <summary>Ticks advanced so far, and the frame the next poll is stamped with.</summary>
    public int Tick { get; private set; }

    /// <summary>Slots in use, slot 0 included.</summary>
    public int SlotCount => _slots;

    /// <summary>Sets, or with null clears, what drives <paramref name="slot"/>.</summary>
    public void Drive(int slot, IInputSource? source) => _sources[slot] = source;

    public IInputSource? SourceOf(int slot) =>
        slot >= 0 && slot < _sources.Length ? _sources[slot] : null;

    /// <summary>Claims the next roster slot, or throws when the world is full.</summary>
    protected int AddSlot()
    {
        if (_slots >= WorldState.MaxSlots)
            throw new InvalidOperationException($"a driven world holds {WorldState.MaxSlots} slots");

        return _slots++;
    }

    /// <summary>
    /// Back to nothing: no state, no events, no sources, no slots, tick zero.
    /// The subclass rebuilds the world from there.
    /// </summary>
    protected void Clear()
    {
        _state = default;
        _events = default;
        Array.Clear(_sources);
        _slots = 0;
        Tick = 0;
    }

    /// <summary>One tick: commands, then keys, then the simulation.</summary>
    /// <param name="spawnPoints">
    /// Where a respawn lands, or null for a world whose characters never
    /// respawn on their own.
    /// </param>
    protected void Advance(SpawnPoints? spawnPoints)
    {
        RunCommands();

        for (var slot = 0; slot < _slots; slot++)
        {
            _inputs[slot] = _sources[slot] is { } source
                ? source.Poll(new WorldView(in _state, Collision), slot, Tick)
                : PlayerInput.None;
        }

        MatchSimulation.Tick(Collision, ref _state, _inputs.AsSpan(0, _slots), ref _events, spawnPoints);
        Tick++;
    }

    private void RunCommands()
    {
        for (var slot = 0; slot < _slots; slot++)
        {
            if (_sources[slot] is not ICommandSource source)
                continue;

            _commands.Clear();
            source.PollCommands(new WorldView(in _state, Collision), slot, Tick, ref _commands);

            for (var i = 0; i < _commands.Count; i++)
                Execute(slot, _commands[i]);
        }
    }

    /// <summary>
    /// The simulation half of <c>GameServer.Execute</c>: the same precondition
    /// checks and the same simulation calls, with no broadcast between them.
    /// ⚠️ Add a new kind here <em>and</em> there, against one shared body.
    /// </summary>
    private void Execute(int slot, in PlayerCommand command)
    {
        switch (command.Kind)
        {
            case PlayerCommandKind.BuildSentry:
            {
                // serverBuildSentry.gml: check, then build at the exact position.
                if (!Sentries.CanBuild(_state, slot, Collision))
                    break;

                ref readonly var builder = ref _state.Characters[slot];
                Sentries.Build(ref _state, slot, builder.X, builder.Y, builder.Facing);
                break;
            }

            case PlayerCommandKind.DestroySentry:
                if (_state.HasSentry[slot])
                    Sentries.Destroy(Collision, ref _state, slot);
                break;

            case PlayerCommandKind.ToggleZoom:
                PlayerCommands.ToggleZoom(ref _state, slot);
                break;

            case PlayerCommandKind.DropIntel:
                Intel.Drop(ref _state, slot);
                break;

            case PlayerCommandKind.EatSandvich:
                PlayerCommands.EatSandvich(ref _state, slot);
                break;

            case PlayerCommandKind.ChangeClass:
                // getCharacterObject() == -1: the reference ignores an unknown class.
                if (Enum.IsDefined(command.AsClass))
                    PlayerCommands.ChangeClass(ref _state, slot, command.AsClass, Collision);
                break;

            case PlayerCommandKind.ChangeTeam:
                if (PlayerCommands.ResolveRequestedTeam(ref _state, slot, command.AsTeam) is Team team)
                    PlayerCommands.ChangeTeamWithBalance(ref _state, slot, team, ref _events, out _);
                break;

            case PlayerCommandKind.None:
            default:
                break;
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using GG2.Bots;

namespace GG2.Server.Rooms;

/// <summary>What a room host is configured with.</summary>
public sealed record RoomHostOptions
{
    /// <summary>The one game port every room shares. 0 asks the OS for one.</summary>
    public int Port { get; init; } = 8300;

    /// <summary>
    /// This host's letter in the directory, and the first character of every
    /// code it issues (<c>docs/ROOMS.md</c>, "The directory").
    /// </summary>
    public char Prefix { get; init; } = 'X';

    /// <summary>The most rooms this host holds at once.</summary>
    public int MaxRooms { get; init; } = 8;

    /// <summary>The front door's connection limits.</summary>
    public ConnectionLimits Limits { get; init; } = ConnectionLimits.Default;

    public void Validate()
    {
        if (Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "port must be 0..65535");

        if (RoomCodes.Alphabet.IndexOf(Prefix) < 0)
            throw new ArgumentOutOfRangeException(nameof(Prefix), Prefix, $"must be one of {RoomCodes.Alphabet}");

        if (MaxRooms is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(MaxRooms), MaxRooms, "must be 1..10000");

        Limits.Validate();
    }
}

/// <summary>Room codes: a host prefix and five characters that are the password.</summary>
public static class RoomCodes
{
    /// <summary>
    /// No <c>0/O</c> and no <c>1/I/L</c>, so a code read aloud or off a phone
    /// cannot be mistyped into another one. 31 characters.
    /// </summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>The length of a room's secret, which is its password.</summary>
    public const int SecretLength = 5;

    /// <summary>A fresh secret from a cryptographic source.</summary>
    public static string NewSecret()
    {
        Span<char> chars = stackalloc char[SecretLength];

        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(chars);
    }

    /// <summary>
    /// The secret a typed password names on a host with <paramref name="prefix"/>,
    /// or null if it cannot name one. Accepts the five-character secret or the
    /// whole six-character code, in either case, with surrounding spaces.
    /// </summary>
    public static string? Normalise(string typed, char prefix)
    {
        var text = typed.Trim().ToUpperInvariant();

        if (text.Length == SecretLength + 1 && text[0] == prefix)
            text = text[1..];

        return text.Length == SecretLength ? text : null;
    }
}

/// <summary>One room inside a <see cref="RoomHost"/>.</summary>
public sealed class HostedRoom(string code, GameServer server, DateTime createdUtc, string? creator = null)
{
    /// <summary>The whole code a player shares: the host prefix and the secret.</summary>
    public string Code { get; } = code;

    /// <summary>The five characters after the prefix, which are the password.</summary>
    public string Secret => Code[1..];

    public GameServer Server { get; } = server;

    public DateTime CreatedUtc { get; } = createdUtc;

    /// <summary>
    /// Who asked for the room: the <see cref="RoomCreateLimits.Key"/> of the
    /// address that created it over the API, or null for one made in-process.
    /// </summary>
    public string? Creator { get; } = creator;

    internal TickFaultBudget Faults { get; } = new();
}

/// <summary>
/// Many rooms in one process, behind one game port (<c>docs/ROOMS.md</c>, "The
/// room host").
/// </summary>
/// <remarks>
/// <b>A room is a <see cref="GameServer"/> started with
/// <see cref="GameServer.StartHosted"/></b>: no listener, ticked from here, and
/// reached only through the <see cref="RoomFrontDoor"/>, which routes by
/// password. Everything a room is told to be is in its own
/// <see cref="ServerOptions"/>; <see cref="CreateRoom"/> forces off what would
/// otherwise be per-process (lobby, panel, control port, recording, analytics,
/// a watched config file).
/// <para>
/// <b>One thread</b> ticks every room, then services the front door. A room
/// that throws has its own <see cref="TickFaultBudget"/>: absorbed while it
/// fits, and past it that room ends and the rest carry on. A room that stops
/// itself (<see cref="GameServer.SelfExit"/>) is disposed and its code freed.
/// </para>
/// </remarks>
public sealed class RoomHost : IDisposable
{
    private readonly RoomHostOptions _options;
    private readonly IServerLog _log;
    private readonly Dictionary<string, HostedRoom> _bySecret = new(StringComparer.Ordinal);
    private readonly List<HostedRoom> _rooms = [];
    private readonly SharedNav _sharedNav = new();
    private readonly SharedPolicies _sharedPolicies = new();
    private readonly ConcurrentQueue<Action> _work = new();
    private RoomFrontDoor? _frontDoor;

    public RoomHost(RoomHostOptions options, IServerLog log)
    {
        options.Validate();
        _options = options;
        _log = log;
    }

    public RoomHostOptions Options => _options;

    /// <summary>The game port, once <see cref="Start"/> has bound it.</summary>
    public int BoundPort => _frontDoor?.BoundPort ?? 0;

    public IReadOnlyList<HostedRoom> Rooms => _rooms;

    /// <summary>The nav data every room shares.</summary>
    public SharedNav SharedNav => _sharedNav;

    /// <summary>The policy nets every room shares.</summary>
    public SharedPolicies SharedPolicies => _sharedPolicies;

    /// <summary>Connections at the front door that have not named a room yet.</summary>
    public int PendingConnections => _frontDoor?.PendingCount ?? 0;

    public void Start()
    {
        _frontDoor = new RoomFrontDoor(_options.Port, _options.Limits, _log, FindRoom);
        _log.Info($"room host listening on {BoundPort}, prefix {_options.Prefix}, up to {_options.MaxRooms} rooms");
    }

    /// <summary>
    /// Builds and starts a room from <paramref name="options"/>, or returns null
    /// when the host is full.
    /// </summary>
    /// <remarks>
    /// The options are the room's own from here: the caller must not reuse the
    /// object for another room. <see cref="ServerOptions.Password"/> is replaced
    /// by the room's secret, and the per-process surfaces are switched off.
    /// <para>
    /// <b>Bots are scripted or policy.</b> A policy room with no checkpoint
    /// named runs the shipped bundle, and its nets come from
    /// <see cref="SharedPolicies"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A brain that will not build.</exception>
    /// <param name="creator">What <see cref="HostedRoom.Creator"/> records.</param>
    public HostedRoom? CreateRoom(ServerOptions options, string? creator = null)
    {
        if (_rooms.Count >= _options.MaxRooms)
            return null;

        BuildBrain(options);

        var secret = NewUniqueSecret();
        var code = _options.Prefix + secret;

        options.Password = secret;
        options.Port = BoundPort;
        options.AnnounceToLobby = false;
        options.ControlPortEnabled = false;
        options.Web = WebAdminOptions.Disabled;
        options.RecordingDirectory = string.Empty;
        options.AnalyticsFile = string.Empty;
        options.ConfigFile = string.Empty;

        var server = new GameServer(options, new PrefixedLog(_log, $"[room {code}] "))
        {
            SharedNav = _sharedNav,
            LocalSeed = Random.Shared.Next(1, 65536),
        };

        server.StartHosted();

        var room = new HostedRoom(code, server, DateTime.UtcNow, creator);
        _rooms.Add(room);
        _bySecret.Add(secret, room);

        _log.Info($"room {code} created ({_rooms.Count}/{_options.MaxRooms})");
        return room;
    }

    private void BuildBrain(ServerOptions options)
    {
        if (options.BotMode == BotMode.Policy && options.PolicyPaths.Count == 0)
        {
            options.PolicyPaths = MapDirectory.ResolveCheckpoints();
            options.BotClassPolicies = MapDirectory.ShippedClassPolicies;
        }

        // Fresh per room; only the weights are shared.
        BotBrain.Build(options, report: null, _sharedPolicies.Load);
    }

    /// <summary>The live room a code names, or null.</summary>
    public HostedRoom? Find(string code) =>
        RoomCodes.Normalise(code, _options.Prefix) is { } secret && _bySecret.TryGetValue(secret, out var room)
            ? room
            : null;

    private GameServer? FindRoom(string password) => Find(password)?.Server;

    private string NewUniqueSecret()
    {
        // 2.9e7 codes against at most MaxRooms live ones: a collision is rare,
        // and drawing again is the whole cost of one.
        while (true)
        {
            var secret = RoomCodes.NewSecret();

            if (!_bySecret.ContainsKey(secret))
                return secret;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the tick thread at the start of the next
    /// <see cref="Tick"/>, and completes with its result or its exception.
    /// </summary>
    /// <remarks>
    /// <b>The only way another thread may touch the host</b>: rooms, codes and
    /// the front door are the tick thread's alone, as a standalone server's
    /// state is (<c>docs/WEBADMIN.md</c>'s threading rule). The <see cref="RoomApi"/>
    /// reaches the host through here.
    /// </remarks>
    public Task<T> Invoke<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _work.Enqueue(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });

        return done.Task;
    }

    /// <summary>One host tick: every room, then the front door.</summary>
    /// <remarks>
    /// Rooms before the front door, so a connection handed over here is serviced
    /// by its room on the next host tick, the same one-tick delay an accept has on
    /// a standalone server.
    /// </remarks>
    public void Tick()
    {
        var nowMs = Environment.TickCount64;

        while (_work.TryDequeue(out var work))
            work();

        for (var i = _rooms.Count - 1; i >= 0; i--)
        {
            var room = _rooms[i];

            try
            {
                room.Server.Tick();
            }
            catch (Exception e)
            {
                _log.Error($"room {room.Code}: tick failed", e);

                if (!room.Faults.RecordAndContinue(nowMs))
                {
                    _log.Error($"room {room.Code}: {TickFaultBudget.Allowance} ticks failed within "
                               + $"{TickFaultBudget.Window.TotalSeconds:0}s; ending the room", e);
                    Remove(i, "its ticks kept failing");
                    continue;
                }
            }

            if (room.Server.SelfExit != SelfExitReason.None)
                Remove(i, room.Server.SelfExit == SelfExitReason.Empty ? "nobody was on it" : "it reached its lifetime");
        }

        _frontDoor?.Service(DateTime.UtcNow, nowMs);
    }

    private void Remove(int index, string why)
    {
        var room = _rooms[index];
        _rooms.RemoveAt(index);
        _bySecret.Remove(room.Secret);

        try
        {
            room.Server.Dispose();
        }
        catch (Exception e)
        {
            _log.Error($"room {room.Code}: dispose failed", e);
        }

        _log.Info($"room {room.Code} ended: {why} ({_rooms.Count}/{_options.MaxRooms})");
    }

    /// <summary>Ticks at 30 Hz until cancelled.</summary>
    public void Run(CancellationToken cancellation)
    {
        using var resolution = TimerResolution.Raise();

        var clock = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / GG2.Protocol.GameConstants.TickRate);
        var next = TimeSpan.Zero;

        while (!cancellation.IsCancellationRequested)
        {
            Tick();

            next += interval;
            var wait = next - clock.Elapsed;

            // A host more than ten ticks behind sheds the debt rather than
            // sprinting every room to catch up, as GameServer.Run does.
            if (wait < -interval * 10)
            {
                next = clock.Elapsed;
                continue;
            }

            if (wait > TimeSpan.Zero)
                cancellation.WaitHandle.WaitOne(wait);
        }
    }

    public void Dispose()
    {
        _frontDoor?.Dispose();
        _frontDoor = null;

        for (var i = _rooms.Count - 1; i >= 0; i--)
            Remove(i, "the host stopped");
    }
}

/// <summary>A log that puts a fixed prefix on every line, to tell rooms apart.</summary>
internal sealed class PrefixedLog(IServerLog inner, string prefix) : IServerLog
{
    public void Info(string message) => inner.Info(prefix + message);

    public void Warn(string message) => inner.Warn(prefix + message);

    public void Error(string message, Exception? error = null) => inner.Error(prefix + message, error);
}

using System.Diagnostics;
using System.Globalization;
using System.Net;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Nav;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Host;

/// <summary>
/// <c>gg2-server mesh</c>: two nodes, one match, one real link between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for is the link nobody can simulate</b> — a real one, between
/// two machines, with its real latency and its real jitter. Everything else
/// about the rollback loop is settled in the suite against a scripted link;
/// what a loopback cannot tell you is how deep a node really has to rewind when
/// the delay is whatever the internet felt like that second.
/// </para>
/// <para>
/// Both ends fill the match with bots and play it. Each node owns half the
/// roster, sends its own players' inputs, and receives the other half. Every
/// second each sends a digest of a tick it has confirmed, and the peer says so
/// loudly if it disagrees — that is the divergence check a deployment needs,
/// running for real.
/// </para>
/// <para>
/// ⛔ <b>It serves no clients.</b> A node that a stock GG2 client can connect to
/// is a further piece of work; this is the transport and the loop, measured.
/// `docs/MESH.md`.
/// </para>
/// </remarks>
internal static class MeshRun
{
    public static int Run(string[] args)
    {
        var mapName = Flag(args, "--map") ?? "ctf_truefort";
        var ticks = Int(args, "--ticks", 1800);
        var bots = Int(args, "--bots", 12);
        var listen = Flag(args, "--listen");
        var connect = Flag(args, "--connect");

        if ((listen is null) == (connect is null))
        {
            Console.Error.WriteLine("mesh needs exactly one of --listen <port> or --connect <host:port>");
            PrintUsage();
            return 2;
        }

        var mapDirectory = Flag(args, "--maps") ?? MapDirectory.Resolve();
        var navDirectory = Flag(args, "--botnav") ?? MapDirectory.ResolveNav();

        var map = LevelMap.FromPng(
            mapName, File.ReadAllBytes(Path.Combine(mapDirectory, mapName + ".png"))).ForArea(1);

        var nav = NavCache.TryLoad(navDirectory, mapName, 1, out var loaded) ? loaded : null;

        if (nav is null)
            Console.Error.WriteLine($"no nav graph for {mapName}: the bots will not walk anywhere");

        var spawns = new SpawnPoints(map);
        var collision = new CollisionWorld(map);

        // ⚠️ Which half of the roster this node owns has to be decided the same
        // way on both ends, and from something neither end chooses at random.
        // The listener takes the even slots.
        var mine = listen is not null ? 0 : 1;

        using var link = listen is not null
            ? MeshLink.Listen(IPAddress.Any, int.Parse(listen, CultureInfo.InvariantCulture))
            : Connect(connect!);

        Console.WriteLine(
            $"mesh: {mapName}, {bots} bots, {ticks} ticks, "
            + (listen is not null ? $"listening on {link.Port}" : $"connected to {connect}")
            + $", this node owns the {(mine == 0 ? "even" : "odd")} slots");

        // ⛔ Both ends derive the tick from the WALL CLOCK and a shared origin,
        // never from a local counter. The listener declares the origin and the
        // connector adopts it; after that a tick is a wall-clock instant on both
        // machines, so they stay aligned to whatever NTP gives them instead of
        // to their own pacing drift. Measured without it: a node started eight
        // seconds late refused all 4,032 inputs it was sent.
        // The node exists before the handshake so that anything the peer sends
        // early reaches it rather than a throwaway.
        var node = new MeshNode(collision, Roster(spawns, bots), new MeshOptions(), spawns);
        var brains = Brains(bots, mine, nav, spawns);

        var origin = Handshake(link, node, listen is not null);

        if (origin is null)
        {
            Console.Error.WriteLine("no peer within 60s");
            return 1;
        }

        Console.WriteLine($"tick origin agreed: {origin}");

        var slots = new List<int>();
        var inputs = new List<PlayerInput>();
        var disagreements = 0;
        var agreements = 0;
        var outsideWindow = 0;
        var confirmed = 0;

        var clock = Stopwatch.StartNew();

        for (var tick = 0; tick < ticks; tick++)
        {
            // Wait until this tick is due by the shared clock, and never simply
            // sleep a tick's worth: a node that paced off its own start drifts
            // away from its peer at whatever rate its sleeps are inaccurate.
            var due = origin.Value + (long)(tick * (1000.0 / 30));
            var wait = due - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (wait > 0)
                Thread.Sleep((int)Math.Min(wait, 1000));

            slots.Clear();
            inputs.Clear();

            foreach (var (slot, bot) in brains)
            {
                if (!node.State.IsAlive(slot))
                    continue;

                slots.Add(slot);
                inputs.Add(bot.Poll(new WorldView(in node.State, collision), slot, tick));
            }

            for (var i = 0; i < slots.Count; i++)
                node.Submit(tick, slots[i], inputs[i]);

            link.SendInputs(tick, slots.ToArray(), inputs.ToArray());

            // A digest a second, of a tick this node has confirmed — so the peer
            // is comparing against something that can no longer change.
            if (tick % 30 == 0 && node.Confirmed > 0)
            {
                var at = node.Confirmed - 1;

                if (node.DigestAt(at) is { } mine2)
                    link.SendDigest(at, mine2);
            }

            link.Service(node, (at, theirs) =>
            {
                // ⛔ Compared against this node's OWN digest for that same tick,
                // never against its live state: the live state is still
                // provisional at both ends and two nodes that agree entirely
                // about the match differ there.
                var ours = node.DigestAt(at);

                if (ours is null)
                {
                    outsideWindow++;
                }
                else if (ours == theirs)
                {
                    agreements++;
                }
                else
                {
                    disagreements++;
                    link.NoteDisagreement();

                    Console.Error.WriteLine(
                        $"DIVERGED at tick {at}: this node {ours:x16}, peer {theirs:x16}");
                }
            });

            node.Advance();

            while (node.TryTakeConfirmed(out _, out _))
                confirmed++;
        }

        clock.Stop();

        Console.WriteLine();
        Console.WriteLine($"node   {node.Stats}");
        Console.WriteLine($"link   {link.Stats}");
        Console.WriteLine($"final  tick {node.Tick}, confirmed {node.Confirmed}, digest {node.Digest:x16}");
        Console.WriteLine(
            $"peer   {agreements} tick(s) agreed, {disagreements} diverged, "
            + $"{outsideWindow} outside the window");
        Console.WriteLine($"events {confirmed} confirmed tick(s) released");
        Console.WriteLine($"took   {clock.Elapsed.TotalSeconds:0.0}s for {ticks} ticks");

        return 0;
    }

    /// <summary>
    /// Agrees a tick origin: the listener names one, the connector adopts it.
    /// </summary>
    /// <remarks>
    /// <b>The listener names it because somebody has to</b>, and it is the end
    /// that exists first. It keeps announcing until the peer has had a chance to
    /// hear — a Hello costs nine bytes and arriving twice is harmless, which is
    /// cheaper than a handshake with states in it.
    /// </remarks>
    private static long? Handshake(MeshLink link, MeshNode node, bool listening)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        long? origin = null;

        while (DateTime.UtcNow < deadline)
        {
            // ⚠️ The origin is named when the peer ARRIVES, not when this node
            // started waiting. Naming it up front puts tick zero however long
            // the wait was into the past, and the node that joins then sprints
            // through a backlog of ticks while its peer sends it the present —
            // which lands outside the rollback window and is refused. Measured:
            // 426 inputs lost and two nodes that never agreed.
            if (listening && link.Connected)
            {
                origin ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000;
                link.SendHello(origin.Value);
            }

            link.Service(node);

            if (listening && link.Connected && link.Stats.FramesSent > 2)
                return origin;

            if (!listening && link.PeerOrigin is { } theirs)
                return theirs;

            Thread.Sleep(20);
        }

        return null;
    }

    private static MeshLink Connect(string target)
    {
        var colon = target.LastIndexOf(':');

        if (colon <= 0)
            throw new ArgumentException($"--connect wants host:port, not '{target}'");

        return MeshLink.Connect(
            target[..colon], int.Parse(target[(colon + 1)..], CultureInfo.InvariantCulture));
    }

    private static WorldState Roster(SpawnPoints spawns, int bots)
    {
        var state = default(WorldState);
        state.AddPlayer(0);

        for (var slot = 1; slot <= bots; slot++)
        {
            var team = slot % 2 == 0 ? Team.Red : Team.Blue;

            state.AddPlayer(slot);
            state.Players[slot].IsBot = true;
            state.Players[slot].Team = team;
            state.Players[slot].Class = PlayerClass.Soldier;
            state.Spawn(spawns, slot, PlayerClass.Soldier, team, spawnpointId: 0, group: 0);
        }

        return state;
    }

    private static Dictionary<int, ScriptedBot> Brains(
        int bots, int mine, NavGraph? nav, SpawnPoints spawns)
    {
        var brains = new Dictionary<int, ScriptedBot>();

        for (var slot = 1; slot <= bots; slot++)
        {
            if (slot % 2 != mine)
                continue;

            var team = slot % 2 == 0 ? Team.Red : Team.Blue;
            var bot = new ScriptedBot(nav, new NavQuery(team), BotSkill.ForTier(3), slot);
            var earlier = new List<PlayerClass>();

            for (var other = 1; other < slot; other++)
            {
                if ((other % 2 == 0 ? Team.Red : Team.Blue) == team)
                    earlier.Add(PlayerClass.Soldier);
            }

            bot.Assign(slot, team, PlayerClass.Soldier, earlier.ToArray(), nav, spawns);
            brains[slot] = bot;
        }

        return brains;
    }

    private static string? Flag(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Int(string[] args, string name, int fallback) =>
        Flag(args, name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

    public static void PrintUsage() => Console.WriteLine(
        """
        gg2-server mesh - run one match across two nodes and a real link

          --listen <port>        wait for the other node; owns the EVEN slots
          --connect <host:port>  join the other node; owns the ODD slots
          --map <name>           default ctf_truefort
          --bots <n>             players in the match, default 12
          --ticks <n>            how long to play, default 1800 (a minute)
          --maps <path>          where the map PNGs live
          --botnav <path>        where the nav graphs live

        Both ends must be given the same --map, --bots and --ticks: they start
        from a roster each builds for itself, and two different rosters are two
        different matches that will never agree.

        It serves no GG2 clients. What it measures is the link - how deep a node
        really has to rewind when the delay is a real one - and whether two
        machines running the same inputs still agree at the end.
        """);
}

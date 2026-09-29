using GG2.Maps;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// A client survives a rotation into <em>every</em> gamemode, not just the one
/// that was reported broken.
/// </summary>
/// <remarks>
/// <b>The generalisation of the stale-<c>MatchState</c> bug.</b> That one made
/// the server keep writing the previous map's HUD block after a rotation, which
/// killed every connected stock client — and it was found against exactly one
/// pair of gamemodes, CTF → KotH, because that is the pair somebody happened to
/// put in a rotation. The fix is structural, so it cannot come back the same way;
/// this file exists because the <em>class</em> can, and because eight HUD block
/// layouts written by <see cref="StateUpdateWriter"/> are read back by a separate
/// <c>StateUpdateReader</c> and nothing was checking that they agree across a
/// change of mode.
/// <para>
/// <b>It walks a cycle rather than every pair.</b> Eight modes is 56 ordered
/// pairs and ~1,800 ticks each to reach a scoreboard heartbeat; a cycle visits
/// every mode once as a source and once as a destination, which is what catches a
/// layout that is wrong rather than a transition that is special.
/// </para>
/// <para>
/// <b>This shares the port's own reader</b> — <c>docs/PARITY.md</c>, the blind
/// spot beside the gate. It proves the writer and the reader agree about each
/// mode and that neither goes stale; it cannot prove either matches a real GM8
/// client. Only driving one does that.
/// </para>
/// </remarks>
public sealed class GamemodeRotationTests
{
    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    /// <summary>
    /// One shipped map per gamemode, chosen by asking the maps rather than by a
    /// hand-written list that would rot.
    /// </summary>
    /// <remarks>
    /// <c>ReferenceTree.PortAuthoredMaps</c> is excluded for the reason every
    /// shipped-map census excludes it: <c>gg_debug2</c> is this repo's own and is
    /// not a map anybody rotates onto.
    /// </remarks>
    private static Dictionary<GameMode, string> OnePerGamemode()
    {
        var byMode = new Dictionary<GameMode, string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Keyed on the FILE's own base name, not on the roster of names.
        // InternalMaps.Names carries five aliases — ctf_2dfort1, ctf_2dfort[0]
        // and friends — that resolve to another map's PNG and have none of their
        // own. Two of them sort ahead of the map they alias, so deduping by file
        // while still loading by name asks for a PNG that is not on disk.
        foreach (var name in InternalMaps.Names.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (InternalMaps.FileFor(name) is not { } file || !seenFiles.Add(file))
                continue;

            var canonical = Path.GetFileNameWithoutExtension(file);

            if (ReferenceTree.PortAuthoredMaps.Contains(canonical))
                continue;

            var mode = GameModes.Of(Load(canonical).ForArea(1));

            if (!byMode.ContainsKey(mode))
                byMode[mode] = canonical;
        }

        return byMode;
    }

    /// <summary>
    /// Rotating through every shipped gamemode leaves the client in agreement.
    /// </summary>
    [Fact]
    public void AClientSurvivesARotationIntoEveryGamemode()
    {
        var maps = OnePerGamemode();

        // Reported, not asserted: which modes have a shipped map is a property of
        // the map set, and a test that demanded all eight would fail for a reason
        // that has nothing to do with rotation. What matters is that more than one
        // mode is exercised — otherwise this whole file is vacuous.
        Assert.True(
            maps.Count >= 4,
            $"only {maps.Count} gamemode(s) have a shipped map: [{string.Join(", ", maps.Keys)}]");

        var order = maps.OrderBy(pair => (int)pair.Key).Select(pair => pair.Value).ToList();

        using var harness = new ServerHarness(options =>
        {
            options.Map = order[0];
            options.MapRotation = [.. order];
            options.ShuffleRotation = ShuffleMode.None;
        });

        var client = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(client, Load, order[0]);

        harness.Tick(30);

        // Round-trip back to the first, so the last mode is a source as well as a
        // destination.
        foreach (var map in order.Skip(1).Append(order[0]))
        {
            Assert.True(harness.Server.RequestMapChange(map), $"the change to {map} was refused");

            harness.TickUntil(
                () =>
                {
                    mirror.Pump();
                    return mirror.CurrentMap == map;
                },
                $"the client to be told about {map}",
                maxTicks: 600);

            AssertHudArrives(harness, mirror, map);
        }
    }

    /// <summary>
    /// Runs on until a <c>CAPS_UPDATE</c> lands, then checks the client still
    /// agrees with the server.
    /// </summary>
    /// <remarks>
    /// <b>Waiting for the <c>CAPS_UPDATE</c> is the whole point.</b> The HUD
    /// block rides on <c>FULL_UPDATE</c> and <c>CAPS_UPDATE</c> only
    /// (<c>serializeState.gml</c>), so a rotation test that ticks a few hundred
    /// times never makes the server write the layout it means to check — which is
    /// exactly how <c>ClientRosterAgreementTests</c> watched the broken rotation
    /// and passed. <c>_frame % 3600</c> is the two-minute heartbeat and is the one
    /// trigger that needs nothing to happen in the match.
    /// </remarks>
    private static void AssertHudArrives(ServerHarness harness, MirrorClient mirror, string map)
    {
        var before = mirror.Of(MessageType.CapsUpdate).Count;

        for (var i = 0; i < 3700 && mirror.Of(MessageType.CapsUpdate).Count == before; i++)
        {
            harness.Tick(1);
            mirror.Pump();
        }

        Assert.True(
            mirror.Of(MessageType.CapsUpdate).Count > before,
            $"{map}: no CAPS_UPDATE was emitted, so its HUD block was never written");

        Assert.Equal(map, mirror.CurrentMap);
        Assert.Equal(harness.Server.RosterCount, mirror.PlayerCount);

        for (var slot = 0; slot < harness.Server.RosterCount; slot++)
        {
            Assert.True(
                harness.Server.EntryOf(slot).Name == mirror.Names[slot],
                $"{map}: slot {slot} is '{harness.Server.EntryOf(slot).Name}' on the server "
                + $"and '{mirror.Names[slot]}' to the client — the stream has slipped");

            Assert.True(
                harness.Server.TeamOf(slot) == mirror.Teams[slot],
                $"{map}: slot {slot} is {harness.Server.TeamOf(slot)} on the server "
                + $"and {mirror.Teams[slot]} to the client");
        }
    }
}

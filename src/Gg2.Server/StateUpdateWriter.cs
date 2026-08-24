using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// Writes the four state-update tiers, transcribed from
/// <c>Scripts/Serialization/serializeState.gml</c> and the <c>User Event 12</c>
/// of everything it visits.
/// </summary>
/// <remarks>
/// <b>This serialises an empty world.</b> Phase 1 of docs/PLAN.md has no
/// simulation, so no player owns a character or a sentry and the per-player
/// block is a single zero byte. The tiers, the cadence and every tail field are
/// real, because those are what the client's <c>deserializeState</c> reads and
/// getting one wrong desynchronises the stream permanently rather than
/// producing a visible glitch.
/// <para>
/// When <c>Gg2.Simulation</c> exists this moves next to it and takes a world
/// rather than a <see cref="MatchState"/>. The layout below does not change;
/// only the source of the numbers does.
/// </para>
/// </remarks>
public sealed class StateUpdateWriter(MatchState match)
{
    /// <summary>
    /// Writes one update of the given tier.
    /// </summary>
    /// <remarks>
    /// The tier is the message byte, which is why <c>INPUTSTATE</c> is both a
    /// client command and a server message.
    /// </remarks>
    public void Write(NetWriter writer, MessageType tier)
    {
        writer.WriteMessageType(tier);

        if (tier == MessageType.FullUpdate)
            writer.WriteUShortFromReal(match.TdmInvulnerabilityTicks);

        // The client aborts with "Wrong number of players while deserializing
        // state" if this disagrees with its own roster size, so every
        // PLAYER_JOIN and PLAYER_LEAVE must already have reached it.
        writer.WriteUByte(checked((byte)match.Roster.Count));

        if (tier != MessageType.CapsUpdate)
        {
            for (var i = 0; i < match.Roster.Count; i++)
                WritePlayer(writer, tier, i);

            // One block per MovingPlatform, in instance order, with no count
            // prefix — the client has the same map loaded and knows how many
            // there are. No shipped map has any.
            foreach (var platform in match.MovingPlatforms)
                WriteMovingPlatform(writer, tier, platform);
        }

        if (tier == MessageType.FullUpdate)
        {
            WriteIntel(writer, match.RedIntel);
            WriteIntel(writer, match.BlueIntel);

            writer.WriteUByte(match.CapLimit);
            writer.WriteUByte(match.RedCaps);
            writer.WriteUByte(match.BlueCaps);
            writer.WriteUByte(match.RespawnTimeSeconds);
            WriteHud(writer, tier);

            for (var i = 0; i < 10; i++)
                writer.WriteUByte(match.ClassLimits[i]);
        }

        if (tier == MessageType.CapsUpdate)
        {
            writer.WriteUByte(match.RedCaps);
            writer.WriteUByte(match.BlueCaps);
            writer.WriteUByte(match.RespawnTimeSeconds);
            WriteHud(writer, tier);
        }
    }

    /// <summary>
    /// <c>Objects/InGameElements/Player.events/User Event 12.xml</c>.
    /// </summary>
    private void WritePlayer(NetWriter writer, MessageType tier, int index)
    {
        var player = match.Roster[index];

        if (tier == MessageType.FullUpdate)
        {
            // Stats, in serialisation order — which is not StatIndex order:
            // REVENGE and DOMINATIONS are not sent, and POINTS follows BONUS.
            writer.WriteUByte(0); // kills
            writer.WriteUByte(0); // deaths
            writer.WriteUByte(0); // caps
            writer.WriteUByte(0); // assists
            writer.WriteUByte(0); // destruction
            writer.WriteUByte(0); // stabs
            writer.WriteUShort(0); // healing
            writer.WriteUByte(0); // defenses
            writer.WriteUByte(0); // invulns
            writer.WriteUByte(0); // bonus
            writer.WriteUByte(0); // points
            writer.WriteUByte(player.QueueJump ? (byte)1 : (byte)0);

            // Rewards come from an auth server this port never talks to.
            writer.WriteString2(string.Empty);

            // One domination count per *other* player, in roster order.
            for (var i = 0; i < match.Roster.Count; i++)
                if (i != index)
                    writer.WriteUByte(0);
        }

        // 0x01 = has a Character, 0x02 = has a Sentry. Nothing has either yet.
        writer.WriteUByte(0);
    }

    /// <summary>
    /// <c>Objects/Map elements/Colliders/MovingPlatform.events/User Event 10.xml</c>
    /// — nothing at all on an INPUTSTATE tick.
    /// </summary>
    private static void WriteMovingPlatform(NetWriter writer, MessageType tier, MovingPlatformState platform)
    {
        if (tier is not (MessageType.QuickUpdate or MessageType.FullUpdate))
            return;

        // x_ * 5 with no GML round(): the extension's own conversion is the
        // whole quantisation. Note the underscored x_, which is the platform's
        // real position — x is the rounded copy GM8 draws.
        writer.WriteUShortFromReal(platform.X * 5);
        writer.WriteUShortFromReal(platform.Y * 5);
        writer.WriteUByte(platform.MovementState);
    }

    /// <summary>
    /// <c>Scripts/Serialization/serialize.gml</c>: a ushort instance count, then
    /// one <c>User Event 10</c> block each. The client destroys every instance
    /// it has and recreates exactly that many, so the count is authoritative.
    /// </summary>
    private static void WriteIntel(NetWriter writer, IntelState? intel)
    {
        if (intel is null)
        {
            writer.WriteUShort(0);
            return;
        }

        writer.WriteUShort(1);

        // round(x*5), unlike Character's plain x*5: GML's round is half-to-even
        // and runs first, after which the extension's own half-away-from-zero
        // round has nothing left to do. Two objects, two quantisations, one
        // wire — see WireQuantise.
        writer.WriteUShortFromReal(Gml.Round(intel.X * 5));
        writer.WriteUShortFromReal(Gml.Round(intel.Y * 5));
        writer.WriteShort(intel.RecoverTimer);
    }

    /// <summary>
    /// The gamemode HUD block, from the <c>User Event 12</c> of whichever HUD
    /// <c>basicRoomSetup.gml</c> would have created.
    /// </summary>
    /// <remarks>
    /// Only <see cref="GameMode.Arena"/> varies by tier, and only on
    /// <c>FULL_UPDATE</c>. Everything else writes the same bytes for
    /// <c>FULL_UPDATE</c> and <c>CAPS_UPDATE</c> alike.
    /// </remarks>
    private void WriteHud(NetWriter writer, MessageType tier)
    {
        switch (match.GameMode)
        {
            case GameMode.CaptureTheFlag:
                writer.WriteUByte(match.TimeLimitMinutes);
                writer.WriteUInt(match.Timer);
                break;

            case GameMode.Invasion:
                writer.WriteUByte(match.TimeLimitMinutes);
                writer.WriteUInt(match.Timer);
                writer.WriteUShort(match.SetupTimer);
                break;

            case GameMode.Generator:
                writer.WriteUByte(match.TimeLimitMinutes);
                writer.WriteUInt(match.Timer);
                WriteGenerator(writer, match.BlueGenerator);
                WriteGenerator(writer, match.RedGenerator);
                break;

            case GameMode.Arena:
                if (tier == MessageType.FullUpdate)
                {
                    writer.WriteUByte(match.RedWins);
                    writer.WriteUByte(match.BlueWins);
                    writer.WriteUByte(match.ArenaState);
                    writer.WriteByte(match.ArenaWinners);
                    writer.WriteUShort(match.ArenaEndCount);
                }

                writer.WriteUByte(match.TimeLimitMinutes);
                writer.WriteUInt(match.Timer);
                writer.WriteUShort(match.ControlPointUnlock);
                writer.WriteUByteFromReal(Gml.Floor(match.ArenaRoundStart / 2.0));
                foreach (var point in match.ControlPoints)
                    WriteControlPoint(writer, point);
                break;

            case GameMode.KingOfTheHill:
            case GameMode.DualKingOfTheHill:
                writer.WriteUShort(match.ControlPointUnlock);
                writer.WriteUShort(match.RedTimer);
                writer.WriteUShort(match.BlueTimer);
                foreach (var point in match.ControlPoints)
                    WriteControlPoint(writer, point);
                break;

            case GameMode.ControlPoints:
                writer.WriteUByte(match.TimeLimitMinutes);
                writer.WriteUInt(match.Timer);
                writer.WriteUShort(match.SetupTimer);
                foreach (var point in match.ControlPoints)
                    WriteControlPoint(writer, point);
                break;

            case GameMode.TeamDeathmatch:
                writer.WriteUByte(match.TimeLimitMinutes);
                writer.WriteUInt(match.Timer);
                writer.WriteUShort(match.KillLimit);
                break;

            default:
                throw new InvalidOperationException($"no HUD block for {match.GameMode}");
        }
    }

    /// <summary>
    /// <c>Objects/Map elements/Gamemode-specific/CP/ControlPoint.events/User Event 12.xml</c>.
    /// </summary>
    private static void WriteControlPoint(NetWriter writer, ControlPointState point)
    {
        // team and cappingTeam are signed: -1 means neutral / nobody capping.
        writer.WriteByte(point.Team);
        writer.WriteByte(point.CappingTeam);

        // round(capping) in the reference: GML's half-to-even.
        writer.WriteUShortFromReal(Gml.Round(point.Capping));
    }

    /// <summary>
    /// <c>Objects/Map elements/Gamemode-specific/Generator/Generator.events/User Event 12.xml</c>.
    /// </summary>
    private static void WriteGenerator(NetWriter writer, GeneratorState generator)
    {
        // ceil(hp) in the reference: a generator on 0.1 hp is still standing,
        // and the client must not draw it dead.
        writer.WriteUShortFromReal(Gml.Ceil(generator.Hp));
        writer.WriteUShortFromReal(Gml.Ceil(generator.ShieldHp));
    }
}

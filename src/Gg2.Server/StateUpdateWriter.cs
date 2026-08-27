using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// Answers what a roster slot's character looks like on the wire, if it has one.
/// </summary>
/// <remarks>
/// A delegate rather than the world itself so the writer never copies a
/// <c>WorldState</c> — it is a large value type by design (docs/BOTAI.md item
/// 1), and serialisation runs every tick.
/// </remarks>
public delegate bool CharacterLookup(int slot, out CharacterBlock character, out WeaponBlock weapon);

/// <summary>
/// Answers what a roster slot's sentry looks like on the wire, if it has one.
/// A delegate for the same reason <see cref="CharacterLookup"/> is.
/// </summary>
public delegate bool SentryLookup(int slot, out SentryBlock sentry);

/// <summary>
/// What the objective contributes to a <c>FULL_UPDATE</c> or a
/// <c>CAPS_UPDATE</c>: the two briefcases and the two cap counters.
/// </summary>
/// <remarks>
/// A lookup for the same reason <see cref="CharacterLookup"/> is one — the
/// objective now lives in <c>Gg2.Simulation.WorldState</c>, which is a large
/// value type the writer must not copy. Null means a server with no objective
/// simulated at all, which writes two empty instance lists and two zeroes; that
/// is what the layout tests want and is a legal state (a map with no intel).
/// </remarks>
public delegate ObjectiveBlock ObjectiveLookup();

/// <summary>
/// One roster entry's scoreboard, as the <c>FULL_UPDATE</c> wants it.
/// </summary>
/// <remarks>
/// A lookup for the same reason the others are: the counters live in
/// <c>Gg2.Simulation.WorldState</c>, which the writer must not copy. Null means
/// a server with no scoreboard simulated, which writes zeroes — what the layout
/// tests want, and what this port did until the stats landed.
/// </remarks>
public delegate PlayerStatsBlock StatsLookup(int slot);

/// <summary>
/// How many times one roster entry has killed another without dying to them
/// since — one cell of the domination matrix.
/// </summary>
/// <remarks>
/// A second lookup rather than a field on <see cref="PlayerStatsBlock"/>,
/// because the block a player writes is a <em>row</em> whose length is the
/// roster size and the writer must not allocate one per player per update. Null
/// writes zeroes, which is what this port did until the matrix landed.
/// </remarks>
public delegate byte DominationLookup(int killer, int victim);

/// <summary>
/// The eleven counters a <c>FULL_UPDATE</c> carries, in
/// <c>Player.events/User Event 12.xml</c>'s order.
/// </summary>
/// <remarks>
/// Reals, because the reference's are: an assist is worth half a point and
/// healing accumulates fractional hit points. The quantisation happens at the
/// writer, once.
/// </remarks>
public readonly record struct PlayerStatsBlock(
    double Kills,
    double Deaths,
    double Caps,
    double Assists,
    double Destruction,
    double Stabs,
    double Healing,
    double Defenses,
    double Invulns,
    double Bonus,
    double Points);

/// <summary>
/// The objective, in wire terms. <c>null</c> for a briefcase that is being
/// carried or that the map does not place — <c>serialize(IntelligenceRed)</c>
/// then writes a count of zero.
/// </summary>
/// <param name="RedIntel">Where the red briefcase is lying, and its return alarm.</param>
/// <param name="BlueIntel">The same for blue's.</param>
/// <param name="RedCaps">Captures by the red team, clamped on the way out.</param>
/// <param name="BlueCaps">Captures by the blue team.</param>
/// <param name="TimeLimitMinutes">
/// <c>global.timeLimitMins</c>. Six of the eight gamemodes' HUD blocks open
/// with it.
/// </param>
/// <param name="RoundTimer">
/// Ticks left in the round, as a <c>uint</c> on the wire. The client runs its
/// own copy down between updates, so a server that never advanced it — which
/// this port did until the round landed — shows every client a frozen clock.
/// </param>
/// <param name="ControlPoints">
/// The map's control points, in the order the HUD block wants them —
/// <c>Gg2.Simulation.ControlPoints.Place</c> decides that once, at load.
/// </param>
/// <param name="ControlPointUnlock">
/// <c>cpUnlock</c>: ticks of pregame left. In the KotH, DKotH, Arena and CP
/// blocks.
/// </param>
/// <param name="RedTimer">KotH's per-team countdown, in ticks.</param>
/// <param name="BlueTimer">The other one.</param>
/// <param name="ArenaState">Which phase of an Arena map the match is in.</param>
/// <param name="ArenaRedWins">Rounds red has won on this map.</param>
/// <param name="ArenaBlueWins">Rounds blue has won.</param>
/// <param name="ArenaWinner">Who won the last <em>round</em>, not the map.</param>
/// <param name="ArenaEndCount">Ticks of scoreboard left after a round.</param>
/// <param name="ArenaRoundStart">
/// Ticks of setup left. ⚠️ Sent as <c>floor(roundStart/2)</c> in one byte.
/// </param>
/// <param name="SetupTimer">
/// <c>global.setupTimer</c>: ticks before the setup gates open. In the Invasion
/// and control-point blocks, and zero in every other mode.
/// </param>
/// <param name="RedGenerator">
/// The red team's generator, or null once it has been destroyed — in which case
/// the HUD block simply does not carry it.
/// </param>
/// <param name="BlueGenerator">The other one.</param>
/// <param name="KillLimit">
/// <c>global.killLimit</c>: kills that end a Team Deathmatch round. In that
/// mode's HUD block and nowhere else.
/// </param>
/// <param name="TdmInvulnerabilityTicks">
/// <c>global.tdmInvulnerabilityTicks</c>, on every <c>FULL_UPDATE</c> whatever
/// the mode — it leads the message, ahead of the player count. Zero outside
/// Team Deathmatch.
/// </param>
public readonly record struct ObjectiveBlock(
    IntelBlock? RedIntel,
    IntelBlock? BlueIntel,
    int RedCaps,
    int BlueCaps,
    int TimeLimitMinutes = 0,
    int RoundTimer = 0,
    ControlPointBlock[]? ControlPoints = null,
    int ControlPointUnlock = 0,
    int RedTimer = 0,
    int BlueTimer = 0,
    int SetupTimer = 0,
    byte ArenaState = 0,
    int ArenaRedWins = 0,
    int ArenaBlueWins = 0,
    sbyte ArenaWinner = 2,
    int ArenaEndCount = 0,
    int ArenaRoundStart = 0,
    GeneratorBlock? RedGenerator = null,
    GeneratorBlock? BlueGenerator = null,
    int KillLimit = 0,
    int TdmInvulnerabilityTicks = 0);

/// <summary>
/// Writes the four state-update tiers, transcribed from
/// <c>Scripts/Serialization/serializeState.gml</c> and the <c>User Event 12</c>
/// of everything it visits.
/// </summary>
/// <remarks>
/// <b>Characters are real; everything else is not yet.</b> Phase 4 simulates
/// the character update, so a player who has spawned serialises a full block
/// straight out of <c>Gg2.Simulation</c>. Sentries, weapons, scores and the
/// objective state are still placeholders at their initial values, waiting on
/// the phases that own them.
/// <para>
/// The tiers, the cadence and every tail field are real regardless, because
/// those are what the client's <c>deserializeState</c> reads and getting one
/// wrong desynchronises the stream permanently rather than producing a visible
/// glitch.
/// </para>
/// </remarks>
public sealed class StateUpdateWriter(
    MatchState match,
    CharacterLookup characters,
    SentryLookup? sentries = null,
    ObjectiveLookup? objective = null,
    StatsLookup? statistics = null,
    DominationLookup? dominations = null)
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

        // One call per update, and it has to be first: the FULL_UPDATE's leading
        // field comes out of it too, ahead of the player count. The lookup
        // reaches into a WorldState the writer must not copy, and WriteHud
        // below needs the same answer.
        _objective = objective?.Invoke() ?? default;

        if (tier == MessageType.FullUpdate)
            writer.WriteUShortFromReal(_objective.TdmInvulnerabilityTicks);

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

        var scores = _objective;

        if (tier == MessageType.FullUpdate)
        {
            WriteIntel(writer, scores.RedIntel);
            WriteIntel(writer, scores.BlueIntel);

            writer.WriteUByte(match.CapLimit);
            writer.WriteUByteFromReal(scores.RedCaps);
            writer.WriteUByteFromReal(scores.BlueCaps);
            writer.WriteUByte(match.RespawnTimeSeconds);
            WriteHud(writer, tier);

            for (var i = 0; i < 10; i++)
                writer.WriteUByte(match.ClassLimits[i]);
        }

        if (tier == MessageType.CapsUpdate)
        {
            writer.WriteUByteFromReal(scores.RedCaps);
            writer.WriteUByteFromReal(scores.BlueCaps);
            writer.WriteUByte(match.RespawnTimeSeconds);
            WriteHud(writer, tier);
        }
    }

    /// <summary>The objective as of the update being written. See <see cref="Write"/>.</summary>
    private ObjectiveBlock _objective;

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
            //
            // ⚠️ Every one is a **real** in the reference and every one but
            // healing is written as a ubyte, so the wire rounds them half away
            // from zero. An assist is worth half a point, which means a client's
            // scoreboard can read one point above the host's own — the host
            // draws floor(stats[POINTS]) and the client draws what it was sent.
            // That is the reference's, not a quantisation this port chose.
            var stats = statistics?.Invoke(index) ?? default;

            writer.WriteUByteFromReal(stats.Kills);
            writer.WriteUByteFromReal(stats.Deaths);
            writer.WriteUByteFromReal(stats.Caps);
            writer.WriteUByteFromReal(stats.Assists);
            writer.WriteUByteFromReal(stats.Destruction);
            writer.WriteUByteFromReal(stats.Stabs);
            writer.WriteUShortFromReal(stats.Healing);
            writer.WriteUByteFromReal(stats.Defenses);
            writer.WriteUByteFromReal(stats.Invulns);
            writer.WriteUByteFromReal(stats.Bonus);
            writer.WriteUByteFromReal(stats.Points);
            writer.WriteUByte(player.QueueJump ? (byte)1 : (byte)0);

            // Rewards come from an auth server this port never talks to.
            writer.WriteString2(string.Empty);

            // One domination count per *other* player, in roster order — no
            // count prefix, because the length is the roster size the message
            // already declared. ⚠️ Self is skipped rather than written as a
            // zero, so the row is one byte shorter than the roster and a reader
            // that took `count` bytes would be one ahead per player.
            for (var i = 0; i < match.Roster.Count; i++)
            {
                if (i != index)
                    writer.WriteUByte(dominations?.Invoke(index, i) ?? 0);
            }
        }

        // 0x01 = has a Character, 0x02 = has a Sentry.
        var hasCharacter = characters(index, out var character, out var weapon);
        var sentry = default(SentryBlock);
        var hasSentry = sentries?.Invoke(index, out sentry) ?? false;

        writer.WriteUByte((byte)((hasCharacter ? 0x01 : 0x00) | (hasSentry ? 0x02 : 0x00)));

        if (hasCharacter)
            WriteCharacter(writer, tier, character, weapon);

        if (hasSentry)
            WriteSentry(writer, tier, sentry);
    }

    /// <summary>
    /// <c>Objects/InGameElements/Sentry.events/User Event 12.xml</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>On an <c>INPUTSTATE</c> a sentry contributes NOTHING — not even
    /// its hp.</b> Both groups below are guarded on the tier and between them
    /// they cover only <c>QUICK_UPDATE</c> and <c>FULL_UPDATE</c>, so on an
    /// <c>INPUTSTATE</c> the player's block ends at its subobjects byte with
    /// bit 1 still set. Writing the hp byte unconditionally is the framing bug
    /// docs/PROTOCOL.md's "Per sentry" section is about.
    /// </remarks>
    private static void WriteSentry(NetWriter writer, MessageType tier, in SentryBlock sentry)
    {
        if (tier == MessageType.FullUpdate)
        {
            writer.WriteByte(sentry.StartDirection);
            writer.WriteUShort(sentry.X);
            writer.WriteUShort(sentry.Y);
        }

        if (tier is MessageType.QuickUpdate or MessageType.FullUpdate)
            writer.WriteUByte(sentry.BuiltAndHp);

        // Self-describing through bit 7 of the byte just written: a falling
        // sentry's vspeed follows on a FULL_UPDATE only.
        if (!sentry.Built && tier == MessageType.FullUpdate)
            writer.WriteUByte(sentry.VSpeed ?? 0);
    }

    /// <summary>
    /// <c>Objects/InGameElements/Character.events/User Event 12.xml</c>.
    /// </summary>
    /// <remarks>
    /// The three tiers are nested rather than separate: an <c>INPUTSTATE</c>
    /// block is the first three fields, a <c>QUICK_UPDATE</c> adds position
    /// through flags, and a <c>FULL_UPDATE</c> adds the tail and is followed by
    /// the current weapon's own block.
    /// </remarks>
    private static void WriteCharacter(
        NetWriter writer,
        MessageType tier,
        in CharacterBlock character,
        in WeaponBlock weapon)
    {
        writer.WriteUByte((byte)character.Keys);
        writer.WriteUShort(character.NetAimDirection);
        writer.WriteUByte(character.AimDistance);

        if (tier is not (MessageType.QuickUpdate or MessageType.FullUpdate))
            return;

        // Already quantised by Gg2.Simulation.CharacterWire, which is the same
        // conversion the parity replayer compares against — deliberately not
        // repeated here.
        writer.WriteUShort(character.X);
        writer.WriteUShort(character.Y);
        writer.WriteByte(character.HSpeed);
        writer.WriteByte(character.VSpeed);
        writer.WriteUByte(character.Hp);
        writer.WriteUByte(character.Ammo);
        writer.WriteUByte(character.Flags);

        if (tier != MessageType.FullUpdate)
            return;

        writer.WriteUByte(character.AnimationOffset);
        writer.WriteUByte(character.ClassSpecific);
        writer.WriteShort(character.Alarm1);
        writer.WriteUByte(character.Intel);
        writer.WriteShort(character.IntelRecharge);

        WriteWeapon(writer, weapon);
    }

    /// <summary>
    /// The <c>with(currentWeapon) event_user(12)</c> closing a FULL_UPDATE
    /// character block.
    /// </summary>
    /// <remarks>
    /// <b>Every class writes one.</b> This said "zero bytes for eight of the ten
    /// classes" and that was wrong: GM8 inherits an event a child does not
    /// define, and every weapon's parent is <c>Weapon</c>, whose
    /// <c>User Event 12</c> writes <c>readyToShoot</c> and <c>alarm[0]</c>. The
    /// client reads them unconditionally
    /// (<c>Weapon.events/User Event 13.xml</c>, a two-byte
    /// <c>receiveCompleteMessage</c>), so a missing byte does not lose a field —
    /// it desynchronises the rest of the stream. See
    /// <c>Gg2.Protocol.StateUpdateReader.ReadWeapon</c> for the measurement.
    /// </remarks>
    private static void WriteWeapon(NetWriter writer, in WeaponBlock weapon)
    {
        // Medigun overrides User Event 12 *without* an inherited action — the
        // only weapon that does — so a medic's block is the heal target alone.
        if (weapon.Class == PlayerClass.Medic)
        {
            // 255, not 0. Zero is roster slot 0, so writing it tells every
            // client the medic is beaming the server's own reserved player.
            writer.WriteUByte(weapon.HealTarget);
            return;
        }

        writer.WriteUByte(weapon.ReadyToShoot);
        writer.WriteUByte(weapon.RefireAlarm);

        if (weapon.Class != PlayerClass.Demoman)
            return;

        // Minegun.events/User Event 12.xml: the count, then one seven-byte
        // record per mine in instance order. The count is a promise, and a
        // client reads the records positionally — so the two must be built from
        // the same walk, which is why CharacterWire fills them together.
        if (weapon.MinesLobbed != weapon.Mines.Length)
        {
            throw new InvalidOperationException(
                $"{weapon.MinesLobbed} mine(s) lobbed but {weapon.Mines.Length} block(s) to write");
        }

        writer.WriteUByte(weapon.MinesLobbed);

        foreach (var mine in weapon.Mines)
        {
            writer.WriteUShort(mine.X);
            writer.WriteUShort(mine.Y);
            writer.WriteByte(mine.HSpeed);
            writer.WriteByte(mine.VSpeed);
            writer.WriteUByte(mine.Stickied);
        }
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
    /// <remarks>
    /// A carried briefcase has no instance, so its count is zero — see
    /// <c>Gg2.Simulation.IntelState</c>. The quantisation is already done, by
    /// the same code the parity harness compares against; see
    /// <see cref="ObjectiveBlock"/>.
    /// </remarks>
    private static void WriteIntel(NetWriter writer, IntelBlock? intel)
    {
        if (intel is not { } block)
        {
            writer.WriteUShort(0);
            return;
        }

        writer.WriteUShort(1);
        writer.WriteUShort(block.X);
        writer.WriteUShort(block.Y);
        writer.WriteShort(block.RecoverTimer);
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
                writer.WriteUByteFromReal(_objective.TimeLimitMinutes);
                writer.WriteUInt((uint)Math.Max(0, _objective.RoundTimer));
                break;

            case GameMode.Invasion:
                writer.WriteUByteFromReal(_objective.TimeLimitMinutes);
                writer.WriteUInt((uint)Math.Max(0, _objective.RoundTimer));
                writer.WriteUShortFromReal(_objective.SetupTimer);
                break;

            case GameMode.Generator:
                writer.WriteUByteFromReal(_objective.TimeLimitMinutes);
                writer.WriteUInt((uint)Math.Max(0, _objective.RoundTimer));

                // ⚠️ Blue first, then red — `with GeneratorBlue event_user(12);
                // with GeneratorRed event_user(12);`, in that order, which is
                // the reverse of every other paired field on the wire.
                WriteGenerator(writer, _objective.BlueGenerator);
                WriteGenerator(writer, _objective.RedGenerator);
                break;

            case GameMode.Arena:
                // ⚠️ Five extra fields on a FULL_UPDATE only, ahead of the
                // scalars every other mode shares. A reader that took them on a
                // CAPS_UPDATE too would be six bytes out.
                if (tier == MessageType.FullUpdate)
                {
                    writer.WriteUByteFromReal(_objective.ArenaRedWins);
                    writer.WriteUByteFromReal(_objective.ArenaBlueWins);
                    writer.WriteUByte(_objective.ArenaState);
                    writer.WriteByte(_objective.ArenaWinner);
                    writer.WriteUShortFromReal(_objective.ArenaEndCount);
                }

                writer.WriteUByteFromReal(_objective.TimeLimitMinutes);
                writer.WriteUInt((uint)Math.Max(0, _objective.RoundTimer));
                writer.WriteUShortFromReal(_objective.ControlPointUnlock);

                // floor(roundStart/2): 300 ticks in one byte, so the client sees
                // the setup countdown to the nearest two ticks.
                writer.WriteUByteFromReal(Gml.Floor(_objective.ArenaRoundStart / 2.0));
                WriteControlPoints(writer);
                break;

            case GameMode.KingOfTheHill:
            case GameMode.DualKingOfTheHill:
                writer.WriteUShortFromReal(_objective.ControlPointUnlock);
                writer.WriteUShortFromReal(_objective.RedTimer);
                writer.WriteUShortFromReal(_objective.BlueTimer);
                WriteControlPoints(writer);
                break;

            case GameMode.ControlPoints:
                writer.WriteUByteFromReal(_objective.TimeLimitMinutes);
                writer.WriteUInt((uint)Math.Max(0, _objective.RoundTimer));
                writer.WriteUShortFromReal(_objective.SetupTimer);
                WriteControlPoints(writer);
                break;

            case GameMode.TeamDeathmatch:
                writer.WriteUByteFromReal(_objective.TimeLimitMinutes);
                writer.WriteUInt((uint)Math.Max(0, _objective.RoundTimer));
                writer.WriteUShortFromReal(_objective.KillLimit);
                break;

            default:
                throw new InvalidOperationException($"no HUD block for {match.GameMode}");
        }
    }

    /// <summary>
    /// <c>Objects/Map elements/Gamemode-specific/CP/ControlPoint.events/User Event 12.xml</c>.
    /// </summary>
    /// <summary>
    /// One block per control point, in order, with no count prefix.
    /// </summary>
    /// <remarks>
    /// The client has the same map loaded and creates the same points from it,
    /// so it knows how many to read — the same self-describing-by-the-map
    /// arrangement the moving platforms use. Already quantised, by
    /// <c>Gg2.Simulation.ControlPoints.ToBlock</c>: <c>team</c> and
    /// <c>cappingTeam</c> are signed bytes whose -1 means neutral, and
    /// <c>capping</c> is <c>round(capping)</c>, GML's half to even.
    /// </remarks>
    private void WriteControlPoints(NetWriter writer)
    {
        foreach (var point in _objective.ControlPoints ?? [])
        {
            writer.WriteByte(point.Team);
            writer.WriteByte(point.CappingTeam);
            writer.WriteUShort(point.Capping);
        }
    }

    /// <summary>
    /// <c>Objects/Map elements/Gamemode-specific/Generator/Generator.events/User Event 12.xml</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A destroyed generator writes nothing at all</b> — not a zeroed
    /// block. The HUD reaches each one through a <c>with</c>, which visits no
    /// instances once it has been destroyed, so the block shortens by four
    /// bytes and a reader has to know. Already quantised, by
    /// <c>Gg2.Simulation.Generators.ToBlock</c>: <c>ceil(hp)</c>, because a
    /// generator on 0.1 hp is still standing and the client must not draw it
    /// dead.
    /// </remarks>
    private static void WriteGenerator(NetWriter writer, GeneratorBlock? generator)
    {
        if (generator is not { } block)
            return;

        writer.WriteUShort(block.Hp);
        writer.WriteUShort(block.ShieldHp);
    }
}

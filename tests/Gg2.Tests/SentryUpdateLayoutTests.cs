using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// A sentry's contribution to each state-update tier.
/// </summary>
/// <remarks>
/// <b>The bug this exists for shipped, and no fixture could see it.</b> An
/// <c>INPUTSTATE</c>-tier update writes <em>nothing at all</em> for a sentry —
/// not the placement and not even the hp — because
/// <c>Sentry.events/User Event 12.xml</c> guards each group of writes on the
/// tier and between them the guards cover only <c>FULL_UPDATE</c> and
/// <c>QUICK_UPDATE</c>. The reader consumed the hp byte unconditionally, so
/// from the first <c>INPUTSTATE</c> tick on which anybody owned a sentry the
/// whole stream slipped one byte per sentry — every tick that is not a quick
/// update (<c>GameServerBeginStep.gml:99</c>).
/// <para>
/// It surfaced as "no framing for server message 0" several hundred bytes
/// downstream, which is nowhere near the cause, and it went unnoticed for six
/// phases because <b>no recording had ever contained an engineer</b>. Every
/// fixture was scout, soldier, demoman and medic. docs/PARITY.md, "when adding
/// a fixture, ask what state it cannot reach".
/// </para>
/// <para>
/// These are hand-built payloads rather than a recording, so they fail on the
/// arithmetic rather than on a downstream symptom: each asserts the reader
/// consumed <em>exactly</em> the bytes the reference wrote.
/// </para>
/// </remarks>
public sealed class SentryUpdateLayoutTests
{
    /// <summary>0x01 has a character, 0x02 has a sentry.</summary>
    private const byte CharacterAndSentry = 0x03;

    /// <summary>Bit 7 set means built; the low seven bits are min(127, hp).</summary>
    private const byte BuiltWith90Hp = 0x80 | 90;

    private static WorldShape Shape => GameModes.ShapeOf(
        LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

    /// <summary>
    /// One player block for a class with no weapon state of its own, at the
    /// given tier, owning a built sentry.
    /// </summary>
    private static byte[] Payload(MessageType tier, bool withSentryHp)
    {
        var writer = new NetWriter();

        // serializeState: the tier byte is consumed by the caller, so the
        // payload starts at the roster size. FULL_UPDATE would prefix the tdm
        // invulnerability ushort; these two tiers do not.
        writer.WriteUByte(1);

        writer.WriteUByte(CharacterAndSentry);

        // Character.events/User Event 12: keys, aim direction, aim distance on
        // every tier, then position and the rest on QUICK_UPDATE and above.
        writer.WriteUByte(0);
        writer.WriteUShort(0);
        writer.WriteUByte(0);

        if (tier == MessageType.QuickUpdate)
        {
            writer.WriteUShort(1234); // x
            writer.WriteUShort(5678); // y
            writer.WriteByte(0);      // hspeed
            writer.WriteByte(0);      // vspeed
            writer.WriteUByte(100);   // hp
            writer.WriteUByte(6);     // ammo
            writer.WriteUByte(0);     // flags
        }

        if (withSentryHp)
            writer.WriteUByte(BuiltWith90Hp);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// On an <c>INPUTSTATE</c>, a sentry contributes <b>zero</b> bytes — the
    /// player's block ends at its <c>subobjects</c> byte with bit 1 set.
    /// </summary>
    [Fact]
    public void AnInputstateCarriesNoSentryBytesAtAll()
    {
        var payload = Payload(MessageType.InputState, withSentryHp: false);
        var reader = new NetReader(payload);

        var update = StateUpdateReader.Read(
            ref reader, MessageType.InputState, Shape, [PlayerClass.Engineer]);

        // The sentry is announced and then says nothing.
        Assert.Equal(CharacterAndSentry, update.Players[0].Subobjects);
        var sentry = Assert.NotNull(update.Players[0].Sentry);
        Assert.False(sentry.HasHp);
        Assert.False(sentry.HasPlacement);

        // The assertion that actually bites: reading one byte too many here is
        // what desynchronised every stream containing an engineer.
        Assert.Equal(payload.Length, reader.Position);
    }

    /// <summary>
    /// On a <c>QUICK_UPDATE</c> it contributes exactly one: built-and-hp.
    /// </summary>
    [Fact]
    public void AQuickUpdateCarriesTheSentrysHpAndNothingElse()
    {
        var payload = Payload(MessageType.QuickUpdate, withSentryHp: true);
        var reader = new NetReader(payload);

        var update = StateUpdateReader.Read(
            ref reader, MessageType.QuickUpdate, Shape, [PlayerClass.Engineer]);

        var sentry = Assert.NotNull(update.Players[0].Sentry);
        Assert.True(sentry.HasHp);
        Assert.True(sentry.Built);
        Assert.Equal(90, sentry.Hp);
        Assert.False(sentry.HasPlacement);
        Assert.Null(sentry.VSpeed);

        Assert.Equal(payload.Length, reader.Position);
    }

    /// <summary>
    /// The control: the two tiers really do differ by exactly one byte per
    /// sentry, which is the whole bug.
    /// </summary>
    /// <remarks>
    /// Asserted as a difference rather than as two absolute widths, because an
    /// absolute width would also have to encode the character block and would
    /// then fail for reasons that have nothing to do with sentries.
    /// </remarks>
    /// <summary>
    /// The real writer against the real reader, with a real sentry: a falling
    /// one carries placement, hp and a vspeed byte on a FULL_UPDATE, and a
    /// built one drops the vspeed byte — the reader keys the block's width off
    /// bit 7 of the byte the writer packed.
    /// </summary>
    /// <remarks>
    /// The hand-built payloads above pin the reader; this pins the writer.
    /// Until the sentry simulated, nothing wrote these bytes at all — the
    /// subobject bit was hardcoded to "character only" — so deleting
    /// <c>StateUpdateWriter.WriteSentry</c> makes this the test that goes red.
    /// </remarks>
    [Fact]
    public void TheRealWriterRoundTripsAFallingAndABuiltSentry()
    {
        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort"));
        var roster = new Roster("server");
        roster.Add(new RosterEntry { Name = "engie", Team = Team.Red, Class = PlayerClass.Engineer });
        var match = new MatchState(map, roster, new ServerOptions(), GameModes.TotalAreas(map));

        var state = default(WorldState);
        var character = CharacterState.Fresh(PlayerClass.Engineer, Team.Red);
        character.X = 100;
        character.Y = 200;
        state.Characters[1] = character;
        state.Weapons[1] = WeaponState.Spawn(PlayerClass.Engineer, 100, 200);
        state.Alive[1] = true;

        state.HasSentry[1] = true;
        state.Sentries[1] = SentryState.Build(Team.Red, 300.26, 400.1, -1);
        state.Sentries[1].Motion.VSpeed = 3.7;

        var captured = state;

        bool Characters(int slot, out CharacterBlock block, out WeaponBlock weapon)
        {
            if (!captured.IsAlive(slot))
            {
                block = default;
                weapon = default;
                return false;
            }

            block = CharacterWire.ToBlock(captured.Characters[slot], captured.Weapons[slot]);
            weapon = CharacterWire.ToWeaponBlock(captured, slot);
            return true;
        }

        bool Sentry(int slot, out SentryBlock block)
        {
            block = captured.HasSentry[slot] ? CharacterWire.ToSentryBlock(captured.Sentries[slot]) : default;
            return captured.HasSentry[slot];
        }

        var stateWriter = new StateUpdateWriter(
() => match, Characters, Sentry);

        var writer = new NetWriter();
        stateWriter.Write(writer, MessageType.FullUpdate);

        var reader = new NetReader(writer.WrittenSpan[1..]);
        var update = StateUpdateReader.Read(
            ref reader, MessageType.FullUpdate, Shape, [PlayerClass.Scout, PlayerClass.Engineer]);

        Assert.Equal(writer.WrittenSpan.Length - 1, reader.Position);

        var falling = Assert.NotNull(update.Players[1].Sentry);
        Assert.True(falling.HasPlacement);
        Assert.Equal(-1, falling.StartDirection);
        Assert.Equal(1501, falling.X);         // round(300.26 * 5) = round(1501.3)
        Assert.Equal(2000, falling.Y);         // round(400.1 * 5) = round(2000.5), half to even
        Assert.False(falling.Built);
        Assert.Equal(25, falling.Hp);
        Assert.Equal((byte)4, falling.VSpeed); // write_ubyte(3.7), half away from zero

        // Built: same world, finished sentry — the vspeed byte disappears and
        // bit 7 appears.
        captured.Sentries[1].Built = true;
        captured.Sentries[1].Motion.VSpeed = 0;
        captured.Sentries[1].Hp = SentryState.MaxHp;

        var builtWriter = new NetWriter();
        stateWriter.Write(builtWriter, MessageType.QuickUpdate);

        var builtReader = new NetReader(builtWriter.WrittenSpan[1..]);
        var builtUpdate = StateUpdateReader.Read(
            ref builtReader, MessageType.QuickUpdate, Shape, [PlayerClass.Scout, PlayerClass.Engineer]);

        Assert.Equal(builtWriter.WrittenSpan.Length - 1, builtReader.Position);

        var built = Assert.NotNull(builtUpdate.Players[1].Sentry);
        Assert.False(built.HasPlacement);
        Assert.True(built.Built);
        Assert.Equal(100, built.Hp);
        Assert.Null(built.VSpeed);
    }

    [Fact]
    public void ASentryCostsOneByteMoreOnAQuickUpdateThanOnAnInputstate()
    {
        var withSentry = Payload(MessageType.InputState, withSentryHp: false).Length;

        var writer = new NetWriter();
        writer.WriteUByte(1);
        writer.WriteUByte(0x01); // a character, no sentry
        writer.WriteUByte(0);
        writer.WriteUShort(0);
        writer.WriteUByte(0);
        var withoutSentry = writer.WrittenSpan.Length;

        Assert.Equal(0, withSentry - withoutSentry);
    }
}

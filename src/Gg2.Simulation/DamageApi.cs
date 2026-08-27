using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// <c>Scripts/Damage API/dealDamage.gml</c>: the one line every weapon's damage
/// eventually goes through, and the one guard in front of it.
/// </summary>
/// <remarks>
/// <b>Three lines of GML, and for six phases the port inlined them at seven call
/// sites because the guard could not fire.</b> It reads
/// <c>variable_local_exists("deathmatch_invulnerable")</c> and then
/// <c>!= 0</c> — false for a sentry and a generator, which have no such
/// variable, and zero for every character outside Team Deathmatch
/// (<c>Character.events/Create.xml:203</c>). Team Deathmatch makes it live, so
/// the sites are routed through here instead of each growing a copy of it.
/// <para>
/// <c>execute_string(global.dealDamageFunction, ...)</c> follows it and is the
/// empty string unless a mod sets one (<c>Scripts/Game/game_init.gml:356</c>),
/// so there is nothing to transcribe.
/// </para>
/// </remarks>
public static class DamageApi
{
    /// <summary>
    /// <c>dealDamage(sourcePlayer, damagedCharacter, damageDealt)</c>.
    /// </summary>
    /// <returns>
    /// Whether the damage landed. False means the victim was invulnerable —
    /// which is <b>not</b> the same as no damage having been attempted, and
    /// callers that do bookkeeping around the subtraction still do it. The
    /// reference's own <c>damageCharacter</c> is the proof: it calls this and
    /// then resets <c>timeUnscathed</c> unconditionally
    /// (<c>damageCharacter.gml:7</c>), so an invulnerable target loses its
    /// healing ramp to a shot that did nothing to it.
    /// </returns>
    public static bool Deal(ref CharacterState victim, double amount)
    {
        // `if(argument1.deathmatch_invulnerable != 0) return 0;`
        //
        // ⚠️ GML's `!=` is fuzzy like every other real comparison, so this is
        // `!Gml.Equal` and not `!=`. It rarely matters — End Step's clamp is
        // what normally makes the value an exact zero — but the clamp is fuzzy
        // for the same reason, and writing either of them raw leaves a
        // permanently invulnerable character on a 7e-14 residue. See
        // CharacterPhysics.EndStep.
        if (!Gml.Equal(victim.DeathmatchInvulnerable, 0))
            return false;

        victim.Hp -= amount;
        return true;
    }
}

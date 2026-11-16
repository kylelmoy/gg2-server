using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// Hold fire at a target that is still under Team Deathmatch's spawn protection.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key, not the shot, ends a character's own protection</b>
/// (<c>Character.events/Begin Step.xml:85</c> for <c>Attack</c>, <c>:124</c> for
/// <c>Special</c>; <see cref="Weapons"/>). So a bot that opens fire on a protected
/// target pays twice: its own protection is gone that tick, and the volley does
/// nothing (<see cref="DamageApi"/> refuses damage to a protected victim). A
/// human who stands still keeps 30 units at 0.2 a tick for 150 ticks; measured on
/// <c>duel_01</c>, every class's bot pressed <c>Attack</c> 15-20 ticks after
/// spawning, emptied a clip into nothing, and the human got the first free hits
/// on an unprotected bot.
/// </para>
/// <para>
/// <b>Aim is untouched</b>, so a bot still tracks its target and fires the tick
/// the protection ends (the human firing ends it too). <c>Attack</c> is held
/// always; <c>Special</c> is held for a class whose special is a shot
/// (<see cref="BotClassProfile.SpecialIsFire"/>), and for every class while the
/// bot is itself still protected, because nothing can hurt it then and a reaction
/// (airblast, detonation, cloak) would only spend its protection.
/// </para>
/// <para>
/// Nothing outside Team Deathmatch is ever protected (<c>WorldState.Spawn</c>),
/// nor is anything in a world with <c>TdmInvulnerabilityTicks</c> 0, which is
/// every headless match, scenario and training rollout here — so this changes no
/// pinned measurement, and a pin that moves because of it is an alarm.
/// </para>
/// </remarks>
public static class BotSpawnProtection
{
    /// <summary>Is <paramref name="target"/> a living character that damage cannot reach yet.</summary>
    public static bool Shielded(in WorldView world, in BotTarget target) =>
        target.Kind == BotTargetKind.Character
        && world.IsAlive(target.Slot)
        && !Gml.Equal(world.Character(target.Slot).DeathmatchInvulnerable, 0);

    /// <summary>
    /// <paramref name="keys"/> without the keys that would end this bot's own
    /// protection for nothing, if its target is shielded.
    /// </summary>
    public static PlayerKeys Hold(PlayerKeys keys, in WorldView world, int slot, in BotTarget target)
    {
        if (!Shielded(in world, in target))
            return keys;

        ref readonly var self = ref world.Character(slot);
        keys &= ~PlayerKeys.Attack;

        if (BotClassProfile.SpecialIsFire(self.Class) || !Gml.Equal(self.DeathmatchInvulnerable, 0))
            keys &= ~PlayerKeys.Special;

        return keys;
    }

    /// <summary>
    /// <see cref="Hold(PlayerKeys, in WorldView, int, in BotTarget)"/> for a
    /// controller that picks no target of its own: the scripted layer's target,
    /// or the nearest enemy this bot may oppose when it has none.
    /// </summary>
    /// <remarks>
    /// The fallback is not optional. A net fires on its own observation, and on
    /// <c>duel_01</c> the soldier's pressed <c>Attack</c> four ticks into a life,
    /// before the scripted layer had acquired anybody.
    /// </remarks>
    public static PlayerKeys HoldNearest(PlayerKeys keys, in WorldView world, int slot, in BotTarget target)
    {
        if (target.Kind == BotTargetKind.Character)
            return Hold(keys, in world, slot, in target);

        Span<int> nearest = stackalloc int[1];

        if (world.NearestEnemies(slot, nearest) == 0)
            return keys;

        ref readonly var enemy = ref world.Character(nearest[0]);

        return Hold(keys, in world, slot,
            new BotTarget(BotTargetKind.Character, nearest[0], enemy.X, enemy.Y, Visible: false));
    }
}

using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// <c>botStepSafe</c>: can this character take one step sideways and still be
/// standing on something?
/// </summary>
/// <remarks>
/// Two questions, both asked with the character's own box at the position it
/// would occupy: is there a wall in the way, and is there a floor under it.
/// <para>
/// <b>It exists for the engage hold's close-in step, which is the only place in
/// the bot layer that presses a movement key no route validated.</b> Everything
/// else that moves a bot horizontally is either following an edge the nav
/// generator proved or backing off a single step inside its own blast radius; a
/// pyro closing on a target is neither, so it is walking on ground nothing has
/// checked. On a map with a pit beside the route — which is not hypothetical, see
/// <c>ctf_truefort</c> — a bot chasing an enemy across a gap walks into a hole it
/// cannot climb out of and stays there until it dies.
/// </para>
/// <para>
/// <b>The drop tolerance is deliberately short.</b> This is not "can the bot
/// get there", which is <see cref="NavSearch"/>'s question and is answered against
/// the graph; it is only "does the ground continue", so a step down to a lower
/// ledge reads as safe and a step over a pit does not.
/// </para>
/// </remarks>
public static class BotStep
{
    /// <summary>
    /// Whether one <see cref="BotConstants.EngageStep"/> in <paramref name="dir"/>
    /// (-1 left, +1 right) lands somewhere with ground under it.
    /// </summary>
    public static bool Safe(CollisionWorld collision, in CharacterState self, int dir)
    {
        ArgumentNullException.ThrowIfNull(collision);

        var ahead = self.X + (dir * BotConstants.EngageStep);

        // Obstacle ALONE — the reference asks `place_meeting(x, y, Obstacle)`
        // rather than `place_free`, because in GM8 `place_free` reads the `solid`
        // flag and the game toggles that flag underneath itself (gunSetSolids
        // brackets every hitscan trace), so what it answers depends on where in
        // the frame it is asked. This port has no global flag to toggle, but the
        // object list still has to be the right one: a gate or a player wall is
        // not a floor to stand on and is not a wall worth refusing either.
        // CollisionFilter.ForSentry() is this codebase's name for that list —
        // `wallSetSolid`, Obstacle and nothing else.
        var obstacles = CollisionFilter.ForSentry();

        // A wall where the bot would stand: not unsafe, but not worth pressing
        // into either — the press achieves nothing and the follower's stuck
        // detector counts it.
        if (!collision.PlaceFree(self.Box, ahead, self.Y, in obstacles))
            return false;

        // ...and something to stand on within the drop tolerance.
        return !collision.PlaceFree(
            self.Box, ahead, self.Y + BotConstants.EngageDrop, in obstacles);
    }
}

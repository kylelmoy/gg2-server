using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// Where one character comes down if it holds a key: a copy of it stepped
/// through the real per-tick physics against the map.
/// </summary>
/// <remarks>
/// <b>The same calls, in the same order, as <c>MatchSimulation</c> makes for a
/// character</b>: <see cref="CharacterState.ApplyInput"/>, then
/// <see cref="CharacterPhysics.BeginStepInput"/>,
/// <see cref="CharacterPhysics.BeginStepAccelerate"/>,
/// <see cref="CharacterPhysics.Step"/>, <see cref="CharacterPhysics.HeavyStep"/>
/// and <see cref="CharacterPhysics.EndStep(CollisionWorld, ref CharacterState)"/>.
/// So a flight that touches only the map is the flight the world will fly, to the
/// bit; <c>FlightPredictorTests</c> holds it to that.
/// <para>
/// ⚠️ <b>What it does not see</b>: anything outside one character and the
/// collision world. Moving platforms, move boxes, kill boxes, blasts and other
/// bodies all run in passes of their own, so a flight across or into one is a
/// guess. The callers act on a prediction only where a wrong one costs a fall
/// back to the path follower's own machinery.
/// </para>
/// <para>
/// <c>gg2-rl</c> <c>docs/PLAN.md</c>, navigation, N1.
/// </para>
/// </remarks>
public static class FlightPredictor
{
    /// <summary>The longest flight followed: a jump off the top of a tall map lands within this.</summary>
    public const int MaxTicks = 90;

    /// <summary>
    /// Fly a copy of <paramref name="start"/> holding <paramref name="dir"/>
    /// (−1 left, 0 neither, 1 right), pressing jump on the first tick if
    /// <paramref name="jump"/>.
    /// </summary>
    /// <returns>
    /// Whether it came back to the ground within <see cref="MaxTicks"/>, alive,
    /// after leaving it. A requested jump that never leaves the ground is a
    /// <c>false</c>, so a caller cannot mistake a refused press for a landing
    /// where it stood.
    /// </returns>
    public static bool Land(
        CollisionWorld world, in CharacterState start, int dir, bool jump,
        out double x, out double y, out int ticks) =>
        Land(world, in start, dir, dir, jump, out x, out y, out ticks);

    /// <summary>
    /// As <see cref="Land(CollisionWorld, in CharacterState, int, bool, out double, out double, out int)"/>,
    /// with a different key on the first tick: a take-off pressed toward the
    /// landing and then let go, or reversed.
    /// </summary>
    public static bool Land(
        CollisionWorld world, in CharacterState start, int firstDir, int dir, bool jump,
        out double x, out double y, out int ticks) =>
        Land(world, in start, firstDir, dir, jump, 0, out x, out y, out ticks);

    /// <summary>
    /// As the overloads above, pressing jump again on tick
    /// <paramref name="rejump"/> (1 is the first tick stepped; 0 never): a
    /// scout's second jump, if it has one left.
    /// </summary>
    /// <remarks>
    /// A press, not a hold, exactly as the world takes it: a press on the tick
    /// after another is held, not pressed, and changes nothing. N3.
    /// </remarks>
    public static bool Land(
        CollisionWorld world, in CharacterState start, int firstDir, int dir, bool jump, int rejump,
        out double x, out double y, out int ticks) =>
        Land(world, in start, firstDir, dir, 1, dir, jump, rejump, out x, out y, out ticks);

    /// <summary>
    /// As the overloads above, with <paramref name="firstDir"/> on tick 1,
    /// <paramref name="earlyDir"/> up to tick <paramref name="earlyTicks"/>, and
    /// <paramref name="dir"/> after: up first and over after, the way a person
    /// gets onto a ledge (N4).
    /// </summary>
    public static bool Land(
        CollisionWorld world, in CharacterState start, int firstDir, int earlyDir, int earlyTicks, int dir, bool jump,
        int rejump, out double x, out double y, out int ticks)
    {
        var c = start;

        // Whatever else is held is held through the flight: a heavy firing its
        // minigun runs slower, and a prediction made without the trigger landed
        // a firing heavy on a node it never reached (BotUnderFireTests).
        var kept = start.KeyState & (PlayerKeys.Attack | PlayerKeys.Special);
        var first = Key(firstDir) | kept;
        var early = Key(earlyDir) | kept;
        var keys = Key(dir) | kept;
        var airborne = !c.OnGround;

        for (var t = 1; t <= MaxTicks; t++)
        {
            var held = (t == 1 ? first : t <= earlyTicks ? early : keys)
                | ((jump && t == 1) || t == rejump ? PlayerKeys.Jump : PlayerKeys.None);
            c.ApplyInput(new PlayerInput(held, c.NetAimDirection, c.AimDistance));

            CharacterPhysics.BeginStepInput(world, ref c);
            CharacterPhysics.BeginStepAccelerate(world, ref c);
            CharacterPhysics.Step(world, ref c);
            CharacterPhysics.HeavyStep(ref c);

            // True means it died (out of the map, or out of health).
            if (CharacterPhysics.EndStep(world, ref c))
                break;

            if (!c.OnGround)
            {
                airborne = true;
            }
            else if (airborne)
            {
                x = c.X;
                y = c.Y;
                ticks = t;
                return true;
            }
            else if (jump && t >= 2)
            {
                break;
            }
        }

        x = c.X;
        y = c.Y;
        ticks = 0;
        return false;
    }

    /// <summary>
    /// One tick of <paramref name="c"/> holding <paramref name="dir"/> and no
    /// jump, with the same held keys a flight keeps.
    /// </summary>
    /// <returns>Whether it is still alive.</returns>
    public static bool Run(CollisionWorld world, ref CharacterState c, int dir)
    {
        var kept = c.KeyState & (PlayerKeys.Attack | PlayerKeys.Special);
        c.ApplyInput(new PlayerInput(Key(dir) | kept, c.NetAimDirection, c.AimDistance));

        CharacterPhysics.BeginStepInput(world, ref c);
        CharacterPhysics.BeginStepAccelerate(world, ref c);
        CharacterPhysics.Step(world, ref c);
        CharacterPhysics.HeavyStep(ref c);

        return !CharacterPhysics.EndStep(world, ref c);
    }

    private static PlayerKeys Key(int dir) => dir > 0 ? PlayerKeys.Right : dir < 0 ? PlayerKeys.Left : PlayerKeys.None;
}

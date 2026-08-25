using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The character update, transcribed from
/// <c>Objects/InGameElements/Character.events/{Begin Step, Step, End Step}.xml</c>
/// at a fixed 30 Hz.
/// </summary>
/// <remarks>
/// <b>The phase split is kept because it is observable.</b> GM8 runs Begin Step
/// for every instance, then Step for every instance, then End Step — so a
/// character's Begin Step sees every other character's <em>previous</em>
/// end-of-tick state (docs/SIMULATION.md, "GM8 semantics that will bite"). A
/// version that ran all three phases per character in turn would be a different
/// simulation.
/// <para>
/// <b>What is not here yet.</b> Weapons, taunting, cloak, the sandvich, uber,
/// afterburn, damage, intel timers, capture zones and animation are phase 5 and
/// 6. Everything omitted is either presentation or something that cannot move a
/// character; where an omission <em>could</em> move one, it is called out at the
/// site.
/// </para>
/// <para>
/// The GM8 automatic-movement dance is gone. The engine adds
/// <c>hspeed</c>/<c>vspeed</c> to the position between Step and End Step, which
/// is why <c>Step.xml</c> ends with <c>x -= hspeed; y -= vspeed;</c> — those two
/// lines exist purely to cancel it. Here the movement is applied once and both
/// lines are deleted.
/// </para>
/// </remarks>
public static class CharacterPhysics
{
    /// <summary>Runs all three phases for one character. Convenience for tests.</summary>
    /// <remarks>
    /// A real world runs each phase across every character before starting the
    /// next, which is not the same thing as soon as characters interact.
    /// </remarks>
    public static void Tick(CollisionWorld world, ref CharacterState character)
    {
        BeginStep(world, ref character);
        Step(world, ref character);
        EndStep(world, ref character);
    }

    /// <summary>
    /// Input, jumping, acceleration and friction, and the gravity accumulation.
    /// </summary>
    public static void BeginStep(CollisionWorld world, ref CharacterState character)
    {
        var stats = character.Stats;
        var box = character.Box;
        var filter = character.Filter;

        var stuckInWall = !world.PlaceFree(box, character.X, character.Y, filter);
        character.ObstacleBelow = !world.PlaceFree(box, character.X, character.Y + 1, filter);

        character.OnGround = false;
        var onNonSurfingGround = false;

        // Movement reads the union, so a key tapped and released inside one tick
        // still counts.
        var downOrPressed = character.KeyState | character.PressedKeys;

        // Real-to-real comparisons go through Gml throughout this file: GML's
        // are fuzzy to 1e-13 and C#'s are not, and `vspeed == 0` below decides
        // whether a character walks down a step (see Gml.Equal).
        if (Gml.GreaterOrEqual(character.VSpeed, 0) && character.ObstacleBelow)
        {
            character.OnGround = true;
            onNonSurfingGround = true;
        }

        // Dropdown platforms are the other way to be on the ground. Not
        // implemented: no shipped map has a MovingPlatform, and the standalone
        // dropdown platforms need place_meeting. Phase 3 left both out
        // deliberately (docs/PLAN.md).

        if (onNonSurfingGround)
            character.MoveStatus = 0;

        if (character.OnGround)
            character.DoubleJumpUsed = false;

        // Jump fires on a fresh press, not on the key being held — so holding
        // jump does not bunny-hop. The reference also accepts a queued jump
        // (the client's queueJump setting), which is not modelled; a queue-jump
        // client would jump a tick later here than it should.
        //
        // The vspeed guard stops a jump re-launching a character already rising
        // faster than a jump would.
        if ((character.PressedKeys & PlayerKeys.Jump) != 0
            && Gml.Greater(character.VSpeed, -ClassStats.BaseJumpStrength))
        {
            if (character.OnGround && !stuckInWall)
            {
                character.WantToJump = false;
                character.VSpeed = -ClassStats.BaseJumpStrength;
                character.OnGround = false;
            }
            else if (stats.CanDoubleJump && !character.DoubleJumpUsed)
            {
                character.WantToJump = false;
                character.VSpeed = -ClassStats.BaseJumpStrength;
                character.OnGround = false;
                character.DoubleJumpUsed = true;
                character.MoveStatus = 0;
            }
        }

        var (controlFactor, frictionFactor) = ControlAndFriction(character);
        var rocketJumping = character.MoveStatus is 1 or 2 or 4;

        character.Controlling = false;

        // The frameskip loop. Running acceleration and friction twice with a
        // half-strength factor is NOT the same number as running them once, and
        // that is the entire reason the loop exists
        // (Documentation/RateController.md). Do not "simplify" it away.
        for (var skip = 0; skip < 2; skip++)
        {
            if ((downOrPressed & PlayerKeys.Left) != 0 && Gml.GreaterOrEqual(character.HSpeed, -stats.BaseMaxSpeed))
            {
                character.HSpeed -= stats.RunPower * controlFactor * 0.5;
                character.Controlling = true;
            }

            if ((downOrPressed & PlayerKeys.Right) != 0 && Gml.LessOrEqual(character.HSpeed, stats.BaseMaxSpeed))
            {
                character.HSpeed += stats.RunPower * controlFactor * 0.5;
                character.Controlling = true;
            }

            // Both directions cancel: pressing left and right is not "in
            // control", it is standing still.
            if ((downOrPressed & (PlayerKeys.Left | PlayerKeys.Right)) == (PlayerKeys.Left | PlayerKeys.Right))
                character.Controlling = false;

            // Way too fast, or being pushed while under the cap: use the base
            // friction rather than the moveStatus's. This is what stops a rocket
            // jump's low friction from making a running player unstoppable.
            var overspeed = Gml.Greater(Math.Abs(character.HSpeed), stats.BaseMaxSpeed * 2);
            var pushingUnderCap = (downOrPressed & (PlayerKeys.Left | PlayerKeys.Right)) != 0
                                  && Gml.Less(Math.Abs(character.HSpeed), stats.BaseMaxSpeed);

            character.HSpeed /= ClassStats.DeltaMultSkip(
                overspeed || pushingUnderCap ? ClassStats.BaseFriction : frictionFactor);
        }

        character.PressedKeys = PlayerKeys.None;
        character.ReleasedKeys = PlayerKeys.None;

        // Anti ice-skating.
        if (Gml.Less(Math.Abs(character.HSpeed), ClassStats.RestSpeed) && !character.Controlling)
            character.HSpeed = 0;

        if (!character.OnGround && !stuckInWall)
            character.AppliedGravity += rocketJumping ? ClassStats.RocketJumpGravity : ClassStats.Gravity;
    }

    /// <summary>
    /// The speed clamp, the split gravity, and the move itself.
    /// </summary>
    public static void Step(CollisionWorld world, ref CharacterState character)
    {
        var box = character.Box;
        var filter = character.Filter;

        // Per axis, independently: clamping the vector would let one component
        // shrink the other.
        // min() and max() do NOT take the epsilon — measured — but sign() does,
        // so a speed inside it is zeroed here rather than kept.
        character.HSpeed = Math.Min(Math.Abs(character.HSpeed), ClassStats.MaxSpeed) * Gml.Sign(character.HSpeed);
        character.VSpeed = Math.Min(Math.Abs(character.VSpeed), ClassStats.MaxSpeed) * Gml.Sign(character.VSpeed);

        UpdateSpinjump(world, ref character);

        // HALF the gravity, before the move. The other half goes on after it,
        // and that split is what makes the arc framerate-independent — it is
        // not stylistic, it defines the trajectory.
        character.VSpeed += character.AppliedGravity / 2;
        if (Gml.Greater(character.VSpeed, ClassStats.TerminalVSpeed))
            character.VSpeed = ClassStats.TerminalVSpeed;

        var target = world.PlaceFree(box, character.X + character.HSpeed, character.Y + character.VSpeed, filter);

        if (!target)
        {
            var mover = new MoverState
            {
                X = character.X,
                Y = character.Y,
                HSpeed = character.HSpeed,
                VSpeed = character.VSpeed,
                MoveStatus = character.MoveStatus,
            };

            CharacterHitObstacle.Resolve(world, box, filter, ref mover);

            character.X = mover.X;
            character.Y = mover.Y;
            character.HSpeed = mover.HSpeed;
            character.VSpeed = mover.VSpeed;
            character.MoveStatus = mover.MoveStatus;
        }
        else
        {
            character.X += character.HSpeed;
            character.Y += character.VSpeed;
        }

        // The other half — but only if there is still air below. Landing eats
        // it, which is why a character does not accumulate downward speed while
        // standing.
        if (world.PlaceFree(box, character.X, character.Y + 1, filter))
            character.VSpeed += character.AppliedGravity / 2;

        if (Gml.Greater(character.VSpeed, ClassStats.TerminalVSpeed))
            character.VSpeed = ClassStats.TerminalVSpeed;

        character.AppliedGravity = 0;

        // Dropdown platform push-out goes here. Not implemented; see BeginStep.

        // The GM8 automatic-movement round trip, and it is NOT decoration.
        //
        // Step.xml ends with `x -= hspeed; y -= vspeed;` purely to cancel the
        // move GM8 makes on its own between Step and End Step. It is tempting to
        // delete both halves as a no-op — but subtracting a value from a double
        // and adding it straight back is not the identity in IEEE arithmetic. It
        // loses a ULP whenever the intermediate needs more precision than the
        // result: 511.490625 with vspeed -2.3000000000000034 comes back as
        // 511.49062499999997, and the reference is running on that value.
        //
        // Note which hspeed/vspeed: the ones as of the END of Step, after the
        // second half of gravity and the terminal clamp above.
        character.X = character.X - character.HSpeed + character.HSpeed;
        character.Y = character.Y - character.VSpeed + character.VSpeed;
    }

    /// <summary>
    /// Stair descent, the facing, and the clamp into the map.
    /// </summary>
    /// <remarks>
    /// The weaponless overload, for the tests and callers that have no weapon to
    /// move. A real character always has one — <c>Character.events/Create.xml</c>
    /// makes it — so the live path is the three-argument form.
    /// </remarks>
    public static void EndStep(CollisionWorld world, ref CharacterState character)
    {
        var discarded = default(WeaponState);
        EndStep(world, ref character, ref discarded);
    }

    /// <summary>
    /// Stair descent, the facing, the weapon that trails the character, and the
    /// clamp into the map.
    /// </summary>
    public static void EndStep(CollisionWorld world, ref CharacterState character, ref WeaponState weapon)
    {
        var box = character.Box;
        var filter = character.Filter;

        // Climbing down stairs. Only when not falling — otherwise every fall
        // would snap to the next ledge down.
        if (Gml.Equal(character.VSpeed, 0))
        {
            if (world.PlaceFree(box, character.X, character.Y + 6, filter))
            {
                if (!world.PlaceFree(box, character.X, character.Y + 7, filter))
                {
                    character.Y += 6;
                }
                else if (Gml.Greater(character.Speed, 6)
                         && world.PlaceFree(box, character.X, character.Y + 12, filter)
                         && !world.PlaceFree(box, character.X, character.Y + 13, filter))
                {
                    // The 12px variant only at speed, so a fast run does not
                    // launch off a two-cell step.
                    character.Y += 12;
                }
            }
        }

        if (Gml.Greater(character.Hp, character.Stats.MaxHp))
            character.Hp = character.Stats.MaxHp;

        // Facing, from the aim. Note this is the only thing that writes Facing,
        // and the spinjump test in Step compares against the previous value.
        character.Facing = (character.AimDirection + 270) % 360 > 180 ? 1 : -1;

        // The weapon catches up, at round(x). Its position here is where the
        // *next* tick's shot will leave from, because a weapon fires in Begin
        // Step — see WeaponState.X. It happens before the clamp below, which is
        // the reference's order (End Step.xml:64-65, then 67-79).
        Weapons.FollowOwner(character, ref weapon);

        // Clamp into the map. map_width() measures the ART, not the walkmask —
        // on gg_debug those differ (docs/SIMULATION.md).
        // Written out as four tests rather than a clamp, because that is what
        // the reference does and GML's comparisons carry the epsilon.
        if (Gml.Less(character.X, 0))
            character.X = 0;
        if (Gml.Greater(character.X, world.Width))
            character.X = world.Width;
        if (Gml.Less(character.Y, 0))
            character.Y = 0;
        if (Gml.Greater(character.Y, world.Height))
            character.Y = world.Height;
    }

    /// <summary>
    /// Which control and friction factors this character's <c>moveStatus</c>
    /// selects.
    /// </summary>
    private static (double Control, double Friction) ControlAndFriction(in CharacterState character) =>
        character.MoveStatus switch
        {
            // The two odd-looking literals are GM8's own doubles for 0.65 and
            // 0.35, which it parses one ULP above the IEEE nearest — see Gml.
            1 => (Gml.PointSixFive, 1.0),   // rocketing myself
            2 => (0.45, 1.05),              // rocketed by an enemy
            3 => (Gml.PointThreeFive, 1.05), // airblast
            4 => (ClassStats.BaseControl, 1.0), // rocketed by a team-mate

            // Humiliation costs 0.2 of control and is not modelled yet; intel
            // costs 0.1 and is.
            _ => (character.CarryingIntel ? ClassStats.BaseControl - 0.1 : ClassStats.BaseControl,
                  ClassStats.BaseFriction),
        };

    /// <summary>
    /// Spinjumping: rubbing a wall while turning against your own movement
    /// cancels a tick of gravity.
    /// </summary>
    /// <remarks>
    /// An emergent trick the reference emulates deliberately. The test is that
    /// the facing changed <em>against</em> the direction of travel while touching
    /// a wall — which is what a player does by flicking the mouse across
    /// themselves mid-jump.
    /// </remarks>
    private static void UpdateSpinjump(CollisionWorld world, ref CharacterState character)
    {
        var box = character.Box;
        var filter = character.Filter;

        var turned = Gml.Sign(character.HSpeed) > 0
            ? character.LastFacing > character.Facing
            : character.LastFacing < character.Facing;

        var rubbingWall = !world.PlaceFree(
            box, character.X + Gml.Sign(character.HSpeed), character.Y, filter);

        character.Spinjumping = turned && rubbingWall;
        character.LastFacing = character.Facing;

        if (!character.Spinjumping)
            return;

        var gravity = character.MoveStatus is 1 or 2 or 4
            ? ClassStats.RocketJumpGravity
            : ClassStats.Gravity;

        var valid = world.PlaceFree(box, character.X, character.Y - gravity, filter);
        var airborneOrRising = world.PlaceFree(box, character.X, character.Y + 1, filter)
                               || Gml.Less(character.VSpeed, 0);

        if (valid && airborneOrRising)
            character.AppliedGravity -= gravity;
        else
            character.Spinjumping = false;
    }
}

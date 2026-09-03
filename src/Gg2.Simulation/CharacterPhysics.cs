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
    /// <remarks>
    /// Kept as one call for the callers that have nothing to do between the two
    /// halves. <see cref="MatchSimulation"/> uses the split pair instead,
    /// because a demoman's right-click goes between them — see
    /// <see cref="BeginStepAccelerate"/>.
    /// </remarks>
    public static void BeginStep(CollisionWorld world, ref CharacterState character)
    {
        BeginStepInput(world, ref character);
        BeginStepAccelerate(world, ref character);
    }

    /// <summary>
    /// The first half of Begin Step: what is under the character, and the jump.
    /// </summary>
    /// <remarks>
    /// Split from the second half at the line the reference splits at.
    /// <c>Character.events/Begin Step.xml</c> runs the attack (line 83), then
    /// the jump (93-116), then the <b>right-click</b> (120-126), and only then
    /// the <c>switch(moveStatus)</c> that picks this tick's control and friction
    /// factors (143). A demoman's right-click detonates their mines, and a mine
    /// that goes off under its own owner sets <c>moveStatus = 1</c> — which that
    /// switch reads <em>on the same tick</em>. That is what a mine jump is, and
    /// running the detonation after the whole of Begin Step would give it last
    /// tick's friction.
    /// </remarks>
    public static void BeginStepInput(CollisionWorld world, ref CharacterState character)
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

        // Dropdown platforms are the other way to be on the ground —
        // Character.events/Begin Step.xml:32-49.
        //
        // ⚠️ The two inner tests are written from the PLATFORM's frame in the
        // reference (`with(DropdownPlatform)`, so `other` is the character).
        // Overlap is symmetric, so "the platform moved up one meets the
        // character" is the same question as "the character moved down one meets
        // the platform", which is what is asked here.
        //
        // ⚠️ A MovingPlatform would land in this scan too — its parent object IS
        // DropdownPlatform, so `place_meeting(…, DropdownPlatform)` matches one —
        // but it never has a SolidRect to be found by. It has no sprite of its
        // own, so its collision box comes from an embedded resource in a custom
        // map, and that pipeline is not ported. MovingPlatforms carries the
        // detail; its motion IS simulated, only its box and its carry are not.
        else if ((character.KeyState & PlayerKeys.Down) == 0)
        {
            foreach (ref readonly var rect in world.RectSpan)
            {
                if (rect.Kind != SolidKind.DropdownPlatform)
                    continue;

                // Only a platform immediately below counts...
                if (!Overlaps(rect, box, character.X, character.Y + 1))
                    continue;

                // ...and never one already overlapped, or a character rising
                // through a platform would be caught on it.
                if (Overlaps(rect, box, character.X, character.Y))
                    continue;

                character.OnGround = true;

                // `resetMoveStatus` defaults to 1 and is a map property. ⚠️ No
                // shipped map sets it — checked across all 22 in maps.json — so
                // it is always true here; a custom map that clears it would get
                // a surfing platform, which is not modelled.
                onNonSurfingGround = true;
                break;
            }
        }

        if (onNonSurfingGround)
            character.MoveStatus = 0;

        if (character.OnGround)
            character.DoubleJumpUsed = false;

        // Begin Step's "Handle input", and ⚠️ it sits OUTSIDE the
        // `if(!taunting and !omnomnomnom)` block that opens below — so a
        // character presses jump during a taunt, queues it, and launches on the
        // tick the taunt ends. Transcribed in that order rather than folded into
        // the jump test.
        //
        // ⚠️ `else if` in the reference, not two independent tests. A tick that
        // carries both a press and a release of the jump key — which
        // ApplyInput's `|=` accumulation makes reachable whenever more than one
        // input arrives between two ticks — queues the jump rather than
        // cancelling it.
        if (character.QueueJump)
        {
            if ((character.PressedKeys & PlayerKeys.Jump) != 0)
                character.WantToJump = true;
            else if ((character.ReleasedKeys & PlayerKeys.Jump) != 0)
                character.WantToJump = false;
        }

        // ⚠️ Begin Step.xml:95's `if(!taunting and !omnomnomnom)` opens here and
        // closes after the jump, and GML evaluates it ONCE. So a character
        // already taunting cannot start another taunt and cannot jump — but one
        // that starts a taunt on this tick still jumps, because the block was
        // entered before the flag was set. That is why `taunting` is read into a
        // local rather than re-read below.
        var blocked = character.Taunting || character.Omnomnomnom;

        // Begin Step.xml:102-107. The attack above this (Weapons.FireStep) is
        // the other statement in the block.
        if (!blocked && !character.Humiliated && (character.PressedKeys & PlayerKeys.Taunt) != 0)
        {
            // ⚠️ `if (!invisible && cloakAlpha == 1) taunting = true;` — a spy
            // part-way through a cloak presses taunt and does not taunt.
            // `invisible` is a *drawing* flag the reference sets from the same
            // cloak state, so this port asks the cloak directly.
            if (!character.Cloak && Gml.Equal(character.CloakAlpha, 1))
                character.Taunting = true;

            // ⚠️ Outside that test, so the counter is rewound whether or not the
            // taunt starts. Transcribed rather than tidied.
            character.TauntIndex = 0;
        }

        // Jump fires on a fresh press, not on the key being held — so holding
        // jump does not bunny-hop, unless the client asked for queued jumping,
        // in which case the queue set above is the second way in and holding it
        // does exactly that.
        //
        // The vspeed guard stops a jump re-launching a character already rising
        // faster than a jump would.
        // The character's own jumpStrength, not the class constant: a scoped
        // sniper's is two units lower. It is the same value for everybody else.
        if (!blocked
            && ((character.PressedKeys & PlayerKeys.Jump) != 0
                || (character.QueueJump && character.WantToJump))
            && Gml.Greater(character.VSpeed, -character.JumpStrength))
        {
            if (character.OnGround && !stuckInWall)
            {
                // ⚠️ Outside the `if(not stabbing)` below it in the reference,
                // so a spy who cannot actually jump still loses the slow decay.
                // Character.events/Begin Step.xml:98.
                character.DeathmatchInvulnDecay = CharacterState.MovingInvulnDecay;

                // ⚠️ Character.events/Begin Step.xml:99, `if(not stabbing)`.
                // A spy in the middle of a knife animation has jumpStrength 0
                // anyway, so this looks redundant — it is not, because the
                // guard above compares against that same zeroed jumpStrength
                // and passes. Without it a stabbing spy would have its vspeed
                // *set to* -0, which cancels a fall.
                if (!character.Stabbing)
                {
                    character.WantToJump = false;
                    character.VSpeed = -character.JumpStrength;
                    character.OnGround = false;
                }
            }
            else if (stats.CanDoubleJump && !character.DoubleJumpUsed)
            {
                character.DeathmatchInvulnDecay = CharacterState.MovingInvulnDecay;
                character.WantToJump = false;
                character.VSpeed = -character.JumpStrength;
                character.OnGround = false;
                character.DoubleJumpUsed = true;
                character.MoveStatus = 0;
            }
        }

    }

    /// <summary>
    /// The second half of Begin Step: the friction switch, the acceleration
    /// loop and the gravity accumulation.
    /// </summary>
    /// <remarks>
    /// Reads <c>moveStatus</c>, so anything that can set it — a blast — has to
    /// have happened already. See <see cref="BeginStepInput"/>.
    /// <para>
    /// <c>stuckInWall</c> and the key union are recomputed here rather than
    /// carried across the split. Both are pure functions of the character, and
    /// nothing that runs in between moves it: a blast changes velocity through
    /// <c>motion_add</c> and sets <c>moveStatus</c>, and touches neither the
    /// position nor the keys. So this is the same answer at the cost of one
    /// extra <c>place_free</c> per character per tick.
    /// </para>
    /// </remarks>
    public static void BeginStepAccelerate(CollisionWorld world, ref CharacterState character)
    {
        var stats = character.Stats;
        var downOrPressed = character.KeyState | character.PressedKeys;
        var stuckInWall = !world.PlaceFree(character.Box, character.X, character.Y, character.Filter);

        var (controlFactor, frictionFactor) = ControlAndFriction(character);
        var rocketJumping = character.MoveStatus is 1 or 2 or 4;

        character.Controlling = false;

        // The frameskip loop. Running acceleration and friction twice with a
        // half-strength factor is NOT the same number as running them once, and
        // that is the entire reason the loop exists
        // (Documentation/RateController.md). Do not "simplify" it away.
        for (var skip = 0; skip < 2; skip++)
        {
            // ⚠️ `if(!taunting && !omnomnomnom)` wraps the acceleration and the
            // both-keys cancel — and NOT the friction below it. So an eating
            // heavy still decelerates to a stop rather than sliding on at the
            // speed they started with. Begin Step.xml:237-253.
            //
            // ⚠️ Unlike the jump, this re-reads `taunting` — it is a different
            // block, several lines further down the event — so the tick a taunt
            // starts is already a tick with no acceleration in it.
            if (!character.Taunting && !character.Omnomnomnom)
            {
                // ⚠️ The character's runPower against the *class's*
                // basemaxspeed. The reference never recomputes basemaxspeed when
                // runPower changes (Create.xml:159 derives it from baseRunPower
                // once), so a scoped sniper accelerates slowly and is still
                // capped at the unscoped maximum. See CharacterState.RunPower.
                if ((downOrPressed & PlayerKeys.Left) != 0 && Gml.GreaterOrEqual(character.HSpeed, -stats.BaseMaxSpeed))
                {
                    character.HSpeed -= character.RunPower * controlFactor * 0.5;
                    character.Controlling = true;
                }

                if ((downOrPressed & PlayerKeys.Right) != 0 && Gml.LessOrEqual(character.HSpeed, stats.BaseMaxSpeed))
                {
                    character.HSpeed += character.RunPower * controlFactor * 0.5;
                    character.Controlling = true;
                }

                // Both directions cancel: pressing left and right is not "in
                // control", it is standing still.
                if ((downOrPressed & (PlayerKeys.Left | PlayerKeys.Right)) == (PlayerKeys.Left | PlayerKeys.Right))
                    character.Controlling = false;
            }

            // ⚠️ Outside the eating guard, deliberately — and note the branch
            // below still reads the direction keys, so an eating heavy holding
            // one takes the base-friction arm exactly as a walking one would.
            //
            // Way too fast, or being pushed while under the cap: use the base
            // friction rather than the moveStatus's. This is what stops a rocket
            // jump's low friction from making a running player unstoppable.
            var overspeed = Gml.Greater(Math.Abs(character.HSpeed), stats.BaseMaxSpeed * 2);
            var pushingUnderCap = (downOrPressed & (PlayerKeys.Left | PlayerKeys.Right)) != 0
                                  && Gml.Less(Math.Abs(character.HSpeed), stats.BaseMaxSpeed);

            character.HSpeed /= ClassStats.DeltaMultSkip(
                overspeed || pushingUnderCap ? ClassStats.BaseFriction : frictionFactor);
        }

        // Character.events/Begin Step.xml:269, immediately below the friction
        // block and above the key reset: walking spends spawn protection five
        // times as fast as standing still does. Note it reads `controlling` as
        // the *loop* left it, which is the second frameskip pass's answer.
        if (character.Controlling)
            character.DeathmatchInvulnDecay = CharacterState.MovingInvulnDecay;

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

        // Character.events/Step.xml:25, and the reference's own comment on it:
        // "Pretty sure this is for the healing ramp (crit heals). Done here
        // instead of begin step to avoid code execution order mistakes."
        //
        // Nothing read it until the Medigun landed — damage resets it and only
        // Healing.HealingFactor and the medic's own regeneration consume it — so
        // this counter sat at zero for four phases and cost the beam its ramp.
        // A plain min, not a fuzzy one, as written.
        character.TimeUnscathed = Math.Min(
            character.TimeUnscathed + 1, CharacterState.MaxTimeUnscathed);

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

        // Dropdown platforms, Character.events/Step.xml:90-107. ⚠️ This is what
        // actually holds a character up: the platform is never solid, so nothing
        // in the movement solver stops it, and without this branch the Begin
        // Step ground test above would say "on the ground" while the character
        // fell straight through.
        if ((character.KeyState & PlayerKeys.Down) == 0)
        {
            foreach (ref readonly var rect in world.RectSpan)
            {
                if (rect.Kind != SolidKind.DropdownPlatform)
                    continue;

                // ⚠️ Only a character moving DOWN is caught. Rising through one
                // is the whole point of a drop-through platform.
                if (Gml.Less(character.Y, character.YPrevious))
                    continue;

                if (!Overlaps(rect, box, character.X, character.Y))
                    continue;

                // ⚠️ Already inside it before this tick's move: leave it alone.
                // This is what lets a character fall THROUGH a platform it was
                // already inside, and getting its sign wrong re-seats them on
                // top of one they had dropped through — measured, see below.
                //
                // The reference is `place_meeting(x, y + (other.y -
                // other.y_previous), other)` inside `with(DropdownPlatform)`, so
                // `x`/`y` are the PLATFORM's and `other` is the character: the
                // platform moves DOWN by the character's dy. Relative to the
                // character that is the same as testing it at its previous y.
                //
                // ⚠️ It is NOT the same as moving the character down by dy,
                // which is what stood here and is the opposite direction — that
                // asks whether it will be inside the platform NEXT tick.
                //
                // ⚠️ And the shift is applied to the platform, not the
                // character, because GM8 rounds the *calling instance's*
                // position to build the box it tests. `place_meeting` on the
                // platform rounds `platformY + dy`; rounding the character's
                // `y - dy` instead is a different number with an unrelated
                // fractional part. A platform's own y is an integer, so
                // `round(P + dy)` is `P + round(dy)` except where `dy` lands
                // exactly on a half and the platform's parity decides it —
                // a tie this project does not chase (docs/SIMULATION.md).
                var shift = CollisionWorld.RoundPosition(character.Y - character.YPrevious);

                if (Overlaps(rect with { Top = rect.Top + shift, Bottom = rect.Bottom + shift },
                        box,
                        character.X,
                        character.Y))
                    continue;

                // Lift out in tenths of a pixel, exactly as the reference does —
                // the loop is observable, because it stops on the first free
                // tenth rather than snapping to the platform's top edge.
                while (Overlaps(rect, box, character.X, character.Y)
                       && world.PlaceFree(box, character.X, character.Y - 0.1, filter))
                {
                    character.Y -= 0.1;
                }

                character.VSpeed = 0;
            }
        }

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
    /// The tail of Begin Step: the nuts-and-bolts regeneration
    /// (<c>Character.events/Begin Step.xml:387</c>) and what being ubered does
    /// to you besides making you invulnerable (<c>:389-394</c>).
    /// </summary>
    /// <remarks>
    /// <b>Still not the whole tail.</b> The animation counter beside these
    /// belongs to a phase that has not landed. The intel drop moved out to
    /// <see cref="Intel.BeginStep"/> with the objective, and the arena arm of
    /// the ammo refill waits on <c>ArenaHUD</c>.
    /// <para>
    /// The regeneration is unconditional and classless:
    /// <c>nutsNBolts = min(nutsNBolts + 0.1, maxNutsNBolts)</c> runs for a
    /// scout too, invisibly — only the engineer's wire block and the build
    /// command ever read the value. Ten ticks per unit, so a fresh build's
    /// hundred takes 1,000 ticks (33 seconds) to come back.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>event_perform(ev_alarm, 5)</c> is inert here and is deliberately
    /// not reproduced.</b> The reference runs the weapon's reload alarm and then
    /// cancels it, but the line above has already filled the magazine — so every
    /// reload body in the game (<c>if ammoCount &lt; maxAmmo</c>) does nothing,
    /// and all that survives is the cancellation.
    /// </para>
    /// </remarks>
    public static void BeginStepUberEffects(ref CharacterState character, ref WeaponState weapon)
    {
        character.NutsNBolts = Math.Min(character.NutsNBolts + 0.1, CharacterState.MaxNutsNBolts);

        if (character.Ubered)
        {
            // An uber puts a fire out, and forgets who lit it — so afterburn that
            // would otherwise have finished the victim credits nobody afterwards.
            if (Gml.Greater(character.BurnIntensity, 0) || Gml.Greater(character.BurnDuration, 0))
            {
                character.BurnIntensity = 0;
                character.BurnDuration = 0;
                character.BurnedBy = CharacterState.Nobody;
            }

            RefillMagazine(character.Class, ref weapon);
        }

        // Character.events/Begin Step.xml:404, a separate block a few lines
        // below with the same body and a different reason: everybody has full
        // ammo while the win is on screen, so the ten seconds are a free-for-all
        // rather than a scramble for a reload.
        //
        // ⚠️ `(arenaRoundEnd or global.mapchanging) and !player.humiliated`, so
        // the losing team of an Arena round does NOT get the free reload — the
        // ten seconds are a free-for-all for the winners only.
        if ((character.MapChanging || character.ArenaRoundEnded) && !character.Humiliated)
            RefillMagazine(character.Class, ref weapon);
    }

    /// <summary>
    /// The body both refills share: a full magazine and no reload pending.
    /// </summary>
    /// <remarks>
    /// `if(variable_local_exists("maxAmmo"))` — true for every weapon in the
    /// game; the guard is there for a weapon that has not defined one yet.
    /// </remarks>
    private static void RefillMagazine(PlayerClass playerClass, ref WeaponState weapon)
    {
        weapon.AmmoCount = WeaponStats.For(playerClass).MaxAmmo;
        weapon.ReloadTimer = WeaponState.Inactive;
    }

    /// <summary>
    /// Stair descent, the facing, and the clamp into the map.
    /// </summary>
    /// <remarks>
    /// The weaponless overload, for the tests and callers that have no weapon to
    /// move. A real character always has one — <c>Character.events/Create.xml</c>
    /// makes it — so the live path is the three-argument form.
    /// </remarks>
    public static bool EndStep(CollisionWorld world, ref CharacterState character)
    {
        var discarded = default(WeaponState);
        return EndStep(world, ref character, ref discarded);
    }

    /// <summary>
    /// Stair descent, the death check, the facing, the weapon that trails the
    /// character, and the clamp into the map.
    /// </summary>
    /// <returns>
    /// Whether this character just died, in which case the rest of End Step did
    /// not run and the caller owes it a <see cref="Deaths.Kill"/>.
    /// </returns>
    /// <remarks>
    /// <b>The death check is reported rather than acted on.</b> It needs the
    /// killer's character to resolve the medic assist and it has to remove a
    /// slot from the world, and this function is handed one character and a
    /// collision world — see <see cref="Deaths"/>.
    /// </remarks>
    public static bool EndStep(CollisionWorld world, ref CharacterState character, ref WeaponState weapon)
    {
        var box = character.Box;
        var filter = character.Filter;

        // Climbing down stairs. Only when not falling — otherwise every fall
        // would snap to the next ledge down.
        //
        // ⚠️ And not while standing on a dropdown platform (End Step.xml:21),
        // or the snap would drop a character straight through the thing holding
        // it up. Holding KEY_DOWN re-enables it, which is how you step down off
        // one deliberately.
        if (Gml.Equal(character.VSpeed, 0) && !StandingOnDropdown(world, in character, box))
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

        // End Step.xml:29-30, and the position matters: after the stair descent,
        // so it records where the character actually ended up, and BEFORE the
        // death exit below, so a character that dies this tick still gets one.
        character.YPrevious = character.Y;

        // End Step.xml:34-47. Note where it sits: after the stair descent and
        // *before* the hp clamp below, and it `exit`s — so a character that dies
        // this tick never updates its facing, never drags its weapon along and
        // is never clamped into the map. The last thing it did was walk down a
        // step.
        if (Deaths.IsDead(character))
            return true;

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

        // End Step.xml:69-72, immediately below the clamps: the cloak fading in
        // or out, twenty ticks each way.
        //
        // ⚠️ An `else if`, so a cloaked character whose flicker is up does
        // *neither* — the alpha is pinned where dealFlicker left it until
        // alarm[7] clears the flicker. And note the fade is not what decides
        // whether the cloak works: `cloak` alone gates the intel grab and the
        // knife, and `cloakAlpha == 1` gates only the capture point and the
        // start of a new cloak.
        if (character.Cloak && Gml.Greater(character.CloakAlpha, 0) && !character.CloakFlicker)
            character.CloakAlpha = Math.Max(character.CloakAlpha - CharacterState.CloakFadeRate, 0);
        else if (!character.Cloak && Gml.Less(character.CloakAlpha, 1))
            character.CloakAlpha = Math.Min(character.CloakAlpha + CharacterState.CloakFadeRate, 1);

        // End Step.xml:83-96, the sandvich, between the cloak fade and the
        // cabinet flag. ⚠️ Its position is what makes the facing freeze work:
        // the line above derived Facing from the aim, and this overwrites it.
        if (character.Omnomnomnom)
        {
            character.OmnomnomnomIndex += CharacterState.OmnomnomnomRate;
            character.Facing = character.EatFacing;

            // ⚠️ Re-armed EVERY tick of the eat while hurt, not once at the
            // start. The reference's own comment: "This should prevent the 'ate
            // and got hit but didn't refresh cooldown' bug ... cooldown is now
            // reset continually until fully healed or finished eating." So a
            // heavy hit on the last tick pays the whole 45 seconds from there,
            // and one who reaches full health early keeps a cooldown that
            // stopped being re-armed.
            if (Gml.Less(character.Hp, character.Stats.MaxHp))
            {
                character.CanEat = false;
                character.EatTimer = Timers.SetAlarm(CharacterState.EatCooldown);
            }

            // ⚠️ `<=`, so the tick a heavy reaches exactly full health still
            // gets 1.6 more and ends the tick 1.6 ABOVE maxHp. The clamp is
            // earlier in this same event, so that overshoot survives until the
            // next tick's End Step — and the wire carries it. Transcribed, not
            // tidied: `Gml.LessOrEqual` then add is what the reference does.
            if (Gml.LessOrEqual(character.Hp, character.Stats.MaxHp))
                character.Hp += CharacterState.OmnomnomnomHeal;

            if (Gml.GreaterOrEqual(character.OmnomnomnomIndex, CharacterState.OmnomnomnomEnd))
                character.Omnomnomnom = false;
        }

        // End Step.xml:90-94, the taunt's own clock and the only thing that ever
        // ends one.
        //
        // ⚠️ `tauntspeed*0.1`, not 0.3 — GM8 holds 3*0.1 and 0.3 as different
        // doubles, and this is a repeated addition compared against a small
        // integer, so the two do not always end on the same tick. The class's
        // frame count is the whole duration: 9 frames for a scout is 30 ticks,
        // 17 for Quote is 57.
        if (character.Taunting)
        {
            character.TauntIndex += CharacterState.TauntRate;

            if (Gml.GreaterOrEqual(character.TauntIndex, character.Stats.TauntFrames))
                character.Taunting = false;
        }

        // End Step.xml:104, between the taunt block and the death-cam
        // position: "for things polling whether the character is on a
        // medcabinet". The cabinet's own effects are a *collision* event and
        // have already happened this tick; this is only the flag a sentry build
        // reads next tick. See CharacterState.OnCabinet.
        character.OnCabinet = world.MeetsCabinet(box, character.X, character.Y);

        // End Step.xml:128, after the clamps: the flame count an airblast
        // extinguish will read next Begin Step. Below the death exit, so a dead
        // character's last value freezes — like everything else here.
        character.RealNumFlames =
            CharacterState.NumFlames * character.BurnDuration / CharacterState.MaxBurnDuration;

        // Character.events/End Step.xml:131-135, the line below realnumflames.
        //
        // ⚠️ Two separate `if`s, not an else — so the tick a countdown crosses
        // zero it is both decremented and clamped, and the clamp is the only
        // thing that ever writes an exact 0.
        //
        // ⚠️ **And both comparisons are fuzzy, which is load-bearing here.** A
        // default five seconds is 30 spent at 0.2 a tick, and 150 subtractions
        // of 0.2 from 30 leave **7.3e-14** rather than zero — inside GML's 1e-13
        // comparison fuzz, so the reference clamps and a raw `<= 0` does not.
        // Written raw, the countdown rests on a positive residue for ever and
        // the character is permanently invulnerable. Measured by writing it raw:
        // TeamDeathmatchTests.ProtectionLastsFiveTimesItsTickCountWhileStandingStill.
        //
        // Same family as the ubercharge's 241st tick, and the opposite outcome:
        // there the residue was 7.02e-12, seventy times the fuzz, so the
        // reference did *not* treat it as zero.
        if (Gml.Greater(character.DeathmatchInvulnerable, 0))
            character.DeathmatchInvulnerable -= character.DeathmatchInvulnDecay;

        if (Gml.LessOrEqual(character.DeathmatchInvulnerable, 0))
            character.DeathmatchInvulnerable = 0;

        return false;
    }

    /// <summary>
    /// <c>Character.events/Begin Step.xml:129-142</c>: the spy's cloak going on
    /// or off.
    /// </summary>
    /// <remarks>
    /// It sits between the right-click block and the <c>moveStatus</c> switch,
    /// which is why it is called between <see cref="Weapons.SpecialStep"/> and
    /// <see cref="BeginStepAccelerate"/> — and it is on the <b>same key</b> as
    /// the right click, so both run on one press.
    /// <list type="bullet">
    /// <item>⚠️ <b>The cloak is on <c>pressedKeys</c>, not the down-or-pressed
    /// union</b> — an edge, so holding the key does not flicker in and out.</item>
    /// <item>⚠️ <b><c>(cloakAlpha == 1 and !cloak) or cloak</c></b>: you can
    /// only <em>start</em> a cloak from fully visible, and you can always drop
    /// one. A spy who uncloaks and immediately presses again has to wait the
    /// twenty ticks of fade back in.</item>
    /// <item>⚠️ <b><c>readyToStab</c> gates the toggle</b>, so the fifty ticks
    /// after a stab are fifty ticks a spy cannot vanish <em>or</em> reappear.
    /// That flag belongs to the Revolver, which is why the weapon is passed in.
    /// </item>
    /// <item>⚠️ <b>Uncloaking costs the intel grab</b>: <c>canGrabIntel =
    /// false</c> and <c>alarm[1] = max(alarm[1], 25)</c> — <em>max</em>, so a
    /// longer cooldown already running is not shortened. The reference's own
    /// comment: "stop spies immediately picking up intel after uncloaking".</item>
    /// <item>An intel carrier cannot cloak at all, and neither can somebody
    /// taunting or humiliated — both real since 2026-08-28. The taunt clause
    /// bites on the tick the taunt starts, because this block is below the one
    /// that sets the flag.</item>
    /// </list>
    /// </remarks>
    public static void CloakStep(ref CharacterState character, ref WeaponState weapon)
    {
        if ((character.PressedKeys & PlayerKeys.Special) == 0)
            return;

        if (!character.Stats.CanCloak
            || character.CarryingIntel
            || character.Taunting
            || character.Humiliated
            || !(character.Cloak || Gml.Equal(character.CloakAlpha, 1)))
        {
            return;
        }

        // Above the readyToStab test, so the key gives up spawn protection even
        // when the toggle itself is refused — the same shape the attack and the
        // right click have.
        character.DeathmatchInvulnerable = 0;

        if (!weapon.ReadyToStab)
            return;

        if (character.Cloak)
        {
            character.CanGrabIntel = false;
            character.IntelGrabTimer = Math.Max(character.IntelGrabTimer, UncloakGrabDelay);
        }

        character.Cloak = !character.Cloak;
    }

    /// <summary>
    /// <c>Objects/Characters/Heavy.events/Step.xml</c>: a heavy holding the
    /// trigger walks at 0.3 instead of 0.8.
    /// </summary>
    /// <remarks>
    /// <b>The only class-specific Step event in the game</b>, and the only thing
    /// besides the sniper's scope and the spy's stab that moves
    /// <see cref="CharacterState.RunPower"/>. (The spy's End Step is the only
    /// other per-class event of any kind.)
    /// <para>
    /// ⚠️ <b>It reads <c>keyState</c> alone, not the down-or-pressed
    /// union</b>, so a click that arrives and releases inside one tick does not
    /// slow the heavy — unlike the shot it fires, which that same click does
    /// produce.
    /// </para>
    /// <para>
    /// ⚠️ <b>It runs after the inherited Step</b>, so the rate it sets is the
    /// one <em>next</em> tick's Begin Step accelerates with. The event's
    /// <c>action_inherited</c> comes first and the code action second.
    /// </para>
    /// </remarks>
    public static void HeavyStep(ref CharacterState character)
    {
        if (character.Class != PlayerClass.Heavy)
            return;

        character.RunPower = (character.KeyState & PlayerKeys.Attack) != 0
            ? HeavyFiringRunPower
            : ClassStats.For(PlayerClass.Heavy).RunPower;
    }

    /// <summary>What a heavy's acceleration drops to while the trigger is down.</summary>
    private const double HeavyFiringRunPower = 0.3;

    /// <summary>
    /// The intel-grab cooldown an uncloak imposes: <c>alarm[1] = max(alarm[1],
    /// 25)</c>.
    /// </summary>
    private const int UncloakGrabDelay = 25;

    /// <summary>
    /// Which control and friction factors this character's <c>moveStatus</c>
    /// selects.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Public because the bot layer's takeoff gate has to read it live.</b>
    /// Begin Step sets these from <c>moveStatus</c> and <b>not</b> from
    /// <c>onground</c>, so the same control factor applies in the air — which is
    /// the whole reason a slow takeoff can be corrected by an in-flight tracker
    /// at all. <c>botJumpReach.gml</c> replays this law to answer "would this arc
    /// reach", and a second copy of the switch would be a second copy of a
    /// measured fact.
    /// </remarks>
    public static (double Control, double Friction) ControlAndFriction(in CharacterState character) =>
        character.MoveStatus switch
        {
            // The two odd-looking literals are GM8's own doubles for 0.65 and
            // 0.35, which it parses one ULP above the IEEE nearest — see Gml.
            1 => (Gml.PointSixFive, 1.0),   // rocketing myself
            2 => (0.45, 1.05),              // rocketed by an enemy
            3 => (Gml.PointThreeFive, 1.05), // airblast
            4 => (ClassStats.BaseControl, 1.0), // rocketed by a team-mate

            // ⚠️ Humiliation costs 0.2 and intel 0.1, and they do not add: the
            // reference is an if/else-if chain, so a humiliated flag carrier
            // loses 0.2 and not 0.3. Begin Step.xml:176-181.
            _ => (character.Humiliated ? ClassStats.BaseControl - 0.2
                    : character.CarryingIntel ? ClassStats.BaseControl - 0.1
                    : ClassStats.BaseControl,
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

    /// <summary>
    /// <c>place_meeting(x, y, DropdownPlatform)</c> against one platform.
    /// </summary>
    /// <remarks>
    /// Position rounding is the collision world's — a fractional position
    /// becomes a bounding box the same way for a platform as for anything else.
    /// </remarks>
    private static bool Overlaps(in SolidRect rect, in CharacterBox box, double x, double y)
    {
        var originX = CollisionWorld.RoundPosition(x);
        var originY = CollisionWorld.RoundPosition(y);

        return rect.Overlaps(
            originX + box.Left,
            originX + box.Right,
            originY + box.Top,
            originY + box.Bottom);
    }

    /// <summary>
    /// The stair descent's dropdown guard, <c>End Step.xml:21</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ The reference spells it as "may descend", which is
    /// <c>KEY_DOWN or not on one or already inside one</c>. This is its
    /// negation, and the third clause matters: a character whose box already
    /// overlaps a platform is not standing <em>on</em> it and may step down.
    /// </remarks>
    private static bool StandingOnDropdown(CollisionWorld world, in CharacterState character, in CharacterBox box)
    {
        if ((character.KeyState & PlayerKeys.Down) != 0)
            return false;

        foreach (ref readonly var rect in world.RectSpan)
        {
            if (rect.Kind != SolidKind.DropdownPlatform)
                continue;

            if (Overlaps(rect, box, character.X, character.Y + 1)
                && !Overlaps(rect, box, character.X, character.Y))
            {
                return true;
            }
        }

        return false;
    }
}

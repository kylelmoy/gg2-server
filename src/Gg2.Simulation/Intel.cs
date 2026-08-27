using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// One team's intelligence: the briefcase a player of the <em>other</em> team
/// carries away.
/// </summary>
/// <remarks>
/// <c>Objects/Map elements/Gamemode-specific/CTF/Intelligence*.xml</c>. The
/// object itself is almost empty — a Create event that sets <c>team</c>, an
/// <c>Alarm 0</c> that returns it and a <c>User Event 10</c> that serialises it;
/// everything else is in the four <c>doEvent*Intel</c> scripts and in the
/// character's four collision events.
/// <para>
/// ⚠️ <b>A carried intel has no instance at all.</b> <c>doEventGrabIntel</c>
/// ends with <c>instance_destroy()</c> and <c>doEventDropIntel</c> creates a
/// fresh one where the carrier stood, so "who has it" is not a field on the
/// intel — it is <see cref="CharacterState.CarryingIntel"/> on the carrier, and
/// <see cref="OnField"/> here is the reference's <c>instance_exists</c>. A
/// <c>serialize(IntelligenceRed)</c> of a carried flag writes a count of zero,
/// which is why <c>StateUpdate.RedIntel</c> is an array and not a nullable.
/// </para>
/// <para>
/// ⚠️ <b>A default <see cref="IntelState"/> is not a valid one</b>, the same
/// trap as <see cref="CharacterState.Fresh"/> and <see cref="PlayerState.Fresh"/>:
/// <see cref="RecoverTimer"/> rests at <see cref="Timers.Inactive"/> and a
/// zeroed struct says 0, which is a live alarm one tick from firing everywhere
/// else in this codebase. Nothing reads it while <see cref="OnField"/> is false,
/// and <see cref="Intel.Place"/> is the only thing that sets that.
/// </para>
/// </remarks>
public struct IntelState
{
    /// <summary>
    /// Whether the map places an <c>IntelligenceBase</c> of this team at all.
    /// </summary>
    /// <remarks>
    /// <c>doEventReturnIntel</c> opens with
    /// <c>if(!instance_exists(intelBaseObj)) exit;</c>, so on a map with no
    /// intel every path here is a no-op rather than an error.
    /// </remarks>
    public bool HasBase;

    /// <summary>
    /// <c>instance_exists(IntelligenceRed)</c>: the briefcase is lying on the
    /// map rather than being carried.
    /// </summary>
    public bool OnField;

    /// <summary>Where it is lying, where <see cref="OnField"/>. Meaningless otherwise.</summary>
    public double X;

    /// <inheritdoc cref="X"/>
    public double Y;

    /// <summary>The <c>IntelligenceBase</c>'s position — where a return puts it.</summary>
    /// <remarks>
    /// Map data copied into the state rather than looked up, so that
    /// <see cref="MatchSimulation.Tick"/> needs no extra read-only argument for
    /// the objective and a forked rollout carries the whole answer with it. Two
    /// doubles per team; the map itself stays out of <see cref="WorldState"/>,
    /// which is docs/BOTAI.md item 1's actual requirement.
    /// </remarks>
    public double HomeX;

    /// <inheritdoc cref="HomeX"/>
    public double HomeY;

    /// <summary>
    /// <c>alarm[0]</c>: ticks until a dropped briefcase returns itself. Rests at
    /// <see cref="Timers.Inactive"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It is set from the carrier's <c>intelRecharge</c>, and that can be
    /// zero.</b> <c>doEventDropIntel</c> writes
    /// <c>newIntelInstance.alarm[0] = intelRecharge</c>, and a flag grabbed
    /// straight off its base starts the carrier at <c>max(0, -1) = 0</c> — so a
    /// briefcase dropped in the first tick of a run gets an alarm of 0, which in
    /// GM8 never fires (<see cref="Timers.Advance(ref int)"/>). It sits there
    /// until somebody touches it, and because the grab guard is
    /// <c>alarm[0] &lt;= 1</c> that somebody can be the carrier who just dropped
    /// it. Both are the reference's behaviour.
    /// </remarks>
    public int RecoverTimer;

    /// <summary>
    /// Whether this flag can be picked up at all.
    /// </summary>
    /// <remarks>
    /// <c>Character.events/Collision with IntelligenceRed.xml</c> guards on
    /// <c>!instance_exists(InvasionHUD)</c> and the blue one does not — in
    /// Invasion the attackers push a single flag one way and the red briefcase
    /// is scenery. Resolved once, in <see cref="Intel.Place"/>, because the
    /// gamemode cannot change while a map is loaded.
    /// </remarks>
    public bool Grabbable;
}

/// <summary>
/// Capture the flag: grabbing, dropping, returning and scoring the intel.
/// </summary>
/// <remarks>
/// The four <c>Scripts/Events/doEvent*Intel.gml</c> scripts and the character
/// collision events that reach them. docs/SIMULATION.md, "Settled: the intel",
/// has the full rules; the traps are on the members below.
/// </remarks>
public static class Intel
{
    /// <summary>
    /// The briefcase's collision box, as offsets from its origin.
    /// </summary>
    /// <remarks>
    /// <c>IntelligenceRedS</c>, <c>IntelligenceBlueS</c> and
    /// <c>IntelligenceBaseS</c> are all 20x22 with their origin at (9, 12) and a
    /// <c>RECTANGLE</c> mask on <c>AUTO</c> bounds, and all three images have
    /// the same opaque extent — columns 0..19, rows 0..19, the bottom two rows
    /// being fully transparent. So one box serves the flags and the bases alike,
    /// and every map places them at <c>xscale = yscale = 1</c>.
    /// <para>
    /// ⚠️ <b>Derived from the PNGs and the sprite XML, not measured against the
    /// running game</b> — unlike every other box in this project. CLAUDE.md's
    /// warning about <c>SentryRed.xml</c> is that a <em>manual</em> mask bound is
    /// clamped to the image; these are <c>AUTO</c>, which is the case the XML
    /// really does describe, but it is still an inference.
    /// </para>
    /// <para>
    /// ⚠️ <b>And the fixtures corroborate almost none of it.</b> Measured by
    /// mutation against <c>closedsupport</c>'s recorded grab, the one message a
    /// replay reproduces on its exact tick: widening <see cref="BoxLeft"/> to
    /// -11 moves the tick, -10 does not, and <see cref="BoxRight"/>,
    /// <see cref="BoxTop"/> and <see cref="BoxBottom"/> can each be moved by
    /// <b>sixteen pixels</b> without any fixture noticing. A bot walks in from
    /// one side and grabs; the other three edges are never the ones it crosses.
    /// So the only gate on three quarters of this box is
    /// <c>IntelTests.TheGrabHappensExactlyWhereTheBoxesTouch</c>, which asserts
    /// the same inference. Measuring it live is on the handoff.
    /// </para>
    /// </remarks>
    public const int BoxLeft = -9;

    /// <inheritdoc cref="BoxLeft"/>
    public const int BoxTop = -12;

    /// <inheritdoc cref="BoxLeft"/>
    public const int BoxRight = 10;

    /// <inheritdoc cref="BoxLeft"/>
    public const int BoxBottom = 7;

    /// <summary>
    /// <c>INTEL_MAX_TIMER</c>: the longest a dropped briefcase can take to
    /// return itself, and the cap on a carrier's <c>intelRecharge</c>.
    /// </summary>
    public const double MaxTimer = 900;

    /// <summary>
    /// How long a player who dropped the flag must wait before they can take it
    /// back — <c>alarm[1] = 300</c> in <c>doEventDropIntel</c>.
    /// </summary>
    public const int GrabCooldown = 300;

    /// <summary>One team's briefcase, by team.</summary>
    /// <remarks>
    /// Returns red's for a team that owns neither — <see cref="Team.Spectator"/>
    /// and <see cref="Team.Any"/> — so every caller guards on the team first. A
    /// <c>ref</c> return so callers mutate the state in place; it lives here
    /// rather than on <see cref="WorldState"/> because a struct may not return
    /// its own field by reference.
    /// </remarks>
    public static ref IntelState Of(ref WorldState state, Team team) =>
        ref (team == Team.Blue ? ref state.BlueIntel : ref state.RedIntel);

    /// <summary>
    /// One briefcase as <c>Intelligence.events/User Event 10.xml</c> writes it,
    /// or null when it is being carried and there is no instance to serialise.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>round(x*5)</c>, not the character's plain <c>x*5</c>.</b> GML's
    /// own <c>round</c> is half to <em>even</em> and runs first, after which the
    /// extension's half-away-from-zero rule has nothing left to decide — two
    /// objects, two quantisations, one wire. CLAUDE.md's rounding table has all
    /// six cases.
    /// <para>
    /// Here rather than in <c>Gg2.Server</c> for the reason
    /// <see cref="CharacterWire"/> is: the parity harness and the live server
    /// must quantise through the same code, or a recording proves the wrong
    /// program.
    /// </para>
    /// </remarks>
    public static IntelBlock? ToBlock(in IntelState intel) =>
        intel.OnField
            ? new IntelBlock(
                WireQuantise.ToUShort(Gml.Round(intel.X * 5)),
                WireQuantise.ToUShort(Gml.Round(intel.Y * 5)),
                WireQuantise.ToShort(intel.RecoverTimer))
            : null;

    /// <summary>The bounding box of a briefcase or a base at a position.</summary>
    /// <remarks>
    /// The position is rounded half to even first, the same rule
    /// <see cref="Aabb.ForCharacter"/> applies — a dropped flag inherits its
    /// carrier's fractional coordinates, so this is reachable rather than
    /// theoretical. <c>image_xscale</c> is 1 on every instance the game creates
    /// (<c>instance_create</c> leaves it there and no intel script touches it),
    /// so there is no flipped-sprite inflation to apply.
    /// </remarks>
    public static Aabb BoxAt(double x, double y)
    {
        var ix = CollisionWorld.RoundPosition(x);
        var iy = CollisionWorld.RoundPosition(y);

        return new Aabb(ix + BoxLeft, iy + BoxTop, ix + BoxRight, iy + BoxBottom);
    }

    /// <summary>
    /// Puts both briefcases on their bases, from the map's own entity list.
    /// </summary>
    /// <remarks>
    /// <c>IntelligenceBaseRed.events/Create.xml</c> is
    /// <c>instance_create(x, y, IntelligenceRed)</c> — the base makes the flag,
    /// at the base's own position — so a map entity of type <c>redintel</c>
    /// becomes both. A map with no intel leaves <see cref="IntelState.HasBase"/>
    /// false and every path here inert.
    /// <para>
    /// Called by whoever loads the map, next to <c>new CollisionWorld</c>: the
    /// state cannot hold the map, so the copy has to be made once at load
    /// rather than looked up per tick.
    /// </para>
    /// </remarks>
    public static void Place(ref WorldState state, LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        state.RedIntel = default;
        state.BlueIntel = default;

        // In Invasion the red briefcase is scenery — see IntelState.Grabbable.
        var invasion = GameModes.Of(map) == GameMode.Invasion;

        foreach (var entity in map.Entities)
        {
            switch (entity.Type)
            {
                // `??=` in spirit: the reference creates one flag per base, and
                // a map with two red bases would end up with two IntelligenceRed
                // instances that every `IntelligenceRed.x` in the game reads as
                // one. No shipped map has more than one of each; taking the
                // first is what the port can represent.
                case EntityTypes.RedIntel when !state.RedIntel.HasBase:
                    state.RedIntel = AtBase(entity.X, entity.Y, grabbable: !invasion);
                    break;

                case EntityTypes.BlueIntel when !state.BlueIntel.HasBase:
                    state.BlueIntel = AtBase(entity.X, entity.Y, grabbable: true);
                    break;
            }
        }
    }

    private static IntelState AtBase(double x, double y, bool grabbable) => new()
    {
        HasBase = true,
        OnField = true,
        X = x,
        Y = y,
        HomeX = x,
        HomeY = y,
        RecoverTimer = Timers.Inactive,
        Grabbable = grabbable,
    };

    /// <summary>
    /// The carrier's half of <c>Character.events/Begin Step.xml</c>: the return
    /// timer charging, and the flag being dropped by an uber.
    /// </summary>
    /// <remarks>
    /// Two blocks, both inside <c>if (intel)</c> and its neighbours, in the
    /// reference's own order — the charge at line 303-337 and the uber drop at
    /// 366-369, with the animation counter between them.
    /// <para>
    /// ⚠️ <b>The charge rate falls with speed, so standing still returns the
    /// flag sooner.</b> <c>INTEL_MAX_TIMER/((3 + abs(min(hspeed, 7))/3.5)*30)</c>
    /// is a full charge in three seconds at rest and in five at a scout's top
    /// speed. <c>min</c> and not <c>abs</c> first, so running left is capped by
    /// the <c>abs</c> and running right by the <c>min</c> — the same number
    /// either way, but do not "simplify" it to <c>min(abs(hspeed), 7)</c>
    /// without noticing that a negative hspeed passes <c>min</c> unchanged.
    /// </para>
    /// <para>
    /// The <c>LooseSheet</c> particles beside it are not modelled: they are
    /// inside <c>global.run_virtual_ticks</c>, which docs/SIMULATION.md records
    /// as always true, so the reference host really does draw one or two
    /// <c>random(1)</c> from the global stream per carried tick. That is
    /// server-side RNG the port is explicitly free about — every seed it feeds
    /// is broadcast — and no client can tell.
    /// </para>
    /// <para>
    /// <c>global.mapchanging</c> is the drop's other trigger and is always false
    /// here; the round ending is not simulated yet.
    /// </para>
    /// </remarks>
    public static void BeginStep(ref WorldState state, int slot, ref TickEvents events)
    {
        ref var character = ref state.Characters[slot];

        if (!character.CarryingIntel)
            return;

        // `deathmatch_invulnerable = 0` above it is not transcribed: the tdm
        // spawn protection is not modelled, and the reference's own comment
        // says the line is there "in case anyone ever applies this code to
        // another gamemode".
        //
        // Begin Step.xml:328-332, immediately above the recharge: ⚠️ **a spy
        // who is carrying the intel is uncloaked outright**, not faded —
        // `cloak = false; cloakAlpha = 1;`. So there is no window in which a
        // cloaked spy holds a briefcase, and the End Step fade never gets to
        // run on this tick. (The grab itself refuses a cloaked spy, so this is
        // the case where a spy cloaks *while* carrying — which the toggle also
        // refuses — or is handed one some other way. Transcribed because the
        // reference has it and because it is one line.)
        if (character.Cloak)
        {
            character.Cloak = false;
            character.CloakAlpha = 1;
        }

        // `if (!omnomnomnom)` — a heavy eating does not charge. Eating is not
        // modelled, so the guard is always true.
        var rate = MaxTimer / ((3 + (Math.Abs(Math.Min(character.HSpeed, 7)) / 3.5)) * 30);
        character.IntelRecharge = Math.Min(MaxTimer, character.IntelRecharge + rate);

        // Begin Step:366, `if (intel and (ubered or global.mapchanging))`. An
        // ubered carrier drops it — otherwise a medic could walk an
        // invulnerable scout through the whole map — and so does everybody once
        // the round is decided, so the win screen is not also a capture.
        if (character.Ubered || state.MapChanging)
            Drop(ref state, slot, ref events);
    }

    /// <summary>
    /// The alarm-0 stage's intel entry: a dropped briefcase whose timer has run
    /// out goes home.
    /// </summary>
    /// <remarks>
    /// <c>Intelligence.events/Alarm 0.xml</c>. Both flags are created by their
    /// bases at room start, before any character, so within GM8's alarm-index-major
    /// stage they are the <em>first</em> alarm 0 of the tick — which is why this
    /// runs at the top of <see cref="Timers.Advance(ref WorldState, SpawnPoints, ref TickEvents)"/>
    /// rather than beside the projectiles.
    /// <para>
    /// A briefcase recreated mid-match by a capture is a younger instance and
    /// would sort after the characters. It is created with its alarm at rest and
    /// only a drop ever arms one, so the distinction is not observable.
    /// </para>
    /// </remarks>
    public static void ReturnStep(ref WorldState state, ref TickEvents events)
    {
        if (state.RedIntel.OnField && Timers.Advance(ref state.RedIntel.RecoverTimer))
            Return(ref state, Team.Red, ref events);

        if (state.BlueIntel.OnField && Timers.Advance(ref state.BlueIntel.RecoverTimer))
            Return(ref state, Team.Blue, ref events);
    }

    /// <summary>
    /// The collision stage's four character events: capturing at a base, then
    /// grabbing a flag.
    /// </summary>
    /// <remarks>
    /// <b>Four passes, not one per character, because GM8's collision stage is
    /// event-major.</b> A collision event is keyed by the other object's id and
    /// the engine walks them in that order —
    /// <c>IntelligenceBaseRed</c> (104), <c>IntelligenceBaseBlue</c> (105),
    /// <c>IntelligenceRed</c> (133), <c>IntelligenceBlue</c> (134) — so every
    /// character's capture is resolved before any character's grab. It is
    /// observable exactly once: a flag lying on the enemy's own base, captured
    /// by one player and grabbed by another on the same tick.
    /// <para>
    /// Within a pass, slots ascend. The reference's order is instance creation,
    /// which the slot walk only approximates — the same caveat as the sentries'
    /// End Steps, and it costs a difference only when two players reach one
    /// briefcase on the same tick.
    /// </para>
    /// </remarks>
    public static void CollisionStep(CollisionWorld world, ref WorldState state, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        // ⚠️ `if(global.isHost and !global.mapchanging)` guards the two *grab*
        // events and not the two capture ones — so a carrier who somehow still
        // held the flag when the round ended could still score. They cannot:
        // Begin Step drops it on the same tick the flag becomes ungrabbable.
        // Transcribed where the reference puts it rather than hoisted, so the
        // asymmetry stays visible.

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            TryScore(ref state, slot, Team.Red, ref events);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            TryScore(ref state, slot, Team.Blue, ref events);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            TryGrab(world, ref state, slot, Team.Red, ref events);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            TryGrab(world, ref state, slot, Team.Blue, ref events);
    }

    /// <summary>
    /// <c>Character.events/Collision with IntelligenceBaseRed.xml</c>: a red
    /// player standing on the red base with the blue flag scores.
    /// </summary>
    private static void TryScore(ref WorldState state, int slot, Team baseTeam, ref TickEvents events)
    {
        if (!state.Alive[slot])
            return;

        ref readonly var home = ref Of(ref state, baseTeam);
        if (!home.HasBase)
            return;

        ref readonly var character = ref state.Characters[slot];

        // `if(team == TEAM_RED and intel == true)`. No gate test, no uber test
        // and no cloak test — the capture event is three lines where the grab is
        // one long condition.
        if (character.Team != baseTeam || !character.CarryingIntel)
            return;

        if (!BoxAt(home.HomeX, home.HomeY).Overlaps(Aabb.ForCharacter(character.Box, character.X, character.Y)))
            return;

        Score(ref state, slot, ref events);
    }

    /// <summary>
    /// <c>Character.events/Collision with IntelligenceRed.xml</c>: an enemy
    /// touching a briefcase takes it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>place_meeting(x, y, TeamGate)</c> is the object <c>TeamGate</c>
    /// and not <c>Gate</c>.</b> <c>IntelGate</c> is its <em>sibling</em> under
    /// <c>Gate</c>, so standing inside an intel gate does not stop a grab and
    /// standing inside a team gate — either team's — does. That is what stops a
    /// player reaching through their own spawn door.
    /// <para>
    /// ⚠️ <b>The gate test asks about solidity nowhere.</b> A team gate is
    /// non-solid to its own team, and this still refuses the grab: it is a plain
    /// mask overlap, so a red player standing in the red gate cannot grab
    /// either.
    /// </para>
    /// <para>
    /// <c>canGrabIntel or other.alarm[0] &lt;= 1</c>: a flag at its base rests
    /// at -1 and is therefore grabbable by anyone, cooldown or not. See
    /// <see cref="IntelState.RecoverTimer"/> for the zero case.
    /// </para>
    /// </remarks>
    private static void TryGrab(
        CollisionWorld world,
        ref WorldState state,
        int slot,
        Team flagTeam,
        ref TickEvents events)
    {
        if (!state.Alive[slot])
            return;

        if (state.MapChanging)
            return;

        ref readonly var flag = ref Of(ref state, flagTeam);
        if (!flag.OnField || !flag.Grabbable)
            return;

        ref readonly var character = ref state.Characters[slot];

        // The enemy of the flag's team, and only red or blue — a spectator has
        // no character to be here with.
        var taker = flagTeam == Team.Red ? Team.Blue : Team.Red;
        if (character.Team != taker || character.Ubered || character.Cloak)
            return;

        if (!character.CanGrabIntel && flag.RecoverTimer > 1)
            return;

        if (!BoxAt(flag.X, flag.Y).Overlaps(Aabb.ForCharacter(character.Box, character.X, character.Y)))
            return;

        if (world.PlaceMeetingKind(character.Box, character.X, character.Y, SolidKind.TeamGate))
            return;

        Grab(ref state, slot, ref events);
    }

    /// <summary>
    /// <c>Scripts/Events/doEventGrabIntel.gml</c>: the briefcase disappears and
    /// the carrier inherits its remaining return timer.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Which flag is taken comes from the <em>grabber's team</em>, not
    /// from the instance that was touched.</b> The script re-derives it —
    /// <c>if(argument0.team == TEAM_RED) ... IntelligenceBlue</c> — so a red
    /// player always takes the blue briefcase whatever collided. Same answer as
    /// the collision event that called it, and worth knowing because the script
    /// is also reachable from a client message on the client side.
    /// <para>
    /// <c>max(0, alarm[0])</c> turns the resting -1 into 0, which is what makes
    /// a flag taken off its base drop with a dead alarm.
    /// </para>
    /// </remarks>
    public static void Grab(ref WorldState state, int slot, ref TickEvents events)
    {
        if (!state.IsAlive(slot))
            return;

        ref var character = ref state.Characters[slot];

        var flagTeam = character.Team switch
        {
            Team.Red => Team.Blue,
            Team.Blue => Team.Red,

            // `else exit` — a spectator grabbing nothing.
            _ => Team.Spectator,
        };

        if (flagTeam == Team.Spectator)
            return;

        ref var flag = ref Of(ref state, flagTeam);
        if (!flag.OnField)
            return;

        character.IntelRecharge = Math.Max(0, flag.RecoverTimer);
        character.CarryingIntel = true;

        flag.OnField = false;
        flag.RecoverTimer = Timers.Inactive;

        events.GrabbedIntel[slot] = true;
    }

    /// <summary>
    /// <c>Scripts/Events/doEventDropIntel.gml</c>: the briefcase reappears where
    /// the carrier is standing, with whatever timer they charged.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A drop caused by the carrier's death broadcasts nothing.</b>
    /// <c>sendEventDropIntel</c>'s own comment: "This event is not sent when the
    /// character dies to avoid issues with order of execution. Instead,
    /// character destruction implicitly drops the intel." So
    /// <see cref="WorldState.Clear"/> calls this with
    /// <paramref name="report"/> false and every client infers the drop from the
    /// <c>PLAYER_DEATH</c> it already has.
    /// <para>
    /// The 300-tick <c>alarm[1]</c> is why a player cannot juggle a flag to keep
    /// its timer alive: they may not touch it again for ten seconds unless its
    /// own alarm has nearly run out.
    /// </para>
    /// </remarks>
    public static void Drop(ref WorldState state, int slot, ref TickEvents events)
    {
        if (Drop(ref state, slot))
            events.DroppedIntel[slot] = true;
    }

    /// <summary>
    /// The same drop with no message: the implicit one
    /// <c>Character.events/Destroy.xml</c> performs.
    /// </summary>
    /// <returns>Whether the character was carrying anything.</returns>
    public static bool Drop(ref WorldState state, int slot)
    {
        if (!state.IsAlive(slot))
            return false;

        ref var character = ref state.Characters[slot];
        if (!character.CarryingIntel)
            return false;

        var flagTeam = character.Team switch
        {
            Team.Red => Team.Blue,
            Team.Blue => Team.Red,

            // The reference calls show_error here — a spectator cannot be
            // carrying anything, so there is nothing sane to do either way.
            _ => Team.Spectator,
        };

        if (flagTeam == Team.Spectator)
            return false;

        character.CarryingIntel = false;
        character.CanGrabIntel = false;
        character.IntelGrabTimer = GrabCooldown;

        ref var flag = ref Of(ref state, flagTeam);
        flag.OnField = true;
        flag.X = character.X;
        flag.Y = character.Y;
        flag.RecoverTimer = Timers.SetAlarm(character.IntelRecharge);

        return true;
    }

    /// <summary>
    /// <c>Scripts/Events/doEventReturnIntel.gml</c>: the briefcase goes back to
    /// its base with its alarm cleared.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The reference's "force carriers to drop it" branch is dead code.</b>
    /// When the flag is not on the field the script does
    /// <c>with(Character) if (team == argument0 and intel) event_user(5);</c> —
    /// and <c>Character</c> has no <c>User Event 5</c>, so GM8 runs nothing. It
    /// never matters, because both callers reach here with the instance already
    /// existing: <c>Alarm 0</c> is the flag's own alarm, and
    /// <c>Character.events/Destroy.xml</c> calls <c>doEventDropIntel</c> first.
    /// Modelling the branch would mean deciding what the reference does not do.
    /// <para>
    /// The <c>xprevious</c>/<c>yprevious</c> writes beside it are for mods that
    /// give the flag a collision; nothing in the shipped game reads them.
    /// </para>
    /// </remarks>
    public static void Return(ref WorldState state, Team team, ref TickEvents events)
    {
        if (team is not (Team.Red or Team.Blue))
            return;

        ref var flag = ref Of(ref state, team);
        if (!flag.HasBase)
            return;

        flag.OnField = true;
        flag.X = flag.HomeX;
        flag.Y = flag.HomeY;
        flag.RecoverTimer = Timers.Inactive;

        if (team == Team.Red)
            events.ReturnedRedIntel = true;
        else
            events.ReturnedBlueIntel = true;
    }

    /// <summary>
    /// <c>Scripts/Events/doEventScoreIntel.gml</c>: a capture. The carrier's
    /// team scores and the briefcase reappears on its own base.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It does not touch <c>canGrabIntel</c> or <c>alarm[1]</c>.</b> Unlike
    /// a drop, a capture leaves the player able to pick the next flag up
    /// immediately — which they can, because the flag they just captured is
    /// recreated at <em>its own</em> base and not at theirs.
    /// <para>
    /// The stats it also increments (<c>CAPS</c>, <c>POINTS</c>) are scoreboard
    /// bookkeeping no client re-simulates; only <c>global.redCaps</c> is on the
    /// wire, and it is what decides the round.
    /// </para>
    /// </remarks>
    public static void Score(ref WorldState state, int slot, ref TickEvents events)
    {
        if (!state.IsAlive(slot))
            return;

        ref var character = ref state.Characters[slot];
        if (!character.CarryingIntel)
            return;

        // The flag they are carrying is the enemy's, and it is the one that
        // reappears — `instance_create(IntelligenceBaseBlue.x, ..., IntelligenceBlue)`
        // for a red capper.
        var flagTeam = character.Team switch
        {
            Team.Red => Team.Blue,
            Team.Blue => Team.Red,
            _ => Team.Spectator,
        };

        if (flagTeam == Team.Spectator)
            return;

        if (character.Team == Team.Red)
            state.RedCaps++;
        else
            state.BlueCaps++;

        ref var flag = ref Of(ref state, flagTeam);

        // A fresh instance: at the base, with its alarm at GM8's default rest.
        flag.OnField = true;
        flag.X = flag.HomeX;
        flag.Y = flag.HomeY;
        flag.RecoverTimer = Timers.Inactive;

        character.CarryingIntel = false;

        // doEventScoreIntel.gml:10-13, above the counter it also bumps.
        Scoring.Award(ref state, slot, StatIndex.Caps, 1);
        Scoring.Award(ref state, slot, StatIndex.Points, 2);

        events.ScoredIntel[slot] = true;
    }
}

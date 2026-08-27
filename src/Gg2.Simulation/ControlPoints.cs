using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// One control point: who owns it and how far the other team has got.
/// </summary>
/// <remarks>
/// <c>Objects/Map elements/Gamemode-specific/CP/ControlPoint.events/</c>. The
/// object serves four gamemodes — symmetrical CP, attack/defend, King of the
/// Hill and Arena — and its <c>mode</c> decides which of them it is behaving as.
/// <para>
/// ⚠️ <b>A defaulted <see cref="ControlPointState"/> is owned by red and being
/// captured by red.</b> <c>User Event 0</c> writes <c>team = -1</c> and
/// <c>cappingTeam = -1</c>, and <see cref="Team"/> has no -1 — the same trap
/// <see cref="WorldState.RoundDecided"/> exists to avoid. Both are flags here,
/// and <see cref="ControlPoints.Neutral"/> is the factory.
/// </para>
/// </remarks>
public struct ControlPointState
{
    /// <summary>Whether anybody owns the point at all. False is the reference's <c>team = -1</c>.</summary>
    public bool Owned;

    /// <summary>Who owns it, where <see cref="Owned"/>.</summary>
    public Team Team;

    /// <summary>Whether a capture is in progress. False is <c>cappingTeam = -1</c>.</summary>
    public bool BeingCaptured;

    /// <summary>Who is capturing, where <see cref="BeingCaptured"/>.</summary>
    public Team CappingTeam;

    /// <summary>
    /// Progress towards <see cref="ControlPoints.CapTime"/>. A real: a scout
    /// contributes 2 a tick and a lone ubered player holds it at whatever it is.
    /// </summary>
    public double Capping;

    /// <summary>
    /// <c>cappers</c>: the summed <c>capStrength</c> of the capturing team on
    /// the point, as of the last tick that advanced the capture.
    /// </summary>
    /// <remarks>
    /// Stored rather than derived because the reference stores it, and because
    /// it is what the reversion rate reads on a tick where nobody is capping.
    /// </remarks>
    public double Cappers;

    /// <summary>
    /// 0 nobody, 1 only ubered players, 2 at least one player who can capture.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Three states, not a count.</b> <c>redPresence = max(2, ...)</c> for
    /// an ordinary player and <c>max(1, ...)</c> for an ubered one, so the value
    /// says <em>what kind</em> of presence there is and never how many. An
    /// ubered player alone (1) cannot capture but does stop the point
    /// reverting; that distinction is the whole reason it is not a bool.
    /// </remarks>
    public int RedPresence;

    /// <inheritdoc cref="RedPresence"/>
    public int BluePresence;

    /// <summary>Last tick's <see cref="RedPresence"/>, which the cap-start test reads.</summary>
    public int OldRedPresence;

    /// <inheritdoc cref="OldRedPresence"/>
    public int OldBluePresence;

    /// <summary>
    /// <c>capTime</c>: how much progress this point takes to change hands.
    /// </summary>
    /// <remarks>
    /// 300 for every point in every mode except the control-point ones, where
    /// <c>ControlPointHUD.events/Create.xml</c> overwrites it per point — see
    /// <see cref="ControlPoints.CapTimeFor"/>. It is per point, not per map: on
    /// a five-point symmetrical map the middle point takes three times as long
    /// as the two on the ends.
    /// </remarks>
    public double CapTime;

    /// <summary>
    /// Whether the point refuses to be captured at all.
    /// </summary>
    /// <remarks>
    /// Set from outside in KotH and Arena, worked out from the neighbouring
    /// points in the CP modes, and forced true for everybody while
    /// <see cref="WorldState.MapChanging"/>.
    /// </remarks>
    public bool Locked;
}

/// <summary>
/// The capture points, and the characters standing on them.
/// </summary>
/// <remarks>
/// <c>ControlPoint.events/Step.xml</c>, <c>Scripts/Maps/pointCapture.gml</c>,
/// and the <c>cappingPoint</c> block of
/// <c>Character.events/Begin Step.xml:429-439</c>.
/// docs/SIMULATION.md, "Settled: the control point", has the full rules.
/// </remarks>
public static class ControlPoints
{
    /// <summary>
    /// <c>capTime = 300</c>: ten seconds of one ordinary capper.
    /// </summary>
    public const double CapTime = 300;

    /// <summary>
    /// A point nobody owns and nobody is capturing — <c>User Event 0</c>'s
    /// <c>team = -1; cappingTeam = -1; capping = 0</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Use this rather than <c>default</c>: <see cref="Team.Red"/> is 0, so a
    /// zeroed point is red's and being taken by red.
    /// <para>
    /// ⚠️ <b>Locked, which is right for three of the five modes and wrong for
    /// two.</b> <c>User Event 0</c> writes <c>locked = true</c> for Arena, KotH
    /// and DKotH and <c>locked = false</c> for both control-point
    /// arrangements — <see cref="Rounds.Start"/> applies that difference. It is
    /// only visible on a point the per-mode rules never write to, which is
    /// exactly the neutral middle point a symmetrical map opens on.
    /// </para>
    /// </remarks>
    public static ControlPointState Neutral => new()
    {
        Owned = false,
        BeingCaptured = false,
        Locked = true,
        CapTime = CapTime,
    };

    /// <summary>
    /// The per-point capture times a control-point map hands out.
    /// </summary>
    /// <remarks>
    /// <c>ControlPointHUD.events/Create.xml:12-71</c>, and it is a lookup table
    /// rather than a formula — the reference writes out every case from one
    /// point to five.
    /// <list type="bullet">
    /// <item>
    /// <b>Symmetrical</b> (<c>basetime = 7*30</c>) is a hill: the ends are
    /// cheapest and the middle dearest, mirrored. Five points are
    /// <c>1, 2, 3, 2, 1</c> times seven seconds.
    /// </item>
    /// <item>
    /// <b>Attack/defend</b> (<c>basetime = 6*30</c>) is a ramp: the point
    /// nearest the attackers is cheapest and each one behind it costs more, so
    /// pushing gets progressively harder. Five points are <c>5, 4, 3, 2, 1</c>
    /// times six seconds, counting from point 1.
    /// </item>
    /// </list>
    /// ⚠️ <b>The multipliers are not whole numbers below five points</b>
    /// (<c>1.5</c>, <c>2.25</c>, <c>4.5</c>, <c>7.5</c>), and the two-point
    /// attack/defend row carries a commented-out alternative in the reference —
    /// <c>basetime*2.5/*5*/</c> — so the live value is 2.5. A map with more
    /// than five points falls through every branch and keeps the default 300,
    /// which is also what the reference does.
    /// </remarks>
    /// <param name="index">0-based; the reference's <c>cp</c> is this plus one.</param>
    public static double CapTimeFor(bool attackDefend, int count, int index)
    {
        var basetime = attackDefend ? 6 * 30 : 7 * 30;

        double[]? multipliers = attackDefend
            ? count switch
            {
                1 => [15],
                2 => [10, 2.5],
                3 => [7.5, 5, 2.5],
                4 => [6, 4.5, 3, 1.5],
                5 => [5, 4, 3, 2, 1],
                _ => null,
            }
            : count switch
            {
                1 => [9],
                2 => [4.5, 4.5],
                3 => [2.25, 4.5, 2.25],
                4 => [1.5, 3, 3, 1.5],
                5 => [1, 2, 3, 2, 1],
                _ => null,
            };

        if (multipliers is null || (uint)index >= (uint)multipliers.Length)
            return CapTime;

        return basetime * multipliers[index];
    }

    /// <summary>
    /// Who owns each point before anybody touches it.
    /// </summary>
    /// <remarks>
    /// <c>ControlPointHUD.events/Create.xml:73-92</c>. Symmetrical splits them
    /// down the middle — everything before the midpoint is red's, everything
    /// after is blue's, and an exact middle point (which only exists with an odd
    /// count) starts neutral. Attack/defend gives **every** point to blue, who
    /// are defending.
    /// </remarks>
    public static void AssignStartingOwners(ref WorldState state)
    {
        var count = state.ControlPointCount;

        for (var i = 0; i < count; i++)
        {
            ref var point = ref state.ControlPoints[i];

            if (state.AttackDefend)
            {
                point.Owned = true;
                point.Team = Team.Blue;
                continue;
            }

            // `middlePoint = (total+1)/2` against the 1-based `i`, so with an
            // even count nothing is ever equal to it and there is no neutral
            // point.
            var middle = (count + 1) / 2.0;
            var number = i + 1;

            point.Owned = number != middle;
            point.Team = number < middle ? Team.Red : Team.Blue;
        }
    }

    /// <summary>
    /// <c>capStrength</c>: how fast this class captures.
    /// </summary>
    /// <remarks>
    /// <c>Character.events/Create.xml:31</c> gives everybody 1 and
    /// <c>Scout.events/Create.xml:58</c> overrides it with 2 — the only class
    /// that does, and the reason a scout is the fastest capper in the game.
    /// </remarks>
    public static double CapStrength(PlayerClass playerClass) =>
        playerClass == PlayerClass.Scout ? 2 : 1;

    /// <summary>
    /// Builds the points and their capture zones from the map.
    /// </summary>
    /// <remarks>
    /// <b>The order is the wire's.</b> <c>ControlPointHUD</c> walks
    /// <c>global.cp[1..totalControlPoints]</c> by number, <c>DKothHUD</c> writes
    /// red then blue, and the rest have one point — so the numbered types go in
    /// numbered order and everything else in the order the map lists them. Same
    /// decision <c>Gg2.Server.MatchState</c> used to make, moved here now that
    /// the points are simulated.
    /// <para>
    /// ⚠️ <b>A capture zone belongs to the <em>nearest</em> point, and that is
    /// resolved once.</b> <c>CaptureZone.events/Alarm 0.xml</c> is
    /// <c>cp = instance_nearest(x, y, ControlPoint)</c> — or the arena point if
    /// there is one — run a step after creation so a custom map's entity order
    /// cannot change the answer. Neither the zones nor the points ever move, so
    /// resolving it at load is the same answer for ever.
    /// </para>
    /// </remarks>
    public static void Place(ref WorldState state, LevelMap map, out CaptureZone[] zones)
    {
        ArgumentNullException.ThrowIfNull(map);

        state.ControlPointCount = 0;

        var positions = new List<(double X, double Y)>();

        foreach (var type in OrderedTypes(state.GameMode))
        {
            foreach (var entity in map.Entities)
            {
                if (!string.Equals(entity.Type, type, StringComparison.Ordinal))
                    continue;

                if (state.ControlPointCount >= WorldState.MaxControlPoints)
                    continue;

                state.ControlPoints[state.ControlPointCount] = Neutral;
                state.ControlPointCount++;
                positions.Add((entity.X, entity.Y));
            }
        }

        zones = BuildZones(map, positions);
    }

    /// <summary>Which point objects a mode places, in serialisation order.</summary>
    private static string[] OrderedTypes(GameMode mode) => mode switch
    {
        GameMode.Arena => [EntityTypes.ArenaControlPoint],
        GameMode.KingOfTheHill => [EntityTypes.KothControlPoint],

        // DKothHUD writes red then blue, in that order.
        GameMode.DualKingOfTheHill => [EntityTypes.KothRedControlPoint, EntityTypes.KothBlueControlPoint],

        GameMode.ControlPoints =>
        [
            EntityTypes.ControlPoint1,
            EntityTypes.ControlPoint2,
            EntityTypes.ControlPoint3,
            EntityTypes.ControlPoint4,
            EntityTypes.ControlPoint5,
        ],

        _ => [],
    };

    /// <summary>
    /// Each <c>CapturePoint</c> entity as a rectangle, tagged with the point it
    /// is nearest.
    /// </summary>
    /// <remarks>
    /// <c>CaptureZoneS</c> is 42x42 with its origin at (0, 0) and a
    /// <c>RECTANGLE</c> mask on <b><c>FULL</c></b> bounds — the whole image, no
    /// alpha involved — which is exactly the shape
    /// <see cref="SolidRect.FromEntity"/> was measured against in phase 3 for
    /// the collider sprites. So the box is the phase-3 rule with a different
    /// size, rather than a new inference.
    /// </remarks>
    private static CaptureZone[] BuildZones(LevelMap map, List<(double X, double Y)> points)
    {
        const int ZoneSize = 42;

        var zones = new List<CaptureZone>();

        foreach (var entity in map.Entities)
        {
            if (!string.Equals(entity.Type, EntityTypes.CapturePoint, StringComparison.Ordinal))
                continue;

            if (points.Count == 0)
                continue;

            var rect = SolidRect.FromEntity(
                entity.X, entity.Y, entity.XScale, entity.YScale, ZoneSize, ZoneSize, SolidKind.Obstacle);

            zones.Add(new CaptureZone(rect.Left, rect.Top, rect.Right, rect.Bottom, Nearest(entity, points)));
        }

        return [.. zones];
    }

    /// <summary>
    /// <c>instance_nearest(x, y, ControlPoint)</c> — plain Euclidean distance
    /// between the two instances' positions, ties going to the lower index.
    /// </summary>
    /// <remarks>
    /// GM8 compares squared distances internally and keeps the first instance at
    /// the minimum, which in id order is the older one. Every shipped map puts
    /// its zones unambiguously nearer one point than another, so the tie rule is
    /// not reachable — it is written down because the alternative is silence.
    /// </remarks>
    private static int Nearest(LevelEntity zone, List<(double X, double Y)> points)
    {
        var best = 0;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < points.Count; i++)
        {
            var dx = points[i].X - zone.X;
            var dy = points[i].Y - zone.Y;
            var distance = (dx * dx) + (dy * dy);

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = i;
        }

        return best;
    }

    /// <summary>
    /// <c>Character.events/Begin Step.xml:429-439</c>: which point, if any, this
    /// character is standing on.
    /// </summary>
    /// <remarks>
    /// <c>collision_point(x, y, CaptureZone, 0, 0)</c> — <c>prec = 0</c>, so it
    /// is the zone's bounding box and not its pixels, and the character is a
    /// <em>point</em> rather than its own box. Standing with your feet over the
    /// edge of a zone does not count; the origin has to be inside it.
    /// <para>
    /// ⚠️ <b>A cloaked spy is not on the point, and neither is one still fading
    /// in.</b> <c>zone >= 0 and !cloak and cloakAlpha == 1</c> — the second
    /// clause means the seconds after decloaking do not count either.
    /// </para>
    /// <para>
    /// ⚠️ <b>Not measured against the running game.</b> Whether GM8 rounds the
    /// point before testing it is the open question, and it costs at most half a
    /// pixel at the rim of a 42-pixel square. See docs/SIMULATION.md.
    /// </para>
    /// </remarks>
    public static void BeginStep(CollisionWorld world, ref CharacterState character)
    {
        ArgumentNullException.ThrowIfNull(world);

        character.CappingPoint = CharacterState.Nobody;

        if (character.Cloak || !Gml.Equal(character.CloakAlpha, 1))
            return;

        var x = CollisionWorld.RoundPosition(character.X);
        var y = CollisionWorld.RoundPosition(character.Y);

        foreach (var zone in world.CaptureZones)
        {
            if (x < zone.Left || x > zone.Right || y < zone.Top || y > zone.Bottom)
                continue;

            character.CappingPoint = (byte)zone.Point;
            return;
        }
    }

    /// <summary>
    /// Every point's Step: who is standing on it, and what that does to the
    /// capture meter.
    /// </summary>
    /// <remarks>
    /// <b>Where this sits is the same argument <see cref="Rounds.Step"/> makes.</b>
    /// A <c>ControlPoint</c> is a map object created by <c>basicRoomSetup</c>,
    /// so it is older than every <c>Character</c> and its Step runs before
    /// theirs — but the presence scan reads <c>cappingPoint</c>, which the
    /// characters wrote in their <em>Begin</em> Step, so the answer is this
    /// tick's either way.
    /// <para>
    /// The <c>alpha</c>/<c>fade</c> pulse at the top of the reference's Step is
    /// the point's own animation and is not transcribed.
    /// </para>
    /// </remarks>
    public static void Step(ref WorldState state)
    {
        for (var i = 0; i < state.ControlPointCount; i++)
            StepOne(ref state, i);
    }

    /// <summary>
    /// The three points at which a moving capture meter earns an extra
    /// <c>CAPS_UPDATE</c>.
    /// </summary>
    /// <remarks>
    /// <c>floor(capping) == floor(capTime - capTime/4) || floor(capping) ==
    /// floor(capTime/4) || floor(capping) == floor(capTime/10)</c> — 225, 75 and
    /// 30 at the standard 300. ⚠️ It is an <em>equality on the floor</em>, so a
    /// meter moving 2 a tick can step straight over an odd threshold and never
    /// ask; that is the reference's, and it is why these are worth writing down
    /// rather than reading as "every quarter".
    /// </remarks>
    private static bool IsSyncThreshold(double capping)
    {
        var floor = Gml.Floor(capping);

        return floor == Gml.Floor(CapTime - (CapTime / 4))
               || floor == Gml.Floor(CapTime / 4)
               || floor == Gml.Floor(CapTime / 10);
    }

    private static void StepOne(ref WorldState state, int index)
    {
        Relock(ref state, index);

        ref var point = ref state.ControlPoints[index];

        // `if (arenaRoundEnd || global.mapchanging) locked = true;` — the
        // autolock, "regardless of game mode".
        if (state.MapChanging || Arena.RoundEnded(state))
            point.Locked = true;

        if (point.Locked)
        {
            // ⚠️ It clears the meter and exits, so a point locked mid-capture
            // loses the progress rather than pausing it. The presences are left
            // where they were, which is what the KotH HUD's "nobody capping"
            // test then reads.
            point.Capping = 0;
            return;
        }

        point.OldRedPresence = point.RedPresence;
        point.OldBluePresence = point.BluePresence;
        point.RedPresence = 0;
        point.BluePresence = 0;

        double redCappers = 0;
        double blueCappers = 0;

        // ⚠️ The reference wraps this scan in `if(not arenaRoundEnd)`, and that
        // guard is **dead code**: `arenaRoundEnd` sets `locked = true` twelve
        // lines above and the `if(locked) { capping = 0; exit; }` below it has
        // already returned. Not transcribed, for the same reason
        // `doEventReturnIntel`'s `event_user(5)` branch is not — modelling an
        // unreachable branch means deciding what the reference does not do.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref readonly var character = ref state.Characters[slot];
            if (character.CappingPoint != index)
                continue;

            var strength = CapStrength(character.Class);

            if (character.Team == Team.Red)
            {
                if (character.Ubered)
                {
                    point.RedPresence = Math.Max(1, point.RedPresence);
                }
                else
                {
                    point.RedPresence = Math.Max(2, point.RedPresence);
                    redCappers += strength;
                }
            }
            else
            {
                // ⚠️ `else`, not `if team == TEAM_BLUE`. A spectator has no
                // character, so the only two teams that reach here are red and
                // blue — but the asymmetry is the reference's and is worth
                // keeping visible.
                if (character.Ubered)
                {
                    point.BluePresence = Math.Max(1, point.BluePresence);
                }
                else
                {
                    point.BluePresence = Math.Max(2, point.BluePresence);
                    blueCappers += strength;
                }
            }
        }

        // ⚠️ 1 for every point except an ArenaControlPoint's, where it scales
        // by how much of the *enemy* team is still alive — see
        // Arena.CappingPower.
        //
        // ⚠️ Both teams' are computed, and the reference then uses two different
        // ones: the *reverting* branches take the power of whoever was already
        // capping when the Step began, and the capture branch takes the power of
        // the team it has just assigned. They differ on the tick a capture
        // starts, which is the tick a bot's first hop onto a point lands on.
        var redPower = 1.0;
        var bluePower = 1.0;

        if (state.GameMode == GameMode.Arena)
        {
            // CountFor answers with the *enemies* of the team it is given, which
            // is already the cross the reference makes: red's power comes from
            // blue's numbers. Crossing it a second time here would cancel it out
            // and leave every power at 1.
            CountFor(state, Team.Red, out var redEnemies, out var redEnemiesAlive);
            CountFor(state, Team.Blue, out var blueEnemies, out var blueEnemiesAlive);

            redPower = Arena.CappingPower(redEnemies, redEnemiesAlive);
            bluePower = Arena.CappingPower(blueEnemies, blueEnemiesAlive);
        }

        // `if (cappingTeam == RED or BLUE) cappingTeamPower = cappingPower[cappingTeam]
        //  else cappingTeamPower = 1`
        var cappingTeamPower = point.BeingCaptured
            ? point.CappingTeam == Team.Red ? redPower : bluePower
            : 1.0;

        Advance(ref point, redCappers, blueCappers, cappingTeamPower, redPower, bluePower);

        if (point.Capping <= 0)
        {
            point.Capping = 0;
            point.BeingCaptured = false;
        }
        else if (IsSyncThreshold(point.Capping))
        {
            state.ScoreboardSyncRequested = true;
        }
        else if (point.Capping >= point.CapTime)
        {
            // ⚠️ `else if`, so a meter that lands exactly on a threshold on the
            // tick it would also complete does not capture this tick. Unreachable
            // at the standard capTime — 225, 75 and 30 are all below 300 — and
            // kept in the reference's shape because a custom capTime could reach
            // it.
            AwardCappers(ref state, index);
            Capture(ref point);
            state.ScoreboardSyncRequested = true;

            // ControlPoint.events/Step.xml:222 — `if ControlPointHUD.mode == 1
            // ControlPointHUD.timer += 5400`. Every capture buys the attackers
            // three more minutes, which is why attack/defend needs the 9,000
            // cap in Rounds.StepControlPoints.
            if (state.GameMode == GameMode.ControlPoints && state.AttackDefend)
                state.RoundTimer += Rounds.AttackDefendCaptureBonus;
        }
    }

    /// <summary>
    /// The mode-specific locking at the top of a point's Step.
    /// </summary>
    /// <remarks>
    /// <b>Every mode but KotH and Arena works its own lock out here, every
    /// tick</b> — the reference's own comment on the <c>else</c> branch is
    /// "Locking is done externally in Arena mode and KotH", which is why
    /// <see cref="GameMode.KingOfTheHill"/> is absent below and its hill is
    /// unlocked only by the HUD.
    /// <para>
    /// <b>Dual King of the Hill's rule is a cross-lock</b>
    /// (<c>ControlPoint.events/Step.xml:12-19</c>): red's own hill is locked
    /// while red holds <em>blue's</em>, and blue's is locked while blue holds
    /// red's. So whoever is ahead cannot be counter-attacked at home, and only
    /// one hill is ever in play. The reference calls its own spelling of it
    /// "bruteforce because the below code won't lock the two edge cases without
    /// causing errors in other modes".
    /// </para>
    /// <para>
    /// ⚠️ <b>And it is what stops <c>UNLOCKCP</c> ever being sent in DKotH.</b>
    /// A <c>ControlPoint</c> is a map instance and a HUD is created by
    /// <c>basicRoomSetup</c> afterwards, so the points' Steps run <em>first</em>:
    /// on the tick <c>cpUnlock</c> reaches zero they unlock themselves here, and
    /// the HUD's <c>if (both locked)</c> is already false when it runs. Clients
    /// unlock from their own copy of this line rather than from a message —
    /// which is exactly what the <c>cpUnlock == 150</c> scoreboard update
    /// exists to keep in step. KotH, whose hill never reaches this code, does
    /// send the message.
    /// </para>
    /// </remarks>
    private static void Relock(ref WorldState state, int index)
    {
        if (state.GameMode == GameMode.ControlPoints)
        {
            RelockNeighbour(ref state, index);
            return;
        }

        if (state.GameMode != GameMode.DualKingOfTheHill || state.ControlPointCount < 2)
            return;

        ref var point = ref state.ControlPoints[index];

        if (state.ControlPointUnlock > 0)
        {
            point.Locked = true;
            return;
        }

        // Index 0 is KothRedControlPoint (`cp = 1`) and index 1 is
        // KothBlueControlPoint (`cp = 2`) — ControlPoints.Place puts them in
        // that order, and the reference's two `if`s are keyed on those numbers.
        //
        // ⚠️ Each reads the *other* point's team as of this tick, and the two
        // points' Steps run in index order — so index 1 sees index 0 already
        // updated and index 0 does not. Observable only on the tick a hill
        // changes hands, and it is the reference's instance order.
        var other = state.ControlPoints[index == 0 ? 1 : 0];
        var lockedBy = index == 0 ? Team.Red : Team.Blue;

        point.Locked = other.Owned && other.Team == lockedBy;
    }

    /// <summary>
    /// The control-point modes' locking: a point is only in play if the one
    /// beside it has already fallen.
    /// </summary>
    /// <remarks>
    /// <c>ControlPoint.events/Step.xml:20-35</c>. Read from each team's side:
    /// <list type="bullet">
    /// <item>
    /// <b>A blue point</b> is locked unless the point to its <em>left</em> is
    /// red's — red pushes left to right and has to take them in order.
    /// </item>
    /// <item>
    /// <b>A red point</b> is locked unless the point to its <em>right</em> is
    /// blue's — blue pushes the other way.
    /// </item>
    /// </list>
    /// ⚠️ <b>The end points are special by omission.</b> The blue rule is
    /// guarded by <c>if (cp &gt; 1)</c> and the red one by
    /// <c>if (cp &lt; totalControlPoints)</c>, so the first point when blue owns
    /// it and the last when red owns it fall through <em>without their lock
    /// being written at all</em> — they keep whatever they had, which from
    /// <c>User Event 0</c> is unlocked. Those are the two points that end the
    /// match, and they are always takeable.
    /// <para>
    /// ⚠️ <b>And a neutral point is never touched either.</b> Neither branch
    /// runs when <c>team</c> is -1, so a middle point on an odd-numbered
    /// symmetrical map stays unlocked from the start — which is what makes it
    /// the opening fight.
    /// </para>
    /// <para>
    /// ⚠️ <b>In attack/defend a red point is locked unconditionally</b>
    /// (<c>if(mode==1 &amp;&amp; team == TEAM_RED) locked = true</c>), after the
    /// rules above. Blue is defending and never takes anything back.
    /// </para>
    /// </remarks>
    private static void RelockNeighbour(ref WorldState state, int index)
    {
        ref var point = ref state.ControlPoints[index];

        if (point.Owned && point.Team == Team.Blue)
        {
            if (index > 0)
            {
                ref readonly var left = ref state.ControlPoints[index - 1];
                point.Locked = !left.Owned || left.Team != Team.Red;
            }
        }
        else if (point.Owned && point.Team == Team.Red)
        {
            if (index < state.ControlPointCount - 1)
            {
                ref readonly var right = ref state.ControlPoints[index + 1];
                point.Locked = !right.Owned || right.Team != Team.Blue;
            }

            if (state.AttackDefend)
                point.Locked = true;
        }
    }

    /// <summary>
    /// How many players oppose <paramref name="team"/>, and how many are alive.
    /// </summary>
    private static void CountFor(in WorldState state, Team team, out int enemies, out int liveEnemies)
    {
        var other = team == Team.Red ? Team.Blue : Team.Red;

        enemies = 0;
        liveEnemies = 0;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasPlayer[slot] || state.Players[slot].Team != other)
                continue;

            enemies++;

            if (state.Alive[slot])
                liveEnemies++;
        }
    }

    /// <summary>
    /// The three-way branch on who is standing on the point.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// <b>Both teams present</b> — nothing happens at all. The meter does not
    /// move in either direction; a contested point is simply frozen.
    /// </item>
    /// <item>
    /// <b>Nobody present</b> — the meter reverts at 1 a tick.
    /// </item>
    /// <item>
    /// <b>One team present</b> — and then it depends on whether they are only
    /// ubered, whether the <em>other</em> team has progress banked, and whether
    /// they already own it.
    /// </item>
    /// </list>
    /// <para>
    /// ⚠️ <b>Reverting the enemy's progress is much faster than capturing.</b>
    /// <c>capping -= (1 + cappers*0.5)</c> against the capture's
    /// <c>+cappers</c>, so two ordinary players wipe out an enemy capture at 2 a
    /// tick and then start their own at 2 a tick — the same rate, which is why
    /// taking a half-captured point takes twice as long as taking a fresh one.
    /// </para>
    /// <para>
    /// ⚠️ <b>Past two cappers the capture rate is halved.</b>
    /// <c>cappers &lt;= 2 ? cappers : cappers/2 + 1</c>: two ordinary players
    /// cap at 2, four cap at 3 rather than 4. A single scout is
    /// <c>capStrength</c> 2 and so caps at the full 2, where two scouts are 4
    /// and cap at 3.
    /// </para>
    /// </remarks>
    private static void Advance(
        ref ControlPointState point,
        double redCappers,
        double blueCappers,
        double cappingTeamPower,
        double redPower,
        double bluePower)
    {
        if (point.RedPresence > 0 && point.BluePresence > 0)
        {
            // Contested. The reference's branch is entirely sound and logging.
            return;
        }

        if (point.RedPresence == 0 && point.BluePresence == 0)
        {
            point.Capping = Math.Max(point.Capping - cappingTeamPower, 0);
            return;
        }

        var onPoint = point.RedPresence > 0 ? Team.Red : Team.Blue;
        var presence = point.RedPresence > 0 ? point.RedPresence : point.BluePresence;
        var cappers = point.RedPresence > 0 ? redCappers : blueCappers;

        if (presence == 1)
        {
            // Only ubered players. They cannot capture, but they hold their own
            // team's progress where it is — the meter reverts only if the
            // progress on it belongs to somebody else.
            if (!point.BeingCaptured || point.CappingTeam != onPoint)
                point.Capping = Math.Max(point.Capping - cappingTeamPower, 0);

            return;
        }

        if (point.BeingCaptured && point.CappingTeam != onPoint)
        {
            point.Capping = Math.Max(point.Capping - ((1 + (cappers * 0.5)) * cappingTeamPower), 0);
            return;
        }

        if (point.Owned && point.Team == onPoint)
        {
            // `else if (teamOnPoint != team)` — standing on your own point does
            // nothing. Note it is reached only when the point is not partially
            // captured by the enemy, so defending is the branch above.
            return;
        }

        point.BeingCaptured = true;
        point.CappingTeam = onPoint;
        point.Cappers = cappers;

        // ⚠️ The *new* capping team's power, assigned two lines above — not
        // cappingTeamPower, which was read before the assignment.
        var power = onPoint == Team.Red ? redPower : bluePower;

        point.Capping += cappers <= 2
            ? cappers * power
            : ((cappers / 2) + 1) * power;
    }

    /// <summary>
    /// <c>Scripts/Maps/pointCapture.gml</c>'s <c>with(Character)</c> loop: two
    /// points each to everybody standing on the point who is on the capturing
    /// team.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The test is <c>player.team == point.cappingTeam</c>, not "was
    /// helping".</b> A team-mate who arrived this tick and contributed nothing
    /// still scores, and an enemy standing on a point their team just took —
    /// which cannot happen, since a contested point is frozen — would too.
    /// <para>
    /// ⚠️ <b>Called before <see cref="Capture"/></b>, because that is where the
    /// reference has it: the whole loop runs above the
    /// <c>with point { team = cappingTeam; capping = 0 }</c> at the bottom of
    /// the script. It reads <c>cappingTeam</c>, which the capture leaves alone
    /// anyway — but the order is the reference's and costs nothing to keep.
    /// </para>
    /// <para>
    /// <c>cappingPoint</c> is the character's own field, set in its Begin Step
    /// from the capture zone it is standing in — see <see cref="BeginStep"/>.
    /// </para>
    /// </remarks>
    private static void AwardCappers(ref WorldState state, int index)
    {
        var cappingTeam = state.ControlPoints[index].CappingTeam;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot] || state.Characters[slot].CappingPoint != index)
                continue;

            if (!state.HasPlayer[slot] || state.Players[slot].Team != cappingTeam)
                continue;

            Scoring.Award(ref state, slot, StatIndex.Caps, 1);
            Scoring.Award(ref state, slot, StatIndex.Points, 2);
        }
    }

    /// <summary>
    /// <c>Scripts/Maps/pointCapture.gml</c>: the point changes hands.
    /// </summary>
    /// <remarks>
    /// Two lines of it are simulation — <c>team = cappingTeam; capping = 0</c> —
    /// and the rest is the kill log, the sound and the per-capper stats, none of
    /// which any client re-simulates.
    /// <para>
    /// ⚠️ <b><c>cappingTeam</c> is <em>not</em> cleared.</b> The point is owned
    /// by the team that took it and still says they are capturing it; only the
    /// next tick's <c>capping &lt;= 0</c> branch clears that. It is observable —
    /// <c>cappingTeam</c> goes on the wire — and it is the reference's.
    /// </para>
    /// </remarks>
    private static void Capture(ref ControlPointState point)
    {
        point.Owned = true;
        point.Team = point.CappingTeam;
        point.Capping = 0;
    }

    /// <summary>One point as <c>User Event 12</c> writes it.</summary>
    /// <remarks>
    /// <c>team</c> and <c>cappingTeam</c> are signed bytes whose -1 means
    /// neutral, and <c>capping</c> is <c>round(capping)</c> — GML's half to
    /// even, like the briefcase's position.
    /// </remarks>
    public static ControlPointBlock ToBlock(in ControlPointState point) => new(
        point.Owned ? (sbyte)point.Team : (sbyte)-1,
        point.BeingCaptured ? (sbyte)point.CappingTeam : (sbyte)-1,
        WireQuantise.ToUShort(Gml.Round(point.Capping)));
}

/// <summary>
/// A <c>CaptureZone</c>: the rectangle standing in which puts a character on a
/// control point.
/// </summary>
/// <remarks>
/// Read-only map geometry, so it lives in <see cref="CollisionWorld"/> beside
/// the solid rectangles rather than in <see cref="WorldState"/> — a zone never
/// moves and never changes, and the state must stay copyable.
/// <para>
/// Bounds are inclusive world pixels, the same convention as
/// <see cref="SolidRect"/> and <see cref="Aabb"/>.
/// </para>
/// </remarks>
/// <param name="Point">Index into <see cref="WorldState.ControlPoints"/>.</param>
public readonly record struct CaptureZone(int Left, int Top, int Right, int Bottom, int Point);

using System.Runtime.CompilerServices;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// <c>global.classlimits</c>: how many of each class one team may field.
/// </summary>
/// <remarks>
/// Ten entries, indexed by <see cref="PlayerClass"/>, and 255 — effectively no
/// limit — is the default a server ships with. Inline for the reason every
/// buffer in <see cref="WorldState"/> is.
/// </remarks>
[InlineArray(TeamBalance.ClassCount)]
public struct ClassLimitBuffer
{
    private byte _element0;
}

/// <summary>
/// Autobalance: moving somebody across when one team is two players larger.
/// </summary>
/// <remarks>
/// <c>Scripts/Team Balance/ServerBalanceTeams.gml</c>,
/// <c>Scripts/Maps/Arena/serverArenaRestart.gml</c> and
/// <c>Scripts/Classlimits/checkClasslimits.gml</c>.
/// <para>
/// <b>It is a two-stage process with a long fuse.</b> The first time the teams
/// are found uneven the server only broadcasts a notice; the switch itself
/// happens on a later call, once <c>balancecounter</c> has reached the respawn
/// time plus two seconds. Both stages reset the counter when the teams come back
/// level on their own.
/// </para>
/// <para>
/// ⚠️ <b>The counter runs every tick and the check does not.</b>
/// <c>GameServerBeginStep.gml:1-2</c> increments <c>balancecounter</c> at the
/// very top of every frame, but <c>ServerBalanceTeams</c> is only <em>called</em>
/// from three places: a character's death (<c>End Step.xml:30</c>), a player
/// leaving (<c>GameServerBeginStep.gml:70</c>) and a team change
/// (<c>processClientCommands.gml:189</c>). So the fuse burns continuously and
/// the switch lands on whichever of those three happens next — on a quiet server
/// the notice can hang unresolved indefinitely.
/// </para>
/// </remarks>
public static class TeamBalance
{
    /// <summary>How many classes a <see cref="ClassLimitBuffer"/> covers.</summary>
    public const int ClassCount = 10;

    /// <summary>
    /// The gap that counts as unbalanced: one team <b>two or more</b> larger.
    /// </summary>
    /// <remarks>
    /// <c>redteam &gt;= blueteam + 2</c>. A one-player difference is normal on
    /// an odd roster and is never acted on.
    /// </remarks>
    public const int Threshold = 2;

    /// <summary>
    /// Extra ticks after the respawn time before stage 2 fires:
    /// <c>balancecounter &gt;= global.Server_Respawntime + 30*2</c>.
    /// </summary>
    public const int SwitchDelay = 30 * 2;

    /// <summary>
    /// <c>GameServerBeginStep.gml:1-2</c>: the fuse, burnt at the very top of
    /// every frame.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Only while a notice is outstanding</b> — <c>if(serverbalance != 0)</c>
    /// — so the counter is not a free-running clock, and a server that has never
    /// been unbalanced has it at zero.
    /// </remarks>
    public static void Advance(ref WorldState state)
    {
        if (state.BalanceNoticeGiven)
            state.BalanceCounter++;
    }

    /// <summary>
    /// <c>ServerBalanceTeams()</c>, in full.
    /// </summary>
    /// <remarks>
    /// Called from a death, a departure and a team change, and from nowhere
    /// else.
    /// <para>
    /// ⚠️ <b>Arena is excluded</b> (<c>!instance_exists(ArenaHUD)</c>) — it
    /// balances between rounds instead, in <see cref="ArenaRestart"/>, where it
    /// can move several players at once and does not have to wait for anybody to
    /// be dead. And a decided round is excluded too
    /// (<c>global.winners == -1</c>), so nobody is moved during the victory
    /// screen.
    /// </para>
    /// </remarks>
    public static void Check(ref WorldState state, ref TickEvents events)
    {
        if (!state.Autobalance || state.GameMode == GameMode.Arena || state.RoundDecided)
            return;

        var bigger = BiggerTeam(state);

        if (bigger == Team.Spectator)
        {
            // "if balanced, reset counter" — and it clears an outstanding
            // notice too, so a team that evens itself out cancels the switch.
            state.BalanceNoticeGiven = false;
            state.BalanceCounter = 0;
            return;
        }

        if (!state.BalanceNoticeGiven)
        {
            // Stage 1: say so, start the fuse, and do nothing else.
            state.BalanceNoticeGiven = true;
            state.BalanceCounter = 0;
            events.BalanceNoticed = true;
            return;
        }

        if (state.BalanceCounter < state.RespawnTime + SwitchDelay)
            return;

        var victim = LowestScoringDeadPlayer(state, bigger);

        // "if no suitable player found, wait until next time" — and note the
        // counter is *not* cleared, so the next call retries immediately.
        if (victim < 0)
            return;

        state.BalanceNoticeGiven = false;
        state.BalanceCounter = 0;

        Switch(ref state, victim, ref events, armRespawn: true);
    }

    /// <summary>
    /// <c>serverArenaRestart.gml:1-63</c>: Arena's own balance, run between
    /// rounds.
    /// </summary>
    /// <remarks>
    /// The same "two or more larger" test and the same lowest-points pick, and
    /// then three differences that matter:
    /// <list type="bullet">
    /// <item>⚠️ <b>It loops</b>, moving players until the gap is under two —
    /// where <see cref="Check"/> moves at most one per call.</item>
    /// <item>⚠️ <b>There is no "only dead players" guard.</b> Between rounds
    /// everybody is about to be respawned anyway.</item>
    /// <item>⚠️ <b>It does not arm <c>alarm[5]</c>.</b> The round start
    /// respawns everybody directly (<c>respawnEveryone</c>), so a balanced
    /// player would otherwise come back twice.</item>
    /// </list>
    /// <para>
    /// ⚠️ The loop counts down its own local <c>teammore</c>/<c>teamless</c>
    /// rather than recounting, which is what makes it terminate. It also clears
    /// the ordinary fuse on every iteration.
    /// </para>
    /// </remarks>
    public static void ArenaRestart(ref WorldState state, ref TickEvents events)
    {
        var (red, blue) = TeamSizes(state);

        Team bigger;
        int more, less;

        if (red >= blue + Threshold)
        {
            bigger = Team.Red;
            more = red;
            less = blue;
        }
        else if (blue >= red + Threshold)
        {
            bigger = Team.Blue;
            more = blue;
            less = red;
        }
        else
        {
            return;
        }

        while (more >= less + Threshold)
        {
            var victim = LowestScoringPlayer(state, bigger);

            // The reference cannot reach this — `more >= less + 2` guarantees
            // at least two players on the bigger team — but a defaulted -1
            // would be an index into the roster if it ever did.
            if (victim < 0)
                return;

            state.BalanceNoticeGiven = false;
            state.BalanceCounter = 0;

            Switch(ref state, victim, ref events, armRespawn: false);

            more--;
            less++;
        }
    }

    /// <summary>
    /// The switch both paths share: across, re-classed, character destroyed.
    /// </summary>
    /// <param name="armRespawn">
    /// Whether to set <c>alarm[5]</c>. True for the ordinary balance and false
    /// for Arena's, which respawns everybody itself a moment later.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>Destroying the character here is not a death.</b> No
    /// <c>PLAYER_DEATH</c> goes out, nobody is credited, and the client is told
    /// only by the <c>BALANCE</c> message — which is why it carries the class.
    /// <c>WorldState.Clear</c> still runs the Destroy event's own work, so a
    /// carried briefcase is dropped.
    /// </remarks>
    private static void Switch(ref WorldState state, int slot, ref TickEvents events, bool armRespawn)
    {
        ref var player = ref state.Players[slot];

        player.Team = player.Team == Team.Red ? Team.Blue : Team.Red;
        player.Class = Classlimits.Pick(ref state, slot, player.Team, player.Class);

        if (state.IsAlive(slot))
        {
            state.Clear(slot);

            if (armRespawn && state.RespawnTime > 0)
                player.RespawnTimer = state.RespawnTime;
        }

        events.Balanced[slot] = true;
        events.BalancedClass[slot] = player.Class;
    }

    /// <summary>Which team is at least <see cref="Threshold"/> larger, or spectator for neither.</summary>
    private static Team BiggerTeam(in WorldState state)
    {
        var (red, blue) = TeamSizes(state);

        if (red >= blue + Threshold)
            return Team.Red;

        return blue >= red + Threshold ? Team.Blue : Team.Spectator;
    }

    /// <summary>
    /// How many players are on each team.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Players, not characters.</b> A dead player and a player who has
    /// never spawned both count, which is what makes the balance stable rather
    /// than swinging with every death.
    /// </remarks>
    public static (int Red, int Blue) TeamSizes(in WorldState state)
    {
        var red = 0;
        var blue = 0;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasPlayer[slot])
                continue;

            if (state.Players[slot].Team == Team.Red)
                red++;
            else if (state.Players[slot].Team == Team.Blue)
                blue++;
        }

        return (red, blue);
    }

    /// <summary>
    /// The lowest-scoring player on a team who is also <b>dead</b>, or -1.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The guard is on the character, not on the player</b>, and its shape
    /// is <c>if (player.object != -1) if (!(player.object.hp &lt;= 0)) continue;</c>
    /// — so a player with no character at all is eligible, and so is one whose
    /// character is alive but already on no hit points. That second case is how
    /// the balance can move the player who has just died: the death event
    /// destroyed their character before <c>ServerBalanceTeams</c> was called.
    /// <para>
    /// Ties go to the <em>first</em> slot, because the comparison is a strict
    /// <c>&lt;</c> against a running minimum — the reference's own rule, unlike
    /// the Arena MVP list's, whose tie order is a <c>ds_priority</c> detail.
    /// </para>
    /// </remarks>
    private static int LowestScoringDeadPlayer(in WorldState state, Team team)
    {
        var best = -1;
        var points = double.MaxValue;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasPlayer[slot] || state.Players[slot].Team != team)
                continue;

            var score = Scoring.Get(state, slot, StatIndex.Points);
            if (score >= points)
                continue;

            if (state.IsAlive(slot) && !Gml.LessOrEqual(state.Characters[slot].Hp, 0))
                continue;

            points = score;
            best = slot;
        }

        return best;
    }

    /// <summary>The same pick with no liveness test — Arena's.</summary>
    private static int LowestScoringPlayer(in WorldState state, Team team)
    {
        var best = -1;
        var points = double.MaxValue;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasPlayer[slot] || state.Players[slot].Team != team)
                continue;

            var score = Scoring.Get(state, slot, StatIndex.Points);
            if (score >= points)
                continue;

            points = score;
            best = slot;
        }

        return best;
    }
}

/// <summary>
/// <c>Scripts/Classlimits/checkClasslimits.gml</c>: an acceptable class for a
/// player joining a team.
/// </summary>
/// <remarks>
/// Called on a team change and on both balance paths. With the default limit of
/// 255 it always returns the class it was given, which is why the port could go
/// six phases without it.
/// </remarks>
public static class Classlimits
{
    /// <summary>
    /// The desired class if the team has room, otherwise a random one that does.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Quote is excluded from the random pick and then offered as a last
    /// resort.</b> The loop runs <c>class &lt; 9</c> and the reference's comment
    /// says why — "Quote shouldn't be randomly assigned" — but the line below
    /// the loop hands it out anyway when nothing else is free.
    /// <para>
    /// ⚠️ <b>And if even Quote is full it returns the desired class regardless</b>,
    /// over the limit. A full server does not refuse a class; it just stops
    /// enforcing.
    /// </para>
    /// <para>
    /// ⚠️ <b>The random pick is a draw from the shared stream</b>
    /// (<c>irandom(ds_list_size - 1)</c>), so it advances the seed every client
    /// re-derives weapon spreads from. Which class comes out is free —
    /// docs/SIMULATION.md's "server-side RNG is free" covers it, because the
    /// answer is broadcast — but the draw itself is not optional.
    /// </para>
    /// </remarks>
    public static PlayerClass Pick(ref WorldState state, int slot, Team team, PlayerClass desired)
    {
        if (CountExcept(state, slot, team, desired) < LimitFor(state, desired))
            return desired;

        // "Make a list of all available classes except quote" — nine, not ten.
        Span<PlayerClass> free = stackalloc PlayerClass[TeamBalance.ClassCount];
        var count = 0;

        for (var i = 0; i < TeamBalance.ClassCount - 1; i++)
        {
            var candidate = (PlayerClass)i;

            if (CountExcept(state, slot, team, candidate) < LimitFor(state, candidate))
                free[count++] = candidate;
        }

        // `irandom(ds_list_size - 1)` is inclusive at both ends, which is
        // NextInclusive and not Next.
        if (count > 0)
            return free[state.Random.NextInclusive(count - 1)];

        if (CountExcept(state, slot, team, PlayerClass.Quote) < LimitFor(state, PlayerClass.Quote))
            return PlayerClass.Quote;

        // "Not enough slots, just use whatever was requested."
        return desired;
    }

    /// <summary>
    /// <c>countClassmembersExcept</c>: how many of a team already have this
    /// class, not counting the player asking.
    /// </summary>
    private static int CountExcept(in WorldState state, int slot, Team team, PlayerClass playerClass)
    {
        var count = 0;

        for (var i = 0; i < WorldState.MaxSlots; i++)
        {
            if (i == slot || !state.HasPlayer[i])
                continue;

            if (state.Players[i].Team == team && state.Players[i].Class == playerClass)
                count++;
        }

        return count;
    }

    private static int LimitFor(in WorldState state, PlayerClass playerClass)
    {
        var index = (int)playerClass;
        return (uint)index < TeamBalance.ClassCount ? state.ClassLimits[index] : 0;
    }
}

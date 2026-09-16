using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// <c>botFindAlly</c>, and the two things it unblocks: a medic that heals rather
/// than shoots, and a medic that follows somebody.
/// </summary>
/// <remarks>
/// <b>One script, two callers, and they pass different arguments.</b> The
/// combat half asks with <c>skipFollowers</c> false — a medic beaming another
/// medic is correct play — and the objective half asks with it true, because two
/// medics who pick each other as a <em>destination</em> deadlock. A port that made
/// the exclusion a rule inside the search rather than an argument would break the
/// first to fix the second.
/// </remarks>
public sealed class BotAllyTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>The measured wide floor with a clear sightline — see <c>ScriptedBotTests</c>.</summary>
    private const double StandX = 3570;

    private const double StandY = 325;

    private static ScenarioWorld Load() =>
        ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

    private static WorldView View(ScenarioWorld world) => new(in world.State, world.Collision);

    // ------------------------------------------------------------------
    // The search
    // ------------------------------------------------------------------

    /// <summary>
    /// The hurt come first, and between two equally hurt the tougher one wins.
    /// </summary>
    /// <remarks>
    /// <c>score = 1000 * hp/maxHp + distance - maxHp</c>, and every term is there
    /// for a reason. <b>Anyone actually wounded outscores anyone who is not by
    /// far more than distance can make up</b> — 1,000 points across the health
    /// range against a few hundred pixels of map. And the <c>- maxHp</c> term is
    /// what makes a heavy at half health beat a scout at half health: a medic's job
    /// is to keep the biggest body in the fight alive, not to top up whoever is
    /// closest.
    /// </remarks>
    [Fact]
    public void TheHurtComeFirstAndTheTougherOfTwoEqualsWins()
    {
        var world = Load();
        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);

        // A healthy scout right beside the medic, and a wounded heavy further off.
        var scout = world.Place(PlayerClass.Scout, Team.Red, StandX + 40, StandY);
        var heavy = world.Place(PlayerClass.Heavy, Team.Red, StandX + 300, StandY);

        world.State.Characters[heavy].Hp = 60;

        Assert.Equal(heavy, BotAlly.Find(View(world), medic, 700, skipFollowers: false));

        // The control: heal the heavy up and the near, healthy scout wins on
        // distance. Without it this passes on a search that simply prefers heavies.
        world.State.Characters[heavy].Hp = ClassStats.For(PlayerClass.Heavy).MaxHp;

        Assert.Equal(scout, BotAlly.Find(View(world), medic, 700, skipFollowers: false));

        // And the toughness term: two classes at the SAME fraction of health,
        // the scout nearer. The heavy still wins, by exactly the difference in what
        // it can absorb.
        var fair = Load();
        var fairMedic = fair.Place(PlayerClass.Medic, Team.Red, StandX, StandY);
        var fairScout = fair.Place(PlayerClass.Scout, Team.Red, StandX + 40, StandY);
        var fairHeavy = fair.Place(PlayerClass.Heavy, Team.Red, StandX + 100, StandY);

        fair.State.Characters[fairScout].Hp = ClassStats.For(PlayerClass.Scout).MaxHp / 2.0;
        fair.State.Characters[fairHeavy].Hp = ClassStats.For(PlayerClass.Heavy).MaxHp / 2.0;

        Assert.Equal(fairHeavy, BotAlly.Find(View(fair), fairMedic, 700, skipFollowers: false));
        output.WriteLine($"tie on health fraction went to the heavy in slot {fairHeavy}, not the nearer scout");
    }

    /// <summary>
    /// Anyone hurt outranks anyone at full health, however tough or near.
    /// </summary>
    /// <remarks>
    /// <b>A partition, and the <c>- maxHp</c> term is why it has to be one.</b>
    /// That term is worth 200 points across the class table while the health term
    /// is worth 1,000 across a whole bar, so on the plain score a full-health
    /// heavy (1000 - 300) beat a scout anywhere above 82.5% (1000h - 125): a medic
    /// stood topping up an untouched heavy while a team-mate bled beside it. The
    /// fixture puts the heavy NEARER as well, so nothing but the partition can
    /// produce this answer.
    /// <para>
    /// ⛔ The full-health ally is still a candidate and the second half says so.
    /// Beaming one charges the uber at 1.75 a tick (2.5 during setup), so "never
    /// instead of somebody hurt" is the rule and "never" is not.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnyoneHurtOutranksAnyoneAtFullHealth()
    {
        var world = Load();
        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);

        // The heavy is nearer AND tougher AND at full health; the scout is barely
        // scratched and further away. Every term in the plain score favours the
        // heavy and the partition still has to pick the scout.
        var heavy = world.Place(PlayerClass.Heavy, Team.Red, StandX + 20, StandY);
        var scout = world.Place(PlayerClass.Scout, Team.Red, StandX + 260, StandY);

        var scoutMax = ClassStats.For(PlayerClass.Scout).MaxHp;
        world.State.Characters[scout].Hp = scoutMax - 1;

        Assert.Equal(scout, BotAlly.Find(View(world), medic, 700, skipFollowers: false));

        // The control: heal that last point off and the heavy wins on everything
        // it was already winning on. Without it this passes on a search that has
        // simply stopped choosing heavies.
        world.State.Characters[scout].Hp = scoutMax;

        Assert.Equal(heavy, BotAlly.Find(View(world), medic, 700, skipFollowers: false));
    }

    /// <summary>
    /// A person is preferred to a bot at comparable health, and not to one that is
    /// dying.
    /// </summary>
    /// <remarks>
    /// <b>A bonus rather than a second partition</b>, worth 30% of a health bar
    /// against the score's 1,000 across a whole one. Both halves matter: a medic
    /// that always picked the person would let a bot bleed out beside it, and one
    /// that never did would spend a live server's heals on its own team-mates.
    /// <para>
    /// ⚠️ <c>PlayerState.IsBot</c> defaults to FALSE MEANING HUMAN and
    /// <c>ScenarioWorld</c> sets it for nobody, so this fixture has to mark the
    /// bot explicitly. That default is also why the term does nothing in a
    /// scenario or a headless match, where every candidate is the same kind — it
    /// discriminates only on a live server, which is the only place it is for.
    /// </para>
    /// </remarks>
    [Fact]
    public void APersonIsPreferredToABotAtComparableHealth()
    {
        var world = Load();
        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);

        // Two scouts, equally hurt, the bot nearer.
        var bot = world.Place(PlayerClass.Scout, Team.Red, StandX + 40, StandY);
        var person = world.Place(PlayerClass.Scout, Team.Red, StandX + 200, StandY);

        var max = ClassStats.For(PlayerClass.Scout).MaxHp;

        world.State.Characters[bot].Hp = max * 0.5;
        world.State.Characters[person].Hp = max * 0.5;
        world.State.Players[bot].IsBot = true;

        Assert.Equal(person, BotAlly.Find(View(world), medic, 700, skipFollowers: false));

        // The control, and the direction that must NOT move: the bot is now dying
        // and the person has a scratch. 300 points is 30% of a bar, so a gap this
        // wide is well past what it can bridge.
        world.State.Characters[bot].Hp = max * 0.1;
        world.State.Characters[person].Hp = max * 0.9;

        Assert.Equal(bot, BotAlly.Find(View(world), medic, 700, skipFollowers: false));
    }

    /// <summary>
    /// A cloaked team-mate is never the patient, and stops being one mid-beam.
    /// </summary>
    /// <remarks>
    /// <b>The reason is on the CLIENT, not in the heal.</b>
    /// <c>Medigun.events/Draw.xml</c> draws the beam as a plain
    /// <c>draw_line_width_color</c> straight to the patient's x/y, gated on
    /// nothing but the medic's own taunt — not on the patient's cloak and not on
    /// <c>invisible</c>. So beaming a cloaked spy paints a five-pixel line at
    /// whatever it is creeping up on. The heal itself works perfectly
    /// (<c>Healing</c> has no cloak test either), which is exactly why nothing
    /// else in the layer stops this.
    /// <para>
    /// <see cref="BotAlly.StillValid"/> is asked separately because
    /// <see cref="BotAlly.Find"/> runs on a cadence: a spy that cloaks mid-beam
    /// has to lose it on that tick, not up to <c>BOT_TARGET_PERIOD</c> later.
    /// </para>
    /// </remarks>
    [Fact]
    public void ACloakedTeamMateIsNeverThePatient()
    {
        var world = Load();
        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);

        // The spy is the better patient by every term: hurt, and nearer than the
        // healthy scout behind it.
        var spy = world.Place(PlayerClass.Spy, Team.Red, StandX + 40, StandY);
        var scout = world.Place(PlayerClass.Scout, Team.Red, StandX + 200, StandY);

        world.State.Characters[spy].Hp = ClassStats.For(PlayerClass.Spy).MaxHp / 2.0;

        Assert.Equal(spy, BotAlly.Find(View(world), medic, 700, skipFollowers: false));
        Assert.True(BotAlly.StillValid(View(world), medic, spy));

        // The cloak goes up and both answers change on the same tick — the search
        // falls through to the scout rather than to nobody, and the beam already
        // on the spy drops immediately.
        world.State.Characters[spy].Cloak = true;

        Assert.Equal(scout, BotAlly.Find(View(world), medic, 700, skipFollowers: false));
        Assert.False(BotAlly.StillValid(View(world), medic, spy));

        // ⚠️ The cloak and not the fade: CloakAlpha is still 1 here, so a rule
        // written against the alpha would have let the beam sit on the spy for the
        // twenty ticks it takes to disappear — which is the whole approach.
        Assert.Equal(1, world.State.Characters[spy].CloakAlpha);
    }

    /// <summary>
    /// An enemy, a corpse and somebody out of range are all not allies.
    /// </summary>
    [Fact]
    public void OnlyLiveTeamMatesInRangeAreCandidates()
    {
        var world = Load();
        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);

        world.Place(PlayerClass.Heavy, Team.Blue, StandX + 40, StandY);

        Assert.Equal(BotAlly.None, BotAlly.Find(View(world), medic, 700, skipFollowers: false));

        // A team-mate beyond the range is not one either.
        var far = Load();
        var farMedic = far.Place(PlayerClass.Medic, Team.Red, StandX, StandY);
        far.Place(PlayerClass.Heavy, Team.Red, StandX + 300, StandY);

        Assert.Equal(BotAlly.None, BotAlly.Find(View(far), farMedic, 100, skipFollowers: false));
        Assert.NotEqual(BotAlly.None, BotAlly.Find(View(far), farMedic, 700, skipFollowers: false));
    }

    /// <summary>
    /// The sightline is the BEAM's, not a bullet's.
    /// </summary>
    /// <remarks>
    /// <c>collision_line</c> against <c>Obstacle</c>, and <b>not</b>
    /// <c>collision_line_bulletblocking</c>. A heal beam is stopped by walls the
    /// same way a bullet is, but the two predicates are not the same object set —
    /// gates, player walls and bullet walls are separate objects, so a medic can
    /// beam a team-mate through their own gate — and the beam's is the one that
    /// decides whether the heal actually connects. <c>Healing</c> uses the same
    /// predicate for the real thing.
    /// <para>
    /// <b>Filtering during the search rather than rejecting the winner
    /// afterwards</b> is what lets the second-best candidate win: with the best
    /// candidate behind a wall, a worse one in the open is still picked instead of
    /// the search giving up.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABlockedTeamMateIsSkippedAndTheNextOneWins()
    {
        var world = Load();
        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);

        // A wounded heavy underground, with the floor between it and the medic —
        // 80px below is inside the map's geometry here (ScriptedBotTests measured
        // that the vertical line from this floor is blocked).
        var buried = world.Place(PlayerClass.Heavy, Team.Red, StandX, StandY + 400);
        world.State.Characters[buried].Hp = 40;

        Assert.True(
            world.Collision.CollisionLineHitsMask(StandX, StandY, StandX, StandY + 400),
            "the fixture's blocked line is not blocked");

        var healthy = world.Place(PlayerClass.Scout, Team.Red, StandX + 60, StandY);

        // The wounded heavy scores far better and is skipped for the sightline, so
        // the healthy scout in the open wins.
        Assert.Equal(healthy, BotAlly.Find(View(world), medic, 900, skipFollowers: false));

        // The control: with no second candidate the search finds NOBODY rather
        // than returning the blocked one.
        var alone = Load();
        var aloneMedic = alone.Place(PlayerClass.Medic, Team.Red, StandX, StandY);
        var aloneBuried = alone.Place(PlayerClass.Heavy, Team.Red, StandX, StandY + 400);

        alone.State.Characters[aloneBuried].Hp = 40;

        Assert.Equal(BotAlly.None, BotAlly.Find(View(alone), aloneMedic, 900, skipFollowers: false));
    }

    /// <summary>
    /// <c>skipFollowers</c> drops a medic as a destination and keeps it as a patient.
    /// </summary>
    /// <remarks>
    /// <b>Two medics who pick each other deadlock.</b> The follow goal is the
    /// ally's own position, so medic A's goal is where medic B is standing and B's
    /// is where A is standing: both are already there, both report arrived, and
    /// neither ever leaves spawn. Seen live on koth_valley — two red medics parked
    /// at x=460 with the rest of their team at the point.
    /// <para>
    /// <b>It is an ARGUMENT rather than a rule inside the search</b>, because the
    /// <em>beam</em> has no such problem: a medic healing another medic is correct
    /// play. Making it a rule would break healing to fix following.
    /// </para>
    /// <para>
    /// <b>Filtering during the search lets the second-best win.</b> A medic
    /// beside a wounded medic and a healthy heavy still follows the heavy, instead
    /// of giving up on following at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void SkipFollowersDropsAMedicAsADestinationAndKeepsItAsAPatient()
    {
        var world = Load();
        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);

        var wounded = world.Place(PlayerClass.Medic, Team.Red, StandX + 40, StandY);
        var heavy = world.Place(PlayerClass.Heavy, Team.Red, StandX + 200, StandY);

        world.State.Characters[wounded].Hp = 30;

        // The beam takes the wounded medic — that is correct play.
        Assert.Equal(wounded, BotAlly.Find(View(world), medic, 700, skipFollowers: false));

        // The follow goal takes the heavy instead. Not "nobody": the exclusion
        // happens during the search, so the second-best candidate still wins.
        Assert.Equal(heavy, BotAlly.Find(View(world), medic, 700, skipFollowers: true));
    }

    // ------------------------------------------------------------------
    // The beam
    // ------------------------------------------------------------------

    /// <summary>
    /// A medic with a patient beams instead of shooting.
    /// </summary>
    /// <remarks>
    /// The needle gun is on the RIGHT click — the Medigun's left is the heal beam
    /// — so a medic with nobody to heal presses SPECIAL to shoot and one with a
    /// patient presses ATTACK. Those are two different keys, so the switch is
    /// visible from outside without reaching into the bot.
    /// </remarks>
    [Fact]
    public void AMedicWithAPatientBeamsInsteadOfShooting()
    {
        var world = Load();
        var bot = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1)
        {
            AliveLast = true,
        };

        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);
        world.Drive(medic, bot);

        // An enemy in range, so the medic HAS something to shoot at.
        world.Place(PlayerClass.Heavy, Team.Blue, StandX + 250, StandY);

        world.Step(120);

        Assert.True(bot.Combat.Target.Exists, "the medic never saw the enemy");
        Assert.Equal(BotAlly.None, bot.Combat.Ally);
        Assert.True(
            (bot.Combat.AttackKeys & PlayerKeys.Special) != 0,
            "a medic with no patient did not shoot needles");

        // Now give it somebody to heal.
        var patient = world.Place(PlayerClass.Soldier, Team.Red, StandX + 40, StandY);
        world.State.Characters[patient].Hp = 40;

        world.Step(BotConstants.TargetPeriod * 2);

        Assert.Equal(patient, bot.Combat.Ally);
        Assert.True((bot.Combat.AttackKeys & PlayerKeys.Attack) != 0, "the beam is not on");
        Assert.Equal(PlayerKeys.None, bot.Combat.AttackKeys & PlayerKeys.Special);
    }

    /// <summary>
    /// A beam is not gated by any of the difficulty clocks.
    /// </summary>
    /// <remarks>
    /// Every gate on the shooting path — the acquire hold, the fire delay, the
    /// aim settle, the hold-fire roll — is about shooting a person who is trying not
    /// to be shot. <b>A medic that made its own team-mate wait a second and a half
    /// before pointing the beam at them would read as broken rather than as bad</b>,
    /// so all four are bypassed.
    /// <para>
    /// The observable is the tier: a tier-1 bot has the longest acquire hold in the
    /// game, and it beams on the first tick it has a patient anyway.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABeamIsNotGatedByTheDifficultyClocks()
    {
        var world = Load();

        // The WORST tier, which is the point: its acquire hold and fire delay are
        // the longest in the game.
        var bot = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(1), stagger: 0)
        {
            AliveLast = true,
        };

        Assert.True(bot.Skill.AcquireTicks > 0, "tier 1 has no acquire hold, so this asserts nothing");

        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);
        var patient = world.Place(PlayerClass.Soldier, Team.Red, StandX + 40, StandY);

        world.State.Characters[patient].Hp = 40;
        world.Drive(medic, bot);

        // One target period, so the ally search has run once — and no more.
        world.Step(BotConstants.TargetPeriod + 1);

        Assert.Equal(patient, bot.Combat.Ally);
        Assert.True(
            (bot.Combat.AttackKeys & PlayerKeys.Attack) != 0,
            $"the beam waited: acquire {bot.Skill.AcquireTicks}, fire delay {bot.Skill.FireDelayTicks}");

        // And the aim is straight at them with NO lead. The beam has no travel
        // time, and leading a team-mate points it at empty air beside them.
        var straight = Gml.PointDirection(StandX, StandY, StandX + 40, StandY);

        Assert.Equal(straight, bot.Combat.AimWant, 3);
    }

    // ------------------------------------------------------------------
    // The follow goal
    // ------------------------------------------------------------------

    /// <summary>
    /// A medic's goal is its ally, on the far side of them from the enemy.
    /// </summary>
    /// <remarks>
    /// <b>It is decided BEFORE the whole mode cascade and returns early</b>,
    /// because a medic following a team-mate is doing its job in every mode and the
    /// objective is that team-mate's problem.
    /// <para>
    /// <b>Horizontal only.</b> The goal is a place to STAND, and standing is a
    /// one-dimensional choice on a given surface.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMedicFollowsItsAllyOnTheFarSideFromTheEnemy()
    {
        var world = Load();
        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
        var objective = new BotObjective(graph, new SpawnPoints(world.Map));

        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);
        var ally = world.Place(PlayerClass.Heavy, Team.Red, StandX + 60, StandY);

        // ScenarioWorld places no objective, so the mode cascade has nothing to
        // offer — which is what makes the follow goal's existence visible. Shown
        // with a NON-medic in the same world: the follow goal fires with or without
        // an enemy, so asking the medic with no target proves nothing about the
        // cascade.
        var soldier = world.Place(PlayerClass.Soldier, Team.Red, StandX - 40, StandY);

        // CHECKED BEFORE THE ENEMY EXISTS: a world with no gamemode is a
        // DEATHMATCH — the cascade's last branch hunts the nearest enemy — so
        // "the cascade offers nothing" is only true when there is nobody to shoot.
        Assert.False(
            objective.Decide(View(world), soldier, BotRole.Attack, 0, allowSpot: true).Found,
            "the cascade found something, so the follow goal is not what is under test");

        var enemy = world.Place(PlayerClass.Soldier, Team.Blue, StandX + 400, StandY);

        // And the other half of the same point: now that there IS an enemy, the
        // cascade has an answer for the soldier. So the medic's goal below is the
        // follow branch's precisely because it returns BEFORE the cascade runs, not
        // because the cascade is empty.
        Assert.True(
            objective.Decide(View(world), soldier, BotRole.Attack, 0, allowSpot: true).Found,
            "a world with an enemy in it should now give a non-follower a deathmatch goal");

        var goal = objective.Decide(View(world), medic, BotRole.Attack, 0, allowSpot: true, target: enemy);

        Assert.True(goal.Found);
        Assert.Equal(world.State.Characters[ally].Y, goal.Y);

        // The enemy is to the RIGHT of the ally, so the medic stands to its LEFT.
        Assert.Equal(world.State.Characters[ally].X - BotConstants.MedicOffset, goal.X);

        // The control: put the enemy on the other side and the medic swaps sides.
        // Without it this passes on an offset with a fixed sign.
        world.State.Characters[enemy].X = StandX - 400;

        var swapped = objective.Decide(View(world), medic, BotRole.Attack, 0, allowSpot: true, target: enemy);

        Assert.Equal(world.State.Characters[ally].X + BotConstants.MedicOffset, swapped.X);

        // And with no enemy at all the offset comes off entirely — there is no
        // side to be on.
        var quiet = objective.Decide(View(world), medic, BotRole.Attack, 0, allowSpot: true);
        Assert.Equal(world.State.Characters[ally].X, quiet.X);
    }

    /// <summary>
    /// A driven medic really is told where the enemy is.
    /// </summary>
    /// <remarks>
    /// <c>AMedicFollowsItsAllyOnTheFarSideFromTheEnemy</c> calls the
    /// objective layer directly and hands it a target, so it gates the RULE and not
    /// the WIRING. Stop <c>ScriptedBot</c> passing its combat target and the medic
    /// stands on top of its ally instead of to one side, and nothing else notices —
    /// checked by mutation.
    /// <para>
    /// The target is passed rather than looked up again because the combat half has
    /// already re-validated it this tick, and a second lookup could disagree with the
    /// one the bot is actually aiming at.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADrivenMedicIsToldWhereTheEnemyIs()
    {
        var world = Load();
        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1)
        {
            Objective = new BotObjective(graph, new SpawnPoints(world.Map)),
            AliveLast = true,
        };

        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);
        var ally = world.Place(PlayerClass.Heavy, Team.Red, StandX + 60, StandY);

        world.Drive(medic, bot);

        // An enemy to the RIGHT of the ally and INSIDE a medic's attention
        // radius, which is shorter than most classes' — at 400px it saw nothing and
        // the wiring under test was never exercised.
        world.Place(PlayerClass.Soldier, Team.Blue, StandX + 200, StandY);

        world.Step(BotConstants.ObjectivePeriod * 4);

        Assert.True(bot.Combat.Target.Exists, "the medic never saw the enemy, so nothing was passed on");
        // NOT `HasGoal`. The follow position is 60px from the ally and the medic
        // is standing right there, so it arrives on the tick it is issued — and
        // arrival clears the goal and frees the path together. `GoalX` is what
        // survives, and it is what the claim is about.
        Assert.NotEqual(0, bot.Path.GoalX);

        var allyX = world.State.Characters[ally].X;

        output.WriteLine($"ally at {allyX:0}, goal at {bot.Path.GoalX:0}");

        // The SIGN, not the exact offset. TryResolve snaps the goal to a
        // node's own column, so the number moves by up to a cell — but which side of
        // the ally it lands on is the whole claim.
        Assert.True(
            bot.Path.GoalX < allyX,
            $"the medic's goal is at {bot.Path.GoalX:0}, not left of its ally at {allyX:0}");
    }

    /// <summary>
    /// A medic carrying the intel does not follow anybody.
    /// </summary>
    /// <remarks>
    /// There is exactly one thing to do with an enemy flag and it is not "escort
    /// somebody else while holding it".
    /// </remarks>
    [Fact]
    public void AMedicCarryingTheIntelDoesNotFollow()
    {
        var world = Load();
        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
        var objective = new BotObjective(graph, new SpawnPoints(world.Map));

        var medic = world.Place(PlayerClass.Medic, Team.Red, StandX, StandY);
        world.Place(PlayerClass.Heavy, Team.Red, StandX + 60, StandY);

        Assert.True(objective.Decide(View(world), medic, BotRole.Attack, 0, allowSpot: true).Found);

        world.State.Characters[medic].CarryingIntel = true;

        // With no objective placed in a scenario world there is nothing else to
        // find, so the follow goal going away is the whole answer.
        Assert.False(objective.Decide(View(world), medic, BotRole.Attack, 0, allowSpot: true).Found);
    }
}

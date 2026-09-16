using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// A whole match tick loop, a real bot, and a spy that cloaks, walks in and
/// stabs it.
/// </summary>
/// <remarks>
/// <b><c>BotTargetingTests</c> proves the predicate; this proves the bot.</b>
/// Everything between a candidate and a corpse — the combat layer's
/// keep-or-drop, the aim solve, the class's fire gate, the weapon, the damage
/// path — sits between <see cref="BotVisibility.CanSee"/> and the behaviour that
/// was asked for, and a green predicate says nothing about any of it. So this
/// drives a <see cref="ScriptedBot"/> through <see cref="ScenarioWorld"/> against
/// a spy pressing real keys, and asks who is alive at the end.
/// <para>
/// <b>The two cases are the same fixture mirrored, and the second is the one
/// that must not move.</b> A spy stabbing in front of a bot is the one the
/// animation gives away; the same spy behind it is the backstab, and a fix that
/// let a bot answer both would have taken the class's whole mechanic away — and
/// would look exactly like the first test passing on its own.
/// </para>
/// <para>
/// ⚠️ <b>The approach has to be a walk, not a placement.</b> A spy that starts
/// uncloaked inside the bot's minimum engagement band is a target for the two
/// ticks before it cloaks, and the bot backs away from it — which leaves the bot
/// aiming, and so facing, in the one direction that makes every later answer
/// "behind". That fixture measures what the back-away did, not what the
/// visibility rule did.
/// </para>
/// </remarks>
public sealed class BotVsSpyTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>The measured wide floor with a clear sightline — see <c>ScriptedBotTests</c>.</summary>
    private const double StandX = 3570;

    private const double StandY = 325;

    /// <summary>
    /// Far enough out that the spy is cloaked long before it is near enough to
    /// push the bot anywhere — outside the heavy's minimum band, and most of a
    /// cloak fade away at a spy's run power.
    /// </summary>
    private const double ApproachFrom = 220;

    /// <summary>
    /// A spy that cloaks, walks to knife range, and holds ATTACK.
    /// </summary>
    /// <remarks>
    /// <b>It presses keys and nothing else.</b> The cloak is
    /// <see cref="PlayerKeys.Special"/> and the stab is
    /// <see cref="PlayerKeys.Attack"/> off the same button a shot uses — the
    /// Revolver's <c>User Event 1</c> is an if/else on the cloak — so a source
    /// that wrote <c>Cloak = true</c> by hand would be testing a state the game
    /// cannot reach. The SPECIAL press is one tick and not a hold, because
    /// <c>CloakStep</c> reads <c>pressedKeys</c> and a held right click refuses
    /// the stab outright.
    /// </remarks>
    private sealed class StabbingSpy(int victim) : IInputSource
    {
        /// <summary>
        /// Where the walk stops. Inside <see cref="BotStab"/>'s box — 37px the
        /// way the spy faces — with enough margin that the slide below cannot
        /// carry the spy out the other side of its victim.
        /// </summary>
        private const double StopGap = 40;

        public int CloakedAt { get; private set; } = -1;

        public int StabbedAt { get; private set; } = -1;

        /// <summary>Which side of the victim the spy committed the knife from.</summary>
        public bool StabbedFromTheRight { get; private set; }

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            ref readonly var self = ref world.Character(slot);
            ref readonly var target = ref world.Character(victim);

            // Always looking at the victim: it is what the knife's direction is
            // taken from, and what the spy's own facing follows.
            var aim = (ushort)(Gml.PointDirection(self.X, self.Y, target.X, target.Y)
                               * 65536.0 / 360.0);

            if (!self.Cloak && CloakedAt < 0)
            {
                CloakedAt = tick;
                return new PlayerInput(PlayerKeys.Special, aim, 0);
            }

            // A cloak fades at 0.05 a tick, so the walk happens behind it rather
            // than in front of it.
            if (!Gml.Equal(self.CloakAlpha, 0))
                return new PlayerInput(PlayerKeys.None, aim, 0);

            var gap = target.X - self.X;

            if (Math.Abs(gap) > StopGap)
                return new PlayerInput(gap > 0 ? PlayerKeys.Right : PlayerKeys.Left, aim, 0);

            // ⚠️ Stand still BEFORE committing, and stop the walk well short.
            // The stab sets runPower to 0, which stops the spy accelerating and
            // does nothing about the hspeed it already has — so a spy that walks
            // to touching distance and presses ATTACK slides on past its victim
            // and finishes the knife from the far side of it. Which side the spy
            // is on is the whole of what this fixture varies, so that slide
            // quietly swapped the two arms over and both of them still "passed"
            // something.
            if (!Gml.Equal(self.HSpeed, 0))
                return new PlayerInput(PlayerKeys.None, aim, 0);

            if (StabbedAt < 0)
            {
                StabbedAt = tick;
                StabbedFromTheRight = self.X > target.X;
            }

            return new PlayerInput(PlayerKeys.Attack, aim, 0);
        }
    }

    private static ScenarioWorld Load() =>
        ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

    /// <summary>
    /// Runs the fixture with the spy approaching from one side, and reports what
    /// happened to each of them.
    /// </summary>
    /// <param name="fromTheRight">
    /// Which side the spy comes from. <see cref="WorldState.Spawn"/> leaves a
    /// character aiming right and a bot with nothing to shoot at and nowhere to
    /// be holds that, so "from the right" is "the bot is facing it" — which the
    /// fixture reports rather than assumes.
    /// </param>
    private (bool SpyHurt, bool BotDead) Duel(bool fromTheRight)
    {
        var world = Load();

        // A heavy: 300hp, and a minigun that needs no arc — so what this measures
        // is whether the bot SHOOTS, not whether it can solve a lob.
        var botSlot = world.Place(PlayerClass.Heavy, Team.Red, StandX, StandY);

        var spyX = StandX + (fromTheRight ? ApproachFrom : -ApproachFrom);
        var spySlot = world.Place(PlayerClass.Spy, Team.Blue, spyX, StandY);

        // No nav graph: the bot has nowhere to be, so it stands where it is put
        // and its aim is decided by what it can see and nothing else. That is
        // what leaves the two arms differing in exactly one thing.
        var bot = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 0);
        var spy = new StabbingSpy(botSlot);

        world.Drive(botSlot, bot);
        world.Drive(spySlot, spy);

        var spyMaxHp = ClassStats.For(PlayerClass.Spy).MaxHp;

        // Long enough for the cloak, the walk, the 32-tick wind-up and the
        // knife's own six ticks.
        for (var i = 0; i < 300; i++)
        {
            world.Step();

            if (!world.State.Alive[botSlot] || !world.State.Alive[spySlot])
                break;
        }

        ref readonly var spyEnd = ref world.State.Characters[spySlot];
        ref readonly var botEnd = ref world.State.Characters[botSlot];

        output.WriteLine(
            $"spy from the {(fromTheRight ? "right" : "left")}: "
            + $"cloakedAt={spy.CloakedAt} stabbedAt={spy.StabbedAt} "
            + $"stabbedFromTheRight={spy.StabbedFromTheRight} "
            + $"botFacing={botEnd.Facing} botX={botEnd.X:F0} spyX={spyEnd.X:F0} "
            + $"spyHp={spyEnd.Hp}/{spyMaxHp} spyAlive={world.State.Alive[spySlot]} "
            + $"botHp={botEnd.Hp} botAlive={world.State.Alive[botSlot]}");

        // The fixture is only meaningful if the spy really did cloak and really
        // did commit to a knife. A spy that fired a revolver instead, or never
        // got the cloak, produces "the bot shot it" for the wrong reason.
        Assert.True(spy.CloakedAt >= 0, "the spy never cloaked");
        Assert.True(spy.StabbedAt >= 0, "the spy never reached knife range");
        Assert.Equal(fromTheRight, spy.StabbedFromTheRight);

        return (
            !world.State.Alive[spySlot] || spyEnd.Hp < spyMaxHp,
            !world.State.Alive[botSlot] || botEnd.Hp <= 0);
    }

    /// <summary>
    /// A spy stabbing in front of a bot gets shot, and the bot lives.
    /// </summary>
    /// <remarks>
    /// The knife's hitbox is not armed until tick 32 of a
    /// <see cref="Weapons.StabFreezeTicks"/> freeze the spy cannot move out of,
    /// so a bot that can see the animation has about a second against a
    /// stationary 100hp target.
    /// </remarks>
    [Fact]
    public void ABotShootsASpyStabbingInFrontOfIt()
    {
        var (spyHurt, botDead) = Duel(fromTheRight: true);

        Assert.True(spyHurt, "the bot never fired at a spy it could see stabbing");
        Assert.False(botDead, "the bot was stabbed anyway");
    }

    /// <summary>
    /// The same stab from behind still kills the bot, because that is the
    /// backstab.
    /// </summary>
    /// <remarks>
    /// <b>The direction that must not move.</b> The animation is hidden from
    /// exactly the player being stabbed — <c>StabAnim.events/Draw.xml</c> exits on
    /// <c>owner.invisible</c> — so a bot that answered this one would be reading a
    /// screen no player has.
    /// </remarks>
    [Fact]
    public void ASpyStabbingFromBehindStillKillsTheBot()
    {
        var (spyHurt, botDead) = Duel(fromTheRight: false);

        Assert.False(spyHurt, "the bot shot a spy it had its back to");
        Assert.True(botDead, "the backstab did not land");
    }
}

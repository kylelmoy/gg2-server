using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>A point in world coordinates.</summary>
public readonly record struct ScenarioPoint(double X, double Y);

/// <summary>
/// One enemy placed for a scenario to act on.
/// </summary>
/// <param name="Dummy">
/// Inert unless false: it never aims, never fires and never moves. Leave it
/// inert unless the scenario is specifically about a two-sided fight, because
/// every point of damage measured is then unambiguously the subject's doing.
/// In the reference these are ordinary knobs — an acquisition delay longer than
/// the scenario and a locked goal — so a training dummy needs no test-only code
/// in the game.
/// </param>
public readonly record struct ScenarioEnemy(PlayerClass Class, ScenarioPoint At, bool Dummy = true);

/// <summary>What a scenario is measuring.</summary>
/// <remarks>
/// <b>A scenario is one or the other, never both and never neither.</b> The
/// choice is required rather than defaulted because "a combat scenario that
/// quietly inherited a movement goal would measure the wrong thing and still
/// pass" — <c>bot-scenarios.js</c>'s own reasoning, and
/// <see cref="Scenario"/> enforces it in the constructor.
/// </remarks>
public enum ScenarioMode
{
    /// <summary>Walk somewhere, inside a tick budget.</summary>
    Travel,

    /// <summary>Stand where you were put, with no goal at all, and fight.</summary>
    Hold,
}

/// <summary>Combat and movement assertions over what a run measured.</summary>
/// <param name="MinDamage">Hp removed from the enemy team, at least.</param>
/// <param name="MaxDamage">Hp removed from the enemy team, at most.</param>
/// <param name="MaxTicks">The run took no longer than this.</param>
/// <param name="MaxMoved">
/// Pixels from where the subject was placed. In <see cref="ScenarioMode.Hold"/>
/// this does <b>not</b> test "did it walk to the objective" — the bot has no
/// goal, so it cannot path anywhere. It measures knockback and evasive hops, so
/// a splash class shelling something at close range drifts a lot and a tight
/// bound is flaky: 23, 42 and then 232 px were measured on three runs of one
/// scenario.
/// </param>
/// <param name="Acquired">
/// Did it ever pick a target at all. Latched while it happens, because a
/// target clears when it dies — by the end, a bot that fought and won looks
/// exactly like one that never saw anything.
/// </param>
public readonly record struct ScenarioExpectations(
    int? MinDamage = null,
    int? MaxDamage = null,
    int? MaxTicks = null,
    double? MaxMoved = null,
    bool? Acquired = null);

/// <summary>
/// One behaviour scenario, ported from <c>gg2-agent/tools/bot-scenarios.js</c>.
/// </summary>
/// <remarks>
/// <b>The <see cref="About"/> text is the valuable part and is carried across
/// verbatim in substance.</b> Each one records what a failure would mean, and
/// most record a measurement and the bug it was written for — which is the
/// difference between "the bot is broken" and "this leg was always marginal".
/// A scenario without that is a red test nobody can act on.
/// <para>
/// <b>Coordinates are snapped to nav nodes before use</b>
/// (<see cref="ScenarioWorld.Snap"/>), and the snapped position is what a run
/// places and reports. A raw objective coordinate usually does not resolve on
/// its own.
/// </para>
/// </remarks>
public sealed record Scenario
{
    /// <param name="to">Where to walk. Mutually exclusive with <paramref name="hold"/>.</param>
    /// <param name="hold">Ticks to stand still and fight. Mutually exclusive with <paramref name="to"/>.</param>
    public Scenario(
        string name,
        string map,
        string about,
        PlayerClass playerClass,
        Team team,
        ScenarioPoint from,
        ScenarioPoint? to = null,
        int? hold = null,
        int budget = 0,
        int area = 1,
        IReadOnlyList<ScenarioEnemy>? enemies = null,
        ScenarioExpectations expect = default,
        string? known = null)
    {
        if (to is null == hold is null)
        {
            throw new ArgumentException(
                $"scenario '{name}' must have exactly one of `to` and `hold`: "
                + "a combat scenario that inherited a movement goal would measure the "
                + "wrong thing and still pass");
        }

        Name = name;
        Map = map;
        Area = area;
        About = about;
        Class = playerClass;
        Team = team;
        From = from;
        To = to;
        Hold = hold ?? 0;
        Budget = budget;
        Enemies = enemies ?? [];
        Expect = expect;
        Known = known;
    }

    /// <summary>Unique, kebab-case. What you pass to run one scenario.</summary>
    public string Name { get; }

    public string Map { get; }

    /// <summary>Which stage of a multi-area map. Every ported scenario is area 1.</summary>
    public int Area { get; }

    /// <summary>One paragraph saying what a failure here would mean.</summary>
    public string About { get; }

    /// <summary>
    /// Movement is class-independent in the reference — <c>botPathKeys</c>
    /// has no class references at all — so a nav scenario uses the soldier
    /// unless it is specifically about a class. The two that are not
    /// (<c>gallery-pit-climb</c>, <c>atalia-ground-to-point</c>) are heavies on
    /// purpose: a heavy's top speed is the takeoff velocity the graph assumes.
    /// </summary>
    public PlayerClass Class { get; }

    /// <summary>Decides which gates the route may use.</summary>
    public Team Team { get; }

    public ScenarioPoint From { get; }

    /// <summary>Where to walk, in <see cref="ScenarioMode.Travel"/>.</summary>
    public ScenarioPoint? To { get; }

    /// <summary>Ticks to stand still, in <see cref="ScenarioMode.Hold"/>.</summary>
    public int Hold { get; }

    /// <summary>
    /// Ticks allowed, in <see cref="ScenarioMode.Travel"/>. A leg that takes
    /// longer is a failure even if it eventually arrives — "gets there in the
    /// end" is exactly what a bot that re-plans in a loop looks like from
    /// outside.
    /// </summary>
    public int Budget { get; }

    public IReadOnlyList<ScenarioEnemy> Enemies { get; }

    public ScenarioExpectations Expect { get; }

    /// <summary>
    /// Set when this reproduces a bug nobody has fixed. It reports rather
    /// than fails, so green keeps meaning "no <em>new</em> regressions" — and it
    /// says what has been ruled out, because the next person to read it is
    /// deciding whether to start there. If it starts passing, delete the field.
    /// </summary>
    public string? Known { get; }

    public ScenarioMode Mode => To is null ? ScenarioMode.Hold : ScenarioMode.Travel;

    /// <summary>How many ticks a run of this scenario may take.</summary>
    public int TickLimit => Mode == ScenarioMode.Hold ? Hold : Budget;

    public override string ToString() => Name;
}

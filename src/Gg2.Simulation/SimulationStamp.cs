using System.Globalization;
using System.Reflection;
using System.Text;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// A fingerprint of everything a trained policy is allowed to depend on —
/// <c>docs/BOTAI.md</c> item 9's <c>SIM_UUID</c>.
/// </summary>
/// <remarks>
/// ⚠️ <b>A policy trained against physics that has since changed does not error
/// and does not warn. It plays slightly worse for ever and the cause is
/// invisible.</b> GG2 has the idiom one layer up: the reference refuses a client
/// whose <c>PROTOCOL_UUID</c> does not match rather than letting it connect and
/// desync. <see cref="Require"/> is the same thing for a policy file — refuse, do
/// not warn.
/// <para>
/// ⚠️ <b>The surface is chosen, not swept.</b> What goes in is what a policy's
/// behaviour actually depends on: the physics constants, the <c>BOT_*</c> and
/// <c>NAV_*</c> blocks, the per-class and per-projectile tables that
/// <c>docs/SIMULATION.md</c> marks contract, and the three hand-bumped version
/// numbers below. What stays out is configuration and protocol — a port number or
/// a maximum name length moving must not invalidate every checkpoint, and
/// <c>GameConstants</c> is therefore deliberately absent.
/// </para>
/// <para>
/// ⚠️ <b>Every bump invalidates every checkpoint, and that is correct.</b> The
/// alternative is a checkpoint nobody can date.
/// </para>
/// </remarks>
public static class SimulationStamp
{
    /// <summary>
    /// The wire action's encoding. Bump when <see cref="PlayerInput"/>'s meaning
    /// changes — a new key bit, a different aim quantisation, a new head.
    /// </summary>
    /// <remarks>
    /// <b>v2, 2026-08-29</b>: <c>EnvAction</c> gains a sixth head,
    /// <c>Zoom</c> — the sniper's scope, as a LEVEL rather than a toggle. Until
    /// it, <b>no bot in this port could zoom at all</b>: <c>toggleZoom</c> is a
    /// client command rather than a key, and a bot's whole output was
    /// <see cref="PlayerInput"/>. Every bot sniper fired at the unscoped 35
    /// against a weapon that reaches 75, and a distilled policy would have
    /// inherited that permanently. See docs/TRAPS-BOTS.md and <c>BotZoomTests</c>.
    /// <para>
    /// ⚠️ <b>Done at this moment on purpose.</b> The bump is free until a
    /// checkpoint exists to invalidate, and <c>gg2-rl</c> held phase 2 — the
    /// first training run — for it. A sixth head added afterwards would
    /// invalidate every checkpoint before it and every number derived from them.
    /// </para>
    /// </remarks>
    public const int ActionEncodingVersion = 2;

    /// <summary>
    /// The observation layout.
    /// </summary>
    /// <remarks>
    /// <b>Was 0, meaning "undesigned"</b>, while <c>docs/BOTAI.md</c> item 6
    /// deliberately left the feature set open. ✅ **1 since 2026-08-28**: the v0
    /// layout is <c>Gg2.Env.ObservationWriter</c>, whose field order is the
    /// authority and whose own <c>LayoutVersion</c> this mirrors.
    /// <para>
    /// ⚠️ <b>This is mirrored rather than referenced.</b> <c>Gg2.Simulation</c>
    /// does not and must not depend on <c>Gg2.Env</c> — the simulation is the
    /// thing an env observes, not the other way round — so the two constants are
    /// held equal by a test instead of by the compiler.
    /// </para>
    /// <para>
    /// ⚠️ <b>Bumping this changes <see cref="Uuid"/>, and that is the point.</b>
    /// A checkpoint trained against one layout must not silently load against
    /// another. When it moves, the pinned digest in <c>SimulationStampTests</c>
    /// is <em>updated as the bump</em>, not fixed as a broken test.
    /// </para>
    /// </remarks>
    public const int ObservationLayoutVersion = 1;

    /// <summary>
    /// What the simulation <em>does</em>, as opposed to the constants it does it
    /// with. Bump when you knowingly change behaviour that a policy could have
    /// learned against.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Asked for by <c>gg2-rl</c> on 2026-08-30, and the case for it is a
    /// commit in this repo.</b> The rest of this surface is constants and two
    /// encodings, so <c>3bba987</c> changed a line of <c>Movement.cs</c> — control
    /// flow in the physics — and the stamp could not move. A trainer had no way
    /// to tell, and the failure that hides behind it is the one this whole type
    /// exists to prevent: a policy that plays slightly worse for ever with the
    /// cause invisible.
    /// <para>
    /// <b>What counts as a bump.</b> Anything that can move a trajectory, a
    /// damage number, a projectile path or a stage's position in
    /// <c>MatchSimulation</c>. ⚠️ <b>What does not: a refactor, a rename, a
    /// comment, a performance change — or a change demonstrated to be
    /// unobservable, and DEMONSTRATED is the word.</b> <c>3bba987</c> is exempt
    /// because two independent 90-iteration training runs reproduced their
    /// per-iteration numbers to the digit across 1.66M agent-steps, not because
    /// the argument for it was good. Reasoning is not checking; this project has
    /// the scars.
    /// </para>
    /// <para>
    /// ⚠️ <b>Deliberately hand-bumped, and deliberately not a sweep of the
    /// code.</b> Hashing the assembly would move on a comment, a refactor or a
    /// compiler upgrade, invalidate every checkpoint for nothing, and teach
    /// everyone to ignore the stamp. The surface is chosen — see this type's
    /// remarks — and a number somebody has to remember to bump is the honest
    /// price of that choice. It shares the failure mode of the two above it: it
    /// catches the change you knew you were making, not the one you did not.
    /// </para>
    /// <para>
    /// <b>1, 2026-08-30</b>: the behaviour this build has. It is a starting
    /// point rather than a claim that anything changed.
    /// </para>
    /// <para>
    /// ⚠️ <b>NOT bumped for moving platforms, 2026-09-01, and this is the second
    /// exemption on record.</b> <c>MovingPlatforms.EndStep</c> is a new stage in
    /// <see cref="MatchSimulation.Tick"/>, which the rule above would normally
    /// make a bump. It is exempt because a platform <b>cannot reach a
    /// character</b>, not because it looked harmless: the stage writes only to
    /// <see cref="WorldState.MovingPlatforms"/> — the three values that go on the
    /// wire — and the one path from there to a character is the <em>carry</em>,
    /// which is not ported. No trajectory, damage number or projectile can differ,
    /// whatever a map places.
    /// </para>
    /// <para>
    /// ⚠️ <b>The first wording of this exemption said the stage was structurally
    /// unreachable because no shipped map places a platform. That was true on
    /// 2026-09-01 and stopped being true within the week</b>, when
    /// <c>gg_debug2</c> arrived in this repo's own <c>maps/</c> with four of them
    /// — and the test named as the gate <b>could not have noticed</b>, because it
    /// scanned only the <em>reference's</em> maps. A gate that cannot fire is
    /// worse than no gate: it reads as diligence while checking nothing. Both
    /// halves are corrected — the claim above no longer depends on absence, and
    /// <c>MovingPlatformTests.NoShippedMapPlacesAPlatformThatCouldCarryAnyone</c>
    /// now scans both map sources and asserts the property that matters: that no
    /// shipped platform names a <c>resource</c>, without which it has no sprite,
    /// no mask, and nothing can stand on it in GM8 either.
    /// </para>
    /// <para>
    /// ⛔ <b>What voids this exemption: porting the CARRY.</b> The day a platform
    /// can move a character, this stage moves trajectories and must bump — and
    /// that is a change somebody makes deliberately, so it is a condition a person
    /// can act on rather than one a test must catch. Until then a shipped map
    /// gaining a platform is not by itself a reason to bump; a shipped map gaining
    /// a platform <em>with a sprite</em> is, and that is what goes red.
    /// </para>
    /// <para>
    /// ⚠️ <b>Custom maps are the live edge.</b> Nothing stops an operator pointing
    /// <c>--map</c> at a map with a platform on it today. The exemption is about
    /// what the <em>checkpoint</em> was trained against, and no training run uses
    /// a custom map; a match on one is outside the stamp's claim either way.
    /// </para>
    /// </remarks>
    public const int SimulationBehaviourVersion = 1;

    /// <summary>The types whose public constants are part of the contract.</summary>
    /// <remarks>
    /// ⚠️ <c>GameConstants</c> is NOT here, on purpose — see the type's remarks.
    /// </remarks>
    private static readonly Type[] ConstantSources =
    [
        // ⚠️ ClassStats is where the PHYSICS constants live — gravity, the
        // control and friction factors, terminal velocity, the rest speed — not
        // just the per-class table below. Leaving it out was the first draft's
        // mistake and would have made a changed gravity invisible to the stamp,
        // which is the single thing it most needs to catch.
        typeof(ClassStats),
        typeof(Gml),
        typeof(BotConstants),
        typeof(NavConstants),
    ];

    private static readonly string Digest = Compute();

    /// <summary>The stamp: sixteen hex characters over the whole surface.</summary>
    public static string Uuid => Digest;

    /// <summary>
    /// Refuses a policy whose stamp is not this build's.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Throws rather than logging</b>, which is the entire point of the
    /// item: a mismatch that merely warns is a mismatch nobody notices until the
    /// policy has been blamed for months.
    /// </remarks>
    public static void Require(string stamp)
    {
        if (!string.Equals(stamp, Digest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"this policy was trained against simulation {stamp}, and this build is {Digest}");
        }
    }

    /// <summary>
    /// The canonical surface the stamp is taken over, one entry per line.
    /// </summary>
    /// <remarks>
    /// Exposed so a test can assert what the stamp covers rather than only that
    /// it is stable — a digest that silently stopped covering gravity would look
    /// exactly like one that covered it and never changed.
    /// </remarks>
    public static IReadOnlyList<string> Surface() => Lines();

    private static List<string> Lines()
    {
        var lines = new List<string>
        {
            $"action={ActionEncodingVersion}",
            $"observation={ObservationLayoutVersion}",
            $"behaviour={SimulationBehaviourVersion}",
        };

        // ⚠️ Sorted by name, because reflection's field order is unspecified and
        // a stamp that depended on it would move between runtimes for nothing.
        foreach (var source in ConstantSources)
        {
            var constants = source
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && !f.IsInitOnly)
                .OrderBy(f => f.Name, StringComparer.Ordinal);

            foreach (var field in constants)
                lines.Add($"{source.Name}.{field.Name}={Format(field.GetRawConstantValue())}");
        }

        // The per-class tables. ⚠️ A class's run power, jump strength and hit
        // points are contract in exactly the sense that matters here: a policy
        // that learned to lead a scout is wrong the moment a scout is slower.
        foreach (var playerClass in Enum.GetValues<PlayerClass>().OrderBy(c => (int)c))
        {
            var stats = ClassStats.For(playerClass);
            var box = CharacterBox.For(playerClass);

            lines.Add(
                $"class.{playerClass}=run:{Format(stats.RunPower)}"
                + $",hp:{Format(stats.MaxHp)}"
                + $",djump:{stats.CanDoubleJump}"
                + $",cloak:{stats.CanCloak}"
                + $",box:{box.Left},{box.Top},{box.Right},{box.Bottom}");
        }

        // The per-projectile blast tables.
        foreach (var kind in Enum.GetValues<ProjectileKind>().OrderBy(k => (int)k))
        {
            if (kind is not (ProjectileKind.Rocket or ProjectileKind.Mine))
                continue;

            lines.Add(
                $"blast.{kind}=radius:{Format(Explosions.BlastRadiusFor(kind))}"
                + $",damage:{Format(Explosions.ExplosionDamageFor(kind))}");
        }

        return lines;
    }

    /// <summary>
    /// ⚠️ Round-trip format, so a <b>one-ULP</b> change to a constant moves the
    /// stamp. <c>Gml.PointSix</c> and its four siblings exist precisely because
    /// GM8 parses some decimals one ULP above the IEEE nearest; a stamp that
    /// printed them to fifteen digits could not tell those apart from the plain
    /// literals and would miss the change the whole family is about.
    /// </summary>
    private static string Format(object? value) => value switch
    {
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        null => "null",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    /// <summary>
    /// FNV-1a over the surface, 64 bits as hex.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not <c>string.GetHashCode()</c>, which is randomised per process</b>
    /// — a stamp that changed every time the server restarted would refuse every
    /// policy including its own. This is a change detector rather than a security
    /// boundary, so a non-cryptographic hash is the right tool and pulling in
    /// <c>System.Security.Cryptography</c> for it would not be.
    /// </remarks>
    private static string Compute()
    {
        const ulong Offset = 14695981039346656037;
        const ulong Prime = 1099511628211;

        var hash = Offset;

        foreach (var line in Lines())
        {
            foreach (var b in Encoding.UTF8.GetBytes(line))
            {
                hash ^= b;
                hash *= Prime;
            }

            hash ^= (byte)'\n';
            hash *= Prime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}

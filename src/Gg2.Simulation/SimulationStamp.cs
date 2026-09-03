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
    /// ⛔ <b>2, 2026-09-01: THE CARRY. The exemption below is spent — read it as
    /// history, not as a live licence.</b> <c>MovingPlatforms.Carry</c> moves a
    /// character with the platform it is standing on and backs one out of a
    /// platform that drives into it, so this stage now moves trajectories
    /// directly. That is the unconditional bump the paragraphs below said it
    /// would be, taken the moment it became true rather than argued around.
    /// <para>
    /// ⚠️ <b>Every checkpoint trained against <c>7ff5940d97aa7bed</c> is
    /// invalid</b> and <see cref="Require"/> will refuse it. That is the stamp
    /// working, not a regression — but it means <c>gg2-rl</c> must re-stamp or
    /// retrain before a policy loads, and until then <c>PolicyBot</c> cannot run.
    /// </para>
    /// <para>
    /// ⚠️ <b>Reachability bought nothing here and was never going to.</b> The
    /// sealed-chamber argument was already dead — <c>gg2-rl</c> seeds duels from
    /// nav nodes, and 18 of <c>gg_debug2</c>'s 70 are inside sealed regions — so
    /// there was no version of this change that could have shipped unbumped.
    /// </para>
    /// </para>
    /// <para>
    /// ⚠️ <b>HISTORY: NOT bumped for moving platforms, earlier on 2026-09-01.</b>
    /// <c>MovingPlatforms.EndStep</c> arrived as a new stage in
    /// <see cref="MatchSimulation.Tick"/> that only moved the platform. It was
    /// exempt because a platform <b>could not reach a character</b> — the stage
    /// wrote only to <see cref="WorldState.MovingPlatforms"/>, and the one path
    /// from there to a character was the carry, which did not exist yet. That
    /// held for exactly as long as the carry was missing.
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
    /// now scans both map sources and asserts the property that matters. (What
    /// that property IS narrowed again the same day; the paragraph below is the
    /// current one, and this sentence deliberately does not restate it — two
    /// statements of the same predicate is how one of them goes stale.)
    /// </para>
    /// <para>
    /// ⛔ <b>What voids this exemption: porting the CARRY — or the standing box
    /// alone.</b> The day a platform can support or move a character, this stage
    /// moves trajectories and must bump; that is a change somebody makes
    /// deliberately, so it is a condition a person can act on rather than one a
    /// test must catch. Until then a shipped map gaining a platform is not by
    /// itself a reason to bump; a shipped map gaining a platform with a sprite
    /// <em>somewhere a character can be</em> is, and that is what goes red.
    /// </para>
    /// <para>
    /// ⚠️ <b>The gate narrowed on 2026-09-01, by agreement between the lane that
    /// wrote it and the lane that trips it, when <c>gg_debug2</c> gained the
    /// raft</b> — a resourced platform placed to give the mask pipeline a real
    /// embedded sprite, inside a sealed chamber whose unreachability is itself
    /// flood-fill-asserted. The first wording ("no shipped platform names a
    /// resource at all") would have forced a bump for it, and a bump would have
    /// claimed a behaviour change that does not exist: today a resourced
    /// platform still has no <c>SolidRect</c>, so nothing stands on it whatever
    /// its mask says (<see cref="CharacterPhysics"/>'s dropdown scan says why),
    /// and therefore nothing any placement does can differ. The gate asserts
    /// reachability rather than absence:
    /// <c>MovingPlatformTests.NoShippedMapPlacesAPlatformThatCouldCarryAnyone</c>
    /// flood-fills each map from its spawns and fails on any resourced
    /// platform whose swept extent a character could reach.
    /// </para>
    /// <para>
    /// ⛔ <b>The sealed chamber buys NO post-carry exemption, and the bump above
    /// is unconditional — owed to <c>gg2-rl</c>, 2026-09-01.</b> The tempting
    /// argument — "a platform nobody can walk to still cannot move anyone, so
    /// the carry could land without a bump as long as every shaped platform is
    /// sealed" — is false for how training actually places characters:
    /// <c>gg2-rl</c>'s banded duels draw both spawns from <b>nav nodes</b>, not
    /// coordinates, and <c>gg2-nav-gen</c> floods the whole walkable mask rather
    /// than seeding from spawns — <c>gg_debug2</c>'s sealed chambers carry
    /// <b>exactly 18</b> island nodes. Measured independently in this repo, not
    /// taken on report: load <c>nav/gg_debug2_a1.txt</c>, flood the walkmask from
    /// the spawn entities, and count nodes whose stand cell the flood never
    /// reaches. One of them is <c>(348,145)</c> — beside the raft at
    /// <c>(282,150)</c>. An ordinary graph draw could seed a duel inside a sealed
    /// chamber with no teleport and no intent anywhere in either repo. A sealed
    /// region is unreachable by <em>walking</em>, not by <em>placement</em> — so
    /// intent is not a boundary the exemption can be drawn around.
    /// </para>
    /// <para>
    /// ⚠️ <b>Which leaves the gate a TRIPWIRE rather than a proof, and it is
    /// worth being exact about that or somebody will try to extend it.</b> What
    /// licenses today's no-bump is not reachability — it is that a
    /// <c>MovingPlatform</c> has no <c>SolidRect</c> at all, so it touches nothing
    /// whether a character can get to it or not, and the raft would be equally
    /// harmless in the middle of a spawn room. The flood fill earns its keep as an
    /// early warning: it fires when a shipped map gains a shaped platform
    /// somewhere a character can walk, which is the moment the mask pipeline stops
    /// being theoretical. <b>It is not, and must not be turned into, an argument
    /// that some platforms are exempt from the carry bump.</b>
    /// </para>
    /// <para>
    /// ⚠️ <b>Custom maps are the live edge.</b> Nothing stops an operator pointing
    /// <c>--map</c> at a map with a platform on it today. The exemption is about
    /// what the <em>checkpoint</em> was trained against, and no training run uses
    /// a custom map; a match on one is outside the stamp's claim either way.
    /// </para>
    /// </remarks>
    public const int SimulationBehaviourVersion = 2;

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
    ];

    /// <summary>
    /// Constants that move where a bot ENDS UP without changing what any action
    /// DOES. Reported, never gated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Split out of the gating surface 2026-09-03</b>, on the user's
    /// authorisation, after <c>gg2-rl</c> reported being stranded twice in two
    /// days by nav edits their own world fingerprint said had changed nothing.
    /// </para>
    /// <para>
    /// <b>The line, so the next addition knows which side it is on:</b> the
    /// gating surface is things that change what the <em>same action does</em> —
    /// the physics, the class tables, the action encoding, the observation
    /// layout. A checkpoint's learned mapping is WRONG if those move, so a
    /// mismatch must refuse. This surface is things that change <em>which states
    /// the policy is asked about</em>. The mapping is still valid; the input
    /// distribution shifted, and a distribution shift is a reason to re-measure
    /// rather than a reason to refuse to run.
    /// </para>
    /// <para>
    /// ⚠️ <b>Nothing here is read at inference.</b> A policy drives keys and aim
    /// inside an engagement window; routing is the scripted layer's, and the
    /// observation's geometry rays come from <c>Walkmask</c>, not from these.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>BotConstants</c> is arguably the same question and was NOT moved.</b>
    /// It changes when the policy is handed the wheel rather than what an action
    /// does, which is this surface's own description — but no measurement has been
    /// taken across a <c>BotConstants</c> edit the way <c>gg2-rl</c> took one
    /// across the nav edits, and moving it on the strength of the argument alone
    /// would be widening an authorisation past its evidence. Open.
    /// </para>
    /// </remarks>
    private static readonly Type[] RoutingSources =
    [
        typeof(NavConstants),
    ];

    private static readonly string Digest = Compute();

    private static readonly string RoutingDigest = ComputeRouting();

    /// <summary>
    /// The stamp: sixteen hex characters over the surface a mismatch REFUSES on.
    /// </summary>
    public static string Uuid => Digest;

    /// <summary>
    /// A second digest over <see cref="RoutingSources"/> — reported, never gated.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Printed beside <see cref="Uuid"/> so a puzzling behaviour change can
    /// still be attributed.</b> Removing nav from the gate must not mean removing
    /// it from the record: "the bots play differently and both stamps match" and
    /// "the bots play differently and the routing digest moved" are completely
    /// different investigations.
    /// <para>
    /// ⚠️ <b>Nothing compares it yet</b>, because no checkpoint records one. If
    /// <c>gg2-rl</c> starts writing it into their metadata, comparing and WARNING
    /// on a mismatch is the natural next step — and a warning is the right verb
    /// here, where <see cref="Require"/>'s throw is right for the other surface.
    /// </para>
    /// </remarks>
    public static string RoutingUuid => RoutingDigest;

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

    /// <summary>The routing surface, for the same reason.</summary>
    public static IReadOnlyList<string> RoutingSurface() => RoutingLines();

    private static string ComputeRouting() => Hash(RoutingLines());

    private static List<string> RoutingLines()
    {
        var lines = new List<string>();

        // ⚠️ Sorted by name for the same reason the gating surface is: reflection
        // field order is unspecified.
        foreach (var source in RoutingSources)
        {
            var constants = source
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && !f.IsInitOnly)
                .OrderBy(f => f.Name, StringComparer.Ordinal);

            foreach (var field in constants)
                lines.Add($"{source.Name}.{field.Name}={Format(field.GetRawConstantValue())}");
        }

        return lines;
    }

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
    private static string Compute() => Hash(Lines());

    /// <summary>
    /// FNV-1a over the lines, newline-terminated. ⚠️ <b>Shared by both digests on
    /// purpose</b>: two hashes computed different ways would be two things to keep
    /// in step, and the surfaces are already separate enough.
    /// </summary>
    private static string Hash(List<string> surface)
    {
        const ulong Offset = 14695981039346656037;
        const ulong Prime = 1099511628211;

        var hash = Offset;

        foreach (var line in surface)
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

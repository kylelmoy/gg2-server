using System.Globalization;
using System.Reflection;
using System.Text;
using GG2.Protocol;

namespace GG2.Simulation;

/// <summary>
/// A fingerprint of everything a trained policy is allowed to depend on —
/// <c>docs/BOTAI.md</c> item 9's <c>SIM_UUID</c>.
/// </summary>
/// <remarks>
/// <b>A policy trained against physics that has since changed does not error
/// and does not warn. It plays slightly worse for ever and the cause is
/// invisible.</b> GG2 has the idiom one layer up: the reference refuses a client
/// whose <c>PROTOCOL_UUID</c> does not match rather than letting it connect and
/// desync. <see cref="Require"/> is the same thing for a policy file — refuse, do
/// not warn.
/// <para>
/// <b>The surface is chosen, not swept.</b> What goes in is what a policy's
/// behaviour actually depends on: the physics constants, the <c>BOT_*</c> and
/// <c>NAV_*</c> blocks, the per-class and per-projectile tables that
/// <c>docs/SIMULATION.md</c> marks contract, and the three hand-bumped version
/// numbers below. What stays out is configuration and protocol — a port number or
/// a maximum name length moving must not invalidate every checkpoint, and
/// <c>GameConstants</c> is therefore deliberately absent.
/// </para>
/// <para>
/// <b>Every bump invalidates every checkpoint, and that is correct.</b> The
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
    /// <c>EnvAction</c>'s sixth head, <c>Zoom</c>, is the sniper's scope as a
    /// LEVEL rather than a toggle: <c>toggleZoom</c> is a client command rather
    /// than a key, so a bot's output through <see cref="PlayerInput"/> alone
    /// cannot reach it, and a policy without this head fires an unscoped 35
    /// against a weapon that reaches 75. See docs/TRAPS-BOTS.md and
    /// <c>BotZoomTests</c>.
    /// <para>
    /// The bump is free until a checkpoint exists to invalidate — a head added
    /// afterwards would invalidate every checkpoint before it and every number
    /// derived from them.
    /// </para>
    /// </remarks>
    public const int ActionEncodingVersion = 2;

    /// <summary>
    /// The observation layout.
    /// </summary>
    /// <remarks>
    /// The authority is <c>GG2.Bots.ObservationWriter</c>'s field order and its
    /// own <c>LayoutVersion</c>, which this mirrors.
    /// <para>
    /// <b>Mirrored rather than referenced.</b> <c>GG2.Simulation</c> does not
    /// and must not depend on <c>GG2.Env</c> — the simulation is the thing an
    /// env observes, not the other way round — so the two constants are held
    /// equal by a test instead of by the compiler.
    /// </para>
    /// <para>
    /// <b>Bumping this changes <see cref="Uuid"/>, and that is the point.</b>
    /// A checkpoint trained against one layout must not silently load against
    /// another. When it moves, the pinned digest in <c>SimulationStampTests</c>
    /// is <em>updated as the bump</em>, not fixed as a broken test.
    /// </para>
    /// </remarks>
    public const int ObservationLayoutVersion = 2;

    /// <summary>
    /// What the simulation <em>does</em>, as opposed to the constants it does it
    /// with. Bump when you knowingly change behaviour that a policy could have
    /// learned against.
    /// </summary>
    /// <remarks>
    /// The rest of this surface is constants and two encodings, so a change to
    /// control flow in the physics (e.g. inside <c>Movement.cs</c>) can move a
    /// trajectory without moving the stamp unless this counter is bumped by hand.
    /// The failure that hides behind a missed bump is the one this whole type
    /// exists to prevent: a policy that plays slightly worse for ever with the
    /// cause invisible.
    /// <para>
    /// <b>What counts as a bump.</b> Anything that can move a trajectory, a
    /// damage number, a projectile path or a stage's position in
    /// <c>MatchSimulation</c>. <b>What does not: a refactor, a rename, a
    /// comment, a performance change — or a change DEMONSTRATED to be
    /// unobservable</b> (e.g. by reproducing training numbers to the digit
    /// across independent runs, not by argument alone — reasoning is not
    /// checking).
    /// </para>
    /// <para>
    /// <b>Deliberately hand-bumped, and deliberately not a sweep of the
    /// code.</b> Hashing the assembly would move on a comment, a refactor or a
    /// compiler upgrade, invalidate every checkpoint for nothing, and teach
    /// everyone to ignore the stamp. The surface is chosen — see this type's
    /// remarks — and a number somebody has to remember to bump is the honest
    /// price of that choice. It shares the failure mode of the two above it: it
    /// catches the change you knew you were making, not the one you did not.
    /// </para>
    /// <para>
    /// <b>2: the moving-platform carry.</b> <c>MovingPlatforms.Carry</c> moves a
    /// character with the platform it is standing on and backs one out of a
    /// platform that drives into it, which moves trajectories directly and is an
    /// unconditional bump. Every checkpoint trained against an earlier stamp is
    /// invalid and <see cref="Require"/> refuses it — that is the stamp working,
    /// not a regression.
    /// <para>
    /// A sealed-chamber argument does not buy an exemption from this bump: a
    /// trainer that seeds duels from nav nodes rather than coordinates (as
    /// <c>gg2-rl</c> does) can place a character inside a region a flood fill
    /// from spawn points would call unreachable, because a nav graph floods the
    /// whole walkable mask rather than seeding from spawns. Unreachable by
    /// <em>walking</em> is not the same as unreachable by <em>placement</em>, so
    /// "nobody can walk there" is not a boundary an exemption can be drawn
    /// around.
    /// </para>
    /// </para>
    /// <para>
    /// <b>A <c>MovingPlatform</c> has no <c>SolidRect</c></b>
    /// (<see cref="CharacterPhysics"/>'s dropdown scan says why), so nothing
    /// stands on one regardless of its mask or placement, and a shipped map
    /// gaining a platform is not by itself a reason to bump — only a platform
    /// with a sprite <em>somewhere a character can reach</em> is.
    /// <c>MovingPlatformTests.NoShippedMapPlacesAPlatformThatCouldCarryAnyone</c>
    /// flood-fills each map from its spawns and fails on any resourced platform
    /// whose swept extent a character could reach; this is a tripwire for when
    /// the mask pipeline stops being theoretical, not a proof that any given
    /// platform is exempt from the carry bump.
    /// </para>
    /// <para>
    /// Custom maps are the live edge: nothing stops an operator pointing
    /// <c>--map</c> at a map with a platform on it. The exemption is about what
    /// the <em>checkpoint</em> was trained against, and no training run uses a
    /// custom map; a match on one is outside the stamp's claim either way.
    /// </para>
    /// <para>
    /// <b>3: queued jumping.</b> <c>CharacterPhysics</c> reads
    /// <c>player.queueJump</c> and the <c>wantToJump</c> queue it drives, so a
    /// client with the setting on jumps the tick it lands instead of waiting for
    /// a fresh press — this moves trajectories and is an unconditional bump.
    /// <see cref="PlayerState.QueueJump"/> is ordinary <c>WorldState</c>, so
    /// anything that builds a state can set it regardless of whether a bot or
    /// fixture does so today; the stamp is about what a checkpoint was trained
    /// against, and a build where the jump rule depends on a state field is a
    /// different build from one where it cannot, whether or not the field is
    /// exercised yet.
    /// </para>
    /// <para>
    /// <b>4: nearest-first projectile queries.</b>
    /// <see cref="WorldView.ProjectilesWithin"/> filled a caller's span in
    /// creation order and stopped; it now picks the nearest. Both
    /// <c>BotReflex</c> uses are existential — the first projectile satisfying
    /// the predicate returns <c>true</c> — so an answer cannot depend on the
    /// order the set is walked, and the set itself is identical whenever the
    /// caller's span is at least as long as the number in radius.
    /// <c>BotReflex</c> asks for 32.
    /// <para>
    /// <b>Bumped anyway, because that is not a demonstration.</b>
    /// <see cref="WorldState.MaxProjectiles"/> is 512, so above 32 in radius the
    /// set genuinely differs (nearest 32 rather than first 32) and an airblast or
    /// a dodge can flip. Nobody has measured how often a firefight passes 32, and
    /// the exemption above is for changes <em>demonstrated</em> unobservable
    /// rather than argued so. The 26 pinned bot tests did not move, which is
    /// consistent with the argument and is not the same as establishing it.
    /// </para>
    /// </para>
    /// <para>
    /// <b>NOT bumped, by the owner's decision: the per-class afterburn values.</b>
    /// <see cref="ClassStats.MaxBurnDuration"/> (a pyro's 10 against everyone
    /// else's 210) and <see cref="ClassStats.NumFlames"/> were a single constant
    /// for every class, so a pyro burned 21 times longer than the reference's.
    /// That is a damage number and the rule above says bump. It was not bumped
    /// because the stamp only dates the <c>.npz</c> checkpoints, which are test
    /// fixtures and two shipped files no host loads, and their trainer
    /// (<c>gg2-rl</c>) has concluded. The <c>.gg2p</c> nets every host runs carry
    /// no stamp at all. Both values are also left out of the class line below for
    /// that reason. The next bump for any other cause should add them to it.
    /// </para>
    /// </remarks>
    public const int SimulationBehaviourVersion = 4;

    /// <summary>The types whose public constants are part of the contract.</summary>
    /// <remarks>
    /// <c>GameConstants</c> is NOT here, on purpose — see the type's remarks.
    /// </remarks>
    private static readonly Type[] ConstantSources =
    [
        // ClassStats is where the PHYSICS constants live — gravity, the
        // control and friction factors, terminal velocity, the rest speed — not
        // just the per-class table below. Leaving it out was the first draft's
        // mistake and would have made a changed gravity invisible to the stamp,
        // which is the single thing it most needs to catch.
        typeof(ClassStats),
        typeof(Gml),
    ];

    /// <summary>
    /// Constants that move where a bot ENDS UP without changing what any action
    /// DOES. Reported, never gated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out of the gating surface so a nav edit that changes only where a
    /// bot ends up, not what an action does, does not strand a checkpoint whose
    /// world fingerprint says nothing behavioural changed.
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
    /// <b>A routing change really can move behaviour, which is why this is
    /// REPORTED rather than dropped.</b> A <c>NavConstant</c> like the A* search
    /// weight moving is exactly the kind of edit that changes how the bots
    /// play, and a change here can be the sole cause of an observed behaviour
    /// shift even when the gating <see cref="Uuid"/> did not move. A surface
    /// whose edits change how the bots play is exactly the surface you want a
    /// digest for; what it does not justify is <em>refusing to run</em>.
    /// </para>
    /// <para>
    /// <b>Nothing here is read at inference.</b> A policy drives keys and aim
    /// inside an engagement window; routing is the scripted layer's, and the
    /// observation's geometry rays come from <c>Walkmask</c>, not from these.
    /// </para>
    /// <para>
    /// <b><c>BotConstants</c> is here too, and the argument that put it here is
    /// checked rather than asserted.</b> Its 117 constants are read by the
    /// scripted bot layer, by <c>GG2.Nav</c> and by <c>HandoverRule</c> — and by
    /// nothing else. <b>No part of <c>GG2.Simulation</c> reads one</b>, so by
    /// construction none of them can change what an action does; and no part of
    /// the inference path does either — not <c>ObservationWriter</c>, not
    /// <c>ActionDecoder</c>, not <c>PolicyNetwork</c>, <c>PolicyController</c>,
    /// <c>HybridBot</c> or <c>Handover</c>. What they change is which states the
    /// policy is asked about and when it is handed the wheel, which is this
    /// surface's own description.
    /// <para>
    /// ⛔ <b><c>BotConstantsReachTests</c> is what keeps that true</b>, and it has
    /// to be a SOURCE scan: a <c>const</c> is inlined at compile time, so the
    /// reference is gone from the IL and no reflection or assembly-reference test
    /// can see it. If that test is deleted, this paragraph becomes an unfalsifiable
    /// claim about the repo — which is the failure mode <c>CLAUDE.md</c> names.
    /// </para>
    /// </para>
    /// </remarks>
    private static readonly Type[] RoutingSources =
    [
        typeof(NavConstants),
        typeof(BotConstants),
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
    /// <b>Printed beside <see cref="Uuid"/> so a puzzling behaviour change can
    /// still be attributed.</b> Removing nav from the gate must not mean removing
    /// it from the record: "the bots play differently and both stamps match" and
    /// "the bots play differently and the routing digest moved" are completely
    /// different investigations.
    /// <para>
    /// <b>Nothing compares it yet</b>, because no checkpoint records one. If
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
    /// <b>Throws rather than logging</b>, which is the entire point of the
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

        // Sorted by name for the same reason the gating surface is: reflection
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

        // Sorted by name, because reflection's field order is unspecified and
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

        // The per-class tables. A class's run power, jump strength and hit
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
    /// Round-trip format, so a <b>one-ULP</b> change to a constant moves the
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
    /// <b>Not <c>string.GetHashCode()</c>, which is randomised per process</b>
    /// — a stamp that changed every time the server restarted would refuse every
    /// policy including its own. This is a change detector rather than a security
    /// boundary, so a non-cryptographic hash is the right tool and pulling in
    /// <c>System.Security.Cryptography</c> for it would not be.
    /// </remarks>
    private static string Compute() => Hash(Lines());

    /// <summary>
    /// FNV-1a over the lines, newline-terminated. <b>Shared by both digests on
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

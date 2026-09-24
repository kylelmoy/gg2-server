using System.IO.Compression;
using System.Security.Cryptography;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The two checkpoints in <c>checkpoints/</c> are what a clone actually plays.
/// </summary>
/// <remarks>
/// <b>Handed over by the <c>gg2-rl</c> lane on 2026-09-07 at the user's
/// instruction</b>, with <c>checkpoints/README.md</c> beside them carrying the
/// provenance and the numbers. They are SHIPPED ARTEFACTS, not fixtures: the
/// files under <c>tests/Gg2.Tests/Fixtures/policy/</c> exist to exercise
/// <see cref="PolicyNetwork"/>'s arithmetic and its metadata reader, and these
/// exist so <c>gg2-server --bot-mode policy</c> has something to drive with.
/// <para>
/// ⛔ <b>Nothing else in this repository would notice if these files went
/// missing, went stale, or quietly stopped driving the classes the bundle says
/// they drive.</b> The suite is deliberately independent of <c>gg2-rl</c>
/// producing anything (<c>docs/BOTS.md</c>), which is right — and it means the
/// shipped configuration is exactly the kind of claim <c>CLAUDE.md</c> warns
/// about: true on the machine that wrote it, unfalsifiable everywhere else. This
/// class is the falsifier.
/// </para>
/// <para>
/// ⚠️ <b>A green run here still says NOTHING about whether either checkpoint
/// plays well.</b> It says the files are present, load through the ordinary
/// stamp gate, and route the classes the README claims. The behavioural evidence
/// is the training lane's and is quoted — with what it does and does not cover —
/// in <c>checkpoints/README.md</c> and <c>docs/TRAPS-BOTS.md</c>.
/// </para>
/// </remarks>
public sealed class ShippedCheckpointTests
{
    /// <summary>Where a clone finds them, and where the config file's probe points.</summary>
    private static string Directory =>
        Path.Combine(ReferenceTree.RepositoryRoot, "checkpoints");

    private static string Tier0 => Path.Combine(Directory, "tier0_combat4_s2.npz");

    private static string Tier2 => Path.Combine(Directory, "tier2_pyro_rest3_s0.npz");

    /// <summary>
    /// Both files are present, and their WEIGHTS are the ones that were handed
    /// over.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Every number in <c>checkpoints/README.md</c> was measured against
    /// these arrays and against nothing else</b>, so a re-minted, re-trained or
    /// truncated file must fail here rather than silently re-point the evidence.
    /// <para>
    /// ⚠️ <b>What to do when this goes red is NOT to update the constant.</b> A
    /// checkpoint whose weights changed has new numbers or no numbers; take the
    /// README with it, or the pin becomes a rubber stamp.
    /// </para>
    /// <para>
    /// <b>The digest is over every member EXCEPT <c>meta.npy</c>, and that is the
    /// point.</b> It was the whole file's md5 until 2026-09-08, when
    /// <c>BotConstants</c> left the gating surface and both checkpoints were
    /// re-stamped: sixteen ASCII characters inside <c>meta.npy</c> changed, every
    /// weight stayed byte-identical, and the pin went red anyway — which under its
    /// own instruction above would have meant re-measuring a README whose numbers
    /// could not have moved. A digest that a provenance edit can break is a digest
    /// that gets updated by hand, and a pin updated by hand is the rubber stamp
    /// the remark warns about.
    /// </para>
    /// <para>
    /// ⚠️ <b>So the metadata is pinned separately, not dropped</b> — see
    /// <see cref="TheShippedCheckpointsCarryTheMetadataTheyWereShippedWith"/>. A
    /// re-stamp is expected to move that one and to leave this one alone; anything
    /// that moves THIS is a different set of weights whatever the filename says.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("tier0_combat4_s2.npz", "2cc556117ade6d5f20b62cb9dab89ba6")]
    [InlineData("tier2_pyro_rest3_s0.npz", "5493babe1dbccb513f40d751f3703e81")]
    public void TheShippedCheckpointsAreTheWeightsThatWereHandedOver(string name, string md5)
    {
        var path = Path.Combine(Directory, name);

        Assert.True(
            File.Exists(path),
            $"{path} is missing. checkpoints/ ships with this repository — see "
            + "checkpoints/README.md — and without it --bot-mode policy has nothing to load.");

        Assert.Equal(md5, WeightsDigest(path));
    }

    /// <summary>
    /// The metadata each checkpoint ships with, pinned so a re-stamp is a
    /// decision rather than a drift.
    /// </summary>
    /// <remarks>
    /// Split off the weights pin above so the two failures mean different things:
    /// this one moving is a provenance edit, that one moving is different weights.
    /// Before the split a re-stamp broke the only pin there was, and the two were
    /// indistinguishable from the failure message.
    /// </remarks>
    [Theory]
    [InlineData("tier0_combat4_s2.npz", "e3e3045f4146afd36e47cd7c3f2e115f")]
    [InlineData("tier2_pyro_rest3_s0.npz", "02746be6ad0f6f1ba929b3244a5cc802")]
    public void TheShippedCheckpointsCarryTheMetadataTheyWereShippedWith(string name, string md5)
    {
        using var archive = ZipFile.OpenRead(Path.Combine(Directory, name));
        using var meta = archive.GetEntry("meta.npy")!.Open();
        using var buffer = new MemoryStream();

        meta.CopyTo(buffer);

        Assert.Equal(md5, Convert.ToHexStringLower(MD5.HashData(buffer.ToArray())));
    }

    /// <summary>
    /// md5 over every member except <c>meta.npy</c>, names included, in a fixed
    /// order.
    /// </summary>
    /// <remarks>
    /// Names are hashed alongside the bytes so a renamed array cannot pass, and
    /// the order is sorted rather than the archive's own so re-zipping a file
    /// cannot move the digest either.
    /// </remarks>
    private static string WeightsDigest(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var md5 = MD5.Create();

        foreach (var entry in archive.Entries
            .Where(e => e.Name != "meta.npy")
            .OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var name = System.Text.Encoding.UTF8.GetBytes(entry.Name);
            md5.TransformBlock(name, 0, name.Length, null, 0);

            using var stream = entry.Open();
            using var buffer = new MemoryStream();

            stream.CopyTo(buffer);

            var bytes = buffer.ToArray();
            md5.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        md5.TransformFinalBlock([], 0, 0);

        return Convert.ToHexStringLower(md5.Hash!);
    }

    /// <summary>
    /// Both load through the ORDINARY gate — no <c>ignoreSimulationStamp</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The bypass is deliberately not used here, and that is the whole
    /// assertion.</b> A policy is DATED by the physics: change the simulation and
    /// these weights describe a game that no longer exists.
    /// <see cref="PolicyNetwork.Load"/> refuses a foreign stamp, so a
    /// <c>SIM_UUID</c> move lands here, naming both stamps, on the same run that
    /// moved <c>SimulationStampTests</c>' pin — instead of on a playtester
    /// wondering why the bots feel wrong. <c>docs/BOTS.md</c>, "a policy is DATED
    /// by the physics".
    /// </remarks>
    [Fact]
    public void BothShippedCheckpointsLoadAgainstThisBuildsStamp()
    {
        foreach (var path in new[] { Tier0, Tier2 })
        {
            var policy = PolicyNetwork.Load(path);

            Assert.Equal(SimulationStamp.Uuid, policy.SimulationUuid);

            // The handover's own three: layout v2 is 211 floats wide, and the
            // decoder holds a decision for four ticks. A checkpoint that loaded but
            // disagreed on either would be a different training run wearing this
            // one's name.
            Assert.Equal(211, policy.ObservationLength);
            Assert.Equal(4, policy.ActionRepeat);
        }
    }

    /// <summary>
    /// Each tier drives exactly the classes the bundle says it drives.
    /// </summary>
    /// <remarks>
    /// <b>Both directions, per class, all nine.</b> Asserting only the members
    /// would pass for a file that drives everything, and asserting only the
    /// non-members would pass for a file that drives nothing — and a checkpoint
    /// driving nothing is invisible: <c>HybridBot</c> falls back to the scripted
    /// layer for a class no tier claims, which is a complete, plausible-looking
    /// bot. That is the all-scripted arm wearing the hybrid one's name.
    /// <para>
    /// ⚠️ <b><c>tier2_pyro</c>'s set is a SHOULD-DRIVE set narrowed from a
    /// TRAINED-ON one.</b> The file says <c>PYRO</c>; the run trained
    /// <c>ENGINEER,SPY,PYRO</c>. Nothing in either repository can tell the two
    /// apart, because there is one field — so what this test pins is what the
    /// server will do, which is the only half that is checkable here.
    /// <c>docs/TRAPS-BOTS.md</c>, "a should-drive set lives in a trained-on field".
    /// </para>
    /// </remarks>
    [Fact]
    public void EachTierDrivesExactlyTheClassesTheBundleClaims()
    {
        AssertDrives(
            Tier0,
            PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Demoman, PlayerClass.Heavy);

        AssertDrives(Tier2, PlayerClass.Pyro);
    }

    /// <summary>
    /// ⛔ The two class sets are DISJOINT, which is what makes their order moot.
    /// </summary>
    /// <remarks>
    /// <b>The bundle's own load-bearing claim, and the reason its README says
    /// "register them in either order".</b> <c>HybridBot.EligibleTiers</c> builds a
    /// bitmask and <c>Choose</c> takes the most capable eligible tier, so with an
    /// overlap the registration order decides which weights drive the overlapping
    /// class — a behaviour change with no flag, no log line and no test behind it.
    /// Disjoint sets mean exactly one bit is ever set, so there is nothing to
    /// decide.
    /// <para>
    /// ⚠️ <b>An overlap is legal</b> — it is a fallback ladder, and
    /// <c>BotBrain.Build</c> warns rather than refusing. What must not happen
    /// silently is these two shipped files growing one.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTwoShippedTiersDriveDisjointClassSets()
    {
        var tier0 = PolicyNetwork.Load(Tier0).TrainedClasses.ToArray();
        var tier2 = PolicyNetwork.Load(Tier2).TrainedClasses.ToArray();

        var both = tier0.Intersect(tier2).ToArray();

        Assert.True(
            both.Length == 0,
            $"the two shipped tiers now both drive {string.Join(", ", both)}, so the order they "
            + "are registered in decides which weights play those classes. Either re-mint them "
            + "disjoint or say in checkpoints/README.md which tier wins and why.");
    }

    /// <summary>
    /// Five of the nine classes are on a policy; the other four stay scripted.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The four that stay scripted are a DECISION, not a gap</b>, and two of
    /// them were dropped on judgement rather than on a refutation — sniper's mirror
    /// is 89.08 and spy's 88.22, both clearing. <c>checkpoints/README.md</c> and
    /// <c>docs/TRAPS-BOTS.md</c> have the reasoning. This asserts the shipped total
    /// so that widening or narrowing it is a deliberate edit here rather than a
    /// side effect of swapping a file.
    /// </remarks>
    [Fact]
    public void FiveOfTheNineClassesAreDrivenByTheShippedBundle()
    {
        var driven = new HashSet<PlayerClass>();

        foreach (var path in new[] { Tier0, Tier2 })
        {
            foreach (var playerClass in PolicyNetwork.Load(path).TrainedClasses)
                driven.Add(playerClass);
        }

        Assert.Equal(
            [
                PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Demoman,
                PlayerClass.Heavy, PlayerClass.Pyro,
            ],
            driven.OrderBy(playerClass => (int)playerClass).ToArray());

        // The four the bundle leaves to the scripted layer, named so that a file
        // swap that silently picked one up fails here.
        foreach (var scripted in new[]
                 {
                     PlayerClass.Sniper, PlayerClass.Spy,
                     PlayerClass.Engineer, PlayerClass.Medic,
                 })
        {
            Assert.DoesNotContain(scripted, driven);
        }
    }

    /// <summary>
    /// <c>tier0</c> is byte-identical to the checkpoint the fixtures already held.
    /// </summary>
    /// <remarks>
    /// <b>The same argument <c>MapCopyTests</c> makes about <c>maps/</c>.</b> The
    /// shipped tier 0 and <c>Fixtures/policy/combat4_s2_iter12000.npz</c> are the
    /// same 79,845 bytes off the same run, and they are duplicated because they
    /// have different lifecycles: the fixture is what
    /// <see cref="TrainedClassesTests"/>' no-op control loads, the shipped file is
    /// what a server plays. A copy that can go stale is only safe if something
    /// says so when it does.
    /// <para>
    /// ⚠️ <b>WHEN this diverges — not if — delete this test rather than weakening
    /// it</b>, and say in <c>checkpoints/README.md</c> that the two are no longer
    /// the same file. A test asserting a relationship that has been deliberately
    /// broken is worse than no test.
    /// </para>
    /// <para>
    /// ⛔ <b>"When" is the training lane's correction to this comment, and it is
    /// right.</b> The two files' goals separate the moment tier 0 improves: a
    /// FIXTURE wants to be a stable test input, and a SHIPPED ARTEFACT wants to be
    /// the best available policy. So this fails on a ROUTINE event — the next
    /// tier-0 change — rather than on an exceptional one, and a red run here is
    /// the expected outcome of ordinary progress, not a signal that something went
    /// wrong. Read it as a prompt to re-record, the way a pinned bot measurement
    /// is. It is quiet only because <c>gg2-rl</c>'s tier 0 is currently stable.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheShippedTierZeroIsTheSameFileAsTheDefaultSetFixture()
    {
        Assert.Equal(
            File.ReadAllBytes(PolicyFixture.DefaultSetCheckpoint),
            File.ReadAllBytes(Tier0));
    }

    /// <summary>
    /// ⛔ Both shipped tiers really drive, in a real match, one class each.
    /// </summary>
    /// <remarks>
    /// <b>Everything above this test reads metadata.</b> A checkpoint whose
    /// <c>Drives</c> answers were perfect and whose weights never took the wheel
    /// would pass every one of them, and it would look exactly like the
    /// all-scripted arm — a complete, plausible bot playing a normal match. That
    /// is <c>gg2-rl/docs/ENV.md</c>'s four per-slot driver tests that passed with the
    /// driver ignored entirely.
    /// <para>
    /// ⛔ <b>The class restriction is what attributes the ticks to a TIER.</b>
    /// <c>MicroTicks</c> is a <c>HybridBot</c> counter and does not say which tier
    /// spent it — but the two shipped sets are disjoint, so in a pyro-only match
    /// tier 0 is not eligible on any slot and every micro tick is tier 2's. That
    /// makes PYRO the case worth spending a match on: it is the one class in the
    /// bundle with no match number of its own, driven by a file that was minted by
    /// narrowing a three-class set down to it.
    /// </para>
    /// <para>
    /// ⚠️ <b>This is a WIRING test and not a quality one.</b> It asserts the
    /// weights reached the wheel and the match survived it; it says nothing about
    /// whether the bot plays better, and no threshold here should ever be read as
    /// if it did. <c>checkpoints/README.md</c> has what was measured and by whom.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Pyro, 61u)]
    [InlineData(PlayerClass.Scout, 62u)]
    public void EachShippedTierTakesTheWheelInARealMatch(PlayerClass only, uint seed)
    {
        var brain = new BotBrainSpec
        {
            Tiers =
            [
                new PolicyMicroSpec(PolicyNetwork.Load(Tier0), "tier0_combat4_s2"),
                new PolicyMicroSpec(PolicyNetwork.Load(Tier2), "tier2_pyro_rest3_s0"),
            ],
        };

        // The shipped gate, spelled out rather than inherited: 150px and
        // MacroTask are BotBrainSpec's defaults AND the bundle's choice, and a
        // default that silently moved would move this test with it.
        Assert.Equal(150, brain.Range);
        Assert.Equal(HandoverRule.MacroTask, brain.Rule);

        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 8,
            Seed = seed,
            CapLimit = 2,
            TimeLimitMinutes = 3,
            KillLimit = 20,
            MaxTicks = 30 * 60 * 4,
            AllowedClasses = [only],
            Brain = brain,
            HybridTeam = Team.Red,
        };

        var match = HeadlessMatch.Load(
            options, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

        var deaths = 0;

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (match.Events.Died[slot])
                    deaths++;
            }
        }

        long micro = 0;
        long scripted = 0;

        for (var slot = 1; slot < WorldState.MaxSlots; slot++)
        {
            if (match.HybridBotFor(slot) is not { } bot)
                continue;

            micro += bot.MicroTicks;
            scripted += bot.ScriptedTicks;
        }

        // A match that ENDS is not a match that happened.
        Assert.NotEqual(MatchOutcome.Running, match.Outcome);
        Assert.True(match.Tick > 30 * 20, $"{only} match lasted {match.Tick} ticks");
        Assert.True(deaths > 0, $"{only} match had no deaths in {match.Tick} ticks");

        Assert.True(
            micro > 0,
            $"no shipped tier ever took the wheel in an all-{only} match, so this is the "
            + "all-scripted arm wearing the bundle's name");

        // And the macro layer still owns most of the game — the bundle's own
        // number is 5.5% of ticks driven at 150px. Asserted as an inequality
        // because this is one map at one seed and that figure is 21 maps x 100
        // matches; what would be wrong is a policy that had taken over.
        Assert.True(scripted > micro, $"{only}: micro {micro} ticks, scripted {scripted}");
    }

    private static void AssertDrives(string path, params PlayerClass[] expected)
    {
        var controller = new PolicyController(PolicyNetwork.Load(path));
        var name = Path.GetFileName(path);

        foreach (var playerClass in Enum.GetValues<PlayerClass>())
        {
            var wanted = expected.Contains(playerClass);

            Assert.True(
                controller.Drives(playerClass) == wanted,
                $"{name} should {(wanted ? "" : "NOT ")}drive {playerClass}");
        }
    }
}

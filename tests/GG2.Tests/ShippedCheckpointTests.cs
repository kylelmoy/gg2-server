using System.Security.Cryptography;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The nine gg2-sim nets in <c>checkpoints/</c> are what a clone and the fleet
/// play.
/// </summary>
/// <remarks>
/// Nothing else in this repository would notice if one went missing, changed, or
/// stopped reaching the wheel: <c>HybridBot</c> falls back to the scripted layer
/// for a class no tier drives, which is a complete, plausible-looking bot. This
/// class is the falsifier. A green run says the files are the ones
/// <c>checkpoints/README.md</c> lists and that they drive; it says nothing about
/// how well they play, which is gg2-sim's claim.
/// </remarks>
public sealed class ShippedCheckpointTests
{
    private static string Directory => Path.Combine(ReferenceTree.RepositoryRoot, "checkpoints");

    /// <summary>
    /// Every file is present and byte-identical to the one the README describes.
    /// </summary>
    /// <remarks>
    /// When this goes red, update the README's table with the constant: a file
    /// whose bytes changed is a different net and gg2-sim's notes no longer
    /// describe it.
    /// </remarks>
    [Theory]
    [InlineData("ppo-warm-soldier-s1.gg2p", "5d0089aaa40050ba965d2d161101886b")]
    [InlineData("ppo-finetune-scout-s1.gg2p", "e92f443986971f983e550b79b6fb23fd")]
    [InlineData("ppo-finetune2-heavy-s1.gg2p", "3bdbca06345f2f51d3796a6d2b74aabf")]
    [InlineData("ppo-finetune2-sniper-s1.gg2p", "8a3cbff6ce3e17af6b3b753fd613f955")]
    [InlineData("ppo-freeze-demoman-s1.gg2p", "d6a922b4c0e24e513c09bcb98ef684a5")]
    [InlineData("ppo-finetune2-pyro-s1.gg2p", "bcc7fe5ed7edb41fd32bfb9c2e3d236c")]
    [InlineData("ppo-freeze-medic-s1.gg2p", "e9014acc53993a00ffd7a24bfa4f48b6")]
    [InlineData("ppo-freeze-engineer-s1.gg2p", "1e901bb88fd79b64a0ed152d23c15fb2")]
    [InlineData("ppo-freeze-spy-s1.gg2p", "0bbf9157dc701538b053f99916ef185c")]
    public void EachShippedNetIsTheFileTheReadmeDescribes(string name, string md5)
    {
        var path = Path.Combine(Directory, name);

        Assert.True(File.Exists(path), $"{path} is missing; checkpoints/README.md lists it.");
        Assert.Equal(md5, Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(path))));

        // Every one is a base layout v2 team net (README).
        var network = SimPolicyNetwork.Load(path);
        Assert.True(network.IsTeam, $"{name} is not a team net");
        Assert.True(network.IsV2, $"{name} is not layout v2");
    }

    /// <summary>
    /// Nothing else ships in <c>checkpoints/</c>: only <c>.gg2p</c> is loadable.
    /// </summary>
    [Fact]
    public void TheDirectoryHoldsOnlyTheNineNetsAndTheReadme()
    {
        var names = System.IO.Directory.GetFiles(Directory).Select(Path.GetFileName).Order().ToArray();

        Assert.Equal(10, names.Length);
        Assert.Contains("README.md", names);
        Assert.All(names.Where(n => n != "README.md"), n => Assert.EndsWith(".gg2p", n));
    }

    /// <summary>
    /// The soldier net really takes the wheel in a real match.
    /// </summary>
    /// <remarks>
    /// Everything above reads files. A net that loaded and never drove would pass
    /// all of it and look exactly like the all-scripted arm. Soldier-only, so every
    /// micro tick is this tier's; the distance rule at 150 px is what the fleet
    /// runs. A wiring test, not a quality one.
    /// </remarks>
    [Fact]
    public void TheSoldierNetTakesTheWheelInARealMatch()
    {
        var brain = BotBrainSpec.Of(PolicyFixture.Tier()) with { Rule = HandoverRule.Distance };

        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 8,
            Seed = 61,
            CapLimit = 2,
            TimeLimitMinutes = 3,
            KillLimit = 20,
            MaxTicks = 30 * 60 * 4,
            AllowedClasses = [PlayerClass.Soldier],
            Brain = brain,
            HybridTeam = Team.Red,
        };

        var match = HeadlessMatch.Load(options, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
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
        Assert.True(match.Tick > 30 * 20, $"match lasted {match.Tick} ticks");
        Assert.True(deaths > 0, $"no deaths in {match.Tick} ticks");
        Assert.True(micro > 0, "the soldier net never took the wheel");
        Assert.True(scripted > micro, $"micro {micro} ticks, scripted {scripted}");
    }
}

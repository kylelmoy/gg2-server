using System.Collections.Generic;
using System.Linq;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// A census of one state: a bot taking damage and doing nothing about it.
/// </summary>
/// <remarks>
/// <b>The instrument behind docs/TRAPS-BOTS.md, "standing still under fire".</b>
/// 23 maps x 2 seeds x 12 bots, tier 5, capped at 9,000 ticks, counted per alive
/// bot-tick:
/// <list type="bullet">
/// <item><c>underFire</c> - hit within the last 60 ticks by something other than
/// itself</item>
/// <item><c>still</c> - on the ground with its position unchanged since the
/// previous tick</item>
/// <item><c>blind</c> - the combat layer holds nothing to shoot at</item>
/// </list>
/// The pathology is <c>still</c> AND <c>blind</c> while under fire; the deaths
/// column is the control, because a change that only made bots run away would show
/// there.
/// <para>
/// <b>Skipped, like the other measurement runs in this suite.</b> It takes about
/// a minute and its output is a table rather than an assertion, and a number it
/// produced is only comparable against another run of the same build shape — see
/// docs/BOTS.md, "measuring a bot change".
/// </para>
/// </remarks>
public sealed class UnderFireCensusTests(ITestOutputHelper output)
{
    private const int Window = 60;

    [Fact(Skip = "Measurement, not a gate. Numbers in docs/TRAPS-BOTS.md.")]
    public void Census()
    {
        var maps = MapDirectory.Resolve();
        var nav = MapDirectory.ResolveNav();

        var names = Directory
            .EnumerateFiles(maps, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: > 0 })
            .Select(n => n!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        long totalAlive = 0, totalFire = 0, totalFireStill = 0, totalFireBlind = 0, totalStillBlind = 0, totalDeaths = 0;

        foreach (var name in names)
        {
            long alive = 0, fire = 0, fireStill = 0, fireBlind = 0, fireStillBlind = 0;
            long ticks = 0, deaths = 0;

            foreach (var seed in new uint[] { 1, 2 })
            {
                var options = HeadlessMatchOptions.Default with
                {
                    Map = name,
                    Seed = seed,
                    MaxTicks = 9000,
                };

                var match = HeadlessMatch.Load(options, maps, nav);

                var lastX = new double[WorldState.MaxSlots];
                var lastY = new double[WorldState.MaxSlots];
                var had = new bool[WorldState.MaxSlots];

                while (match.Outcome == MatchOutcome.Running)
                {
                    match.Step();
                    ticks++;

                    for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                    {
                        if (match.SourceOf(slot) is not ScriptedBot bot)
                        {
                            had[slot] = false;
                            continue;
                        }

                        if (!match.State.IsAlive(slot))
                        {
                            if (had[slot])
                                deaths++;

                            had[slot] = false;
                            continue;
                        }

                        ref readonly var self = ref match.State.Characters[slot];
                        alive++;

                        var underFire = self.TimeUnscathed <= Window
                            && self.LastDamageDealer != CharacterState.Nobody
                            && self.LastDamageDealer != slot;

                        var still = had[slot] && self.OnGround
                            && Gml.Equal(self.X, lastX[slot]) && Gml.Equal(self.Y, lastY[slot]);

                        if (underFire)
                        {
                            fire++;

                            if (still)
                                fireStill++;

                            if (!bot.Combat.Target.Exists)
                            {
                                fireBlind++;

                                if (still)
                                    fireStillBlind++;
                            }
                        }

                        lastX[slot] = self.X;
                        lastY[slot] = self.Y;
                        had[slot] = true;
                    }
                }
            }

            totalAlive += alive;
            totalFire += fire;
            totalFireStill += fireStill;
            totalFireBlind += fireBlind;
            totalStillBlind += fireStillBlind;
            totalDeaths += deaths;

            output.WriteLine(
                $"{name,-22} alive {alive,9} underFire {fire,8} ({100.0 * fire / Math.Max(1, alive),5:0.0}%) "
                + $"still {fireStill,7} ({100.0 * fireStill / Math.Max(1, fire),5:0.0}% of fire) "
                + $"blind {fireBlind,7} ({100.0 * fireBlind / Math.Max(1, fire),5:0.0}%) "
                + $"stillBlind {fireStillBlind,7} ({100.0 * fireStillBlind / Math.Max(1, fire),5:0.0}%) "
                + $"deaths {deaths,5} ({1000.0 * deaths / Math.Max(1, alive),5:0.00}/kilotick)");
        }

        output.WriteLine("");
        output.WriteLine(
            $"TOTAL alive {totalAlive} underFire {totalFire} "
            + $"({100.0 * totalFire / Math.Max(1, totalAlive):0.00}% of alive) "
            + $"stillUnderFire {totalFireStill} ({100.0 * totalFireStill / Math.Max(1, totalFire):0.00}% of fire, "
            + $"{100.0 * totalFireStill / Math.Max(1, totalAlive):0.00}% of alive) "
            + $"blindUnderFire {totalFireBlind} ({100.0 * totalFireBlind / Math.Max(1, totalFire):0.00}% of fire) "
            + $"stillAndBlind {totalStillBlind} ({100.0 * totalStillBlind / Math.Max(1, totalFire):0.00}% of fire) "
            + $"deaths {totalDeaths} ({1000.0 * totalDeaths / Math.Max(1, totalAlive):0.000} per alive kilotick)");
    }
}

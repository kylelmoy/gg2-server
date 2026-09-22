using System;
using System.Collections.Generic;
using System.Linq;
using Gg2.Server;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// What a meshed round end does to the map rotation.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A round end decides the next map, and in a mesh it must decide once.</b>
/// The standalone server is guarded by <c>WorldState.MapChanging</c>, which
/// <c>Rounds.BeginMapChange</c> sets and the world keeps. In a mesh the node
/// owns the world and overwrites it every tick, so that flag never sticks and
/// the only thing saying "a change is already pending here" is this server's own
/// bookkeeping.
/// </para>
/// <para>
/// ⚠️ <b>The window is between SCHEDULING the change and APPLYING it.</b> A
/// meshed change is booked <c>MeshSession.CommandLead</c> ticks ahead, and the
/// world is not reset until it lands — so for those ticks the round is still
/// decided, and a guard that only asks whether the ten-second countdown is
/// running will let the round end a second time.
/// </para>
/// </remarks>
public sealed class MeshRotationTests
{
    private static readonly string[] Rotation =
        ["ctf_truefort", "ctf_orange", "ctf_2dfort", "ctf_conflict"];

    /// <summary>
    /// ⛔ Two consecutive round ends play two CONSECUTIVE maps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rotation is the observable, because the loaded map is not.</b> The
    /// chosen name travels to the peer inside the <c>ChangeMap</c> command and
    /// <c>DrainMeshCommands</c> puts it back into <c>_nextMap</c> before
    /// <c>PerformMapChange</c> reads it — so a second decision loads the right
    /// map anyway and is invisible at the first change. What it costs is a
    /// rotation entry, silently, on every round end: the server plays every
    /// other map and the log announces one it never loads.
    /// </para>
    /// <para>
    /// ⚠️ <b>Consecutive rather than named.</b> Asserting "ctf_orange then
    /// ctf_2dfort" would also pin where the rotation happens to start, which is
    /// <c>ServerOptions.Map</c>'s business and is tested elsewhere.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMeshedRoundEndTakesOneMapFromTheRotation()
    {
        using var listener = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
            options.Map = Rotation[0];
            options.MapRotation = Rotation;
            options.TimeLimitMinutes = 1;
        });

        using var joiner = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshConnect = $"127.0.0.1:{listener.Server.MeshPort}";
            options.Map = Rotation[0];
            options.MapRotation = Rotation;
            options.TimeLimitMinutes = 1;
        });

        // One clock for both, or two servers on their own timers fall thousands
        // of ticks apart.
        listener.AfterTick = () => joiner.Tick();

        var played = new List<string>();
        var current = listener.Server.CurrentMap;

        // Two round ends. A one-minute round is 1,800 ticks, plus the
        // ten-second countdown and the command lead.
        for (var i = 0; i < 6000 && played.Count < 2; i++)
        {
            listener.Tick();

            if (listener.Server.CurrentMap != current)
            {
                current = listener.Server.CurrentMap;
                played.Add(current);
            }
        }

        Assert.Equal(2, played.Count);

        // Both servers are on the same map at each step, which is what makes
        // the rotation question meaningful rather than a local accounting one.
        Assert.Equal(listener.Server.CurrentMap, joiner.Server.CurrentMap);

        var first = Array.IndexOf(Rotation, played[0]);

        Assert.True(first >= 0, $"played a map that is not in the rotation: {played[0]}");

        var expected = Rotation[(first + 1) % Rotation.Length];

        Assert.True(
            played[1] == expected,
            $"the rotation skipped an entry: played {string.Join(" -> ", played)}, "
                + $"but after {played[0]} the next entry is {expected}. A meshed "
                + "round end decided twice, and the second decision consumed a "
                + "rotation entry that is never played. See ServeRoundEnd's "
                + "mayDecide guard: the ten-second countdown restarts, so "
                + "_mapChangeCountdown < 0 is true again between the schedule "
                + "and the apply while the round is still decided.");
    }
}

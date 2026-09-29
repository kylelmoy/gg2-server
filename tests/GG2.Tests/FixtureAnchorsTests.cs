using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// The shared anchor derivation, on synthetic ticks small enough to see.
/// </summary>
/// <remarks>
/// The two closed fixtures gate the two happy paths against real recordings —
/// <c>closedsupport</c> is the all-slots-spawn case and <c>closedcombat</c> the
/// admitted-ghost case (its slot 6 is last observed 781 ticks before the
/// anchor). What no fixture reaches is the <b>refusal</b>: a slot observed
/// <em>after</em> the last first-spawn without ever spawning, which is what
/// makes a recording open-loop only. That branch is the one a wrong rewrite
/// would most likely lose, so it is pinned here.
/// </remarks>
public sealed class FixtureAnchorsTests
{
    private static TraceTick Tick(int index, int[] observedSlots, params (byte Slot, MessageType Type)[] events)
    {
        var slots = new TraceSlot[8];

        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = new TraceSlot
            {
                HasCharacter = Array.IndexOf(observedSlots, i) >= 0,
                Input = default,
                Observed = Array.IndexOf(observedSlots, i) >= 0
                    ? new CharacterBlock
                    {
                        Keys = PlayerKeys.None,
                        NetAimDirection = 0,
                        AimDistance = 0,
                        HasPosition = true,
                    }
                    : null,
            };
        }

        return new TraceTick
        {
            Index = index,
            Tier = MessageType.QuickUpdate,
            Slots = slots,
            Events = [.. events.Select(e => new ServerMessage
            {
                Type = e.Type,
                Payload = [e.Slot, 0, 0],
                PlayerId = e.Slot,
            })],
        };
    }

    [Fact]
    public void TheAnchorIsTheLatestFirstSpawnOverObservedSlots()
    {
        TraceTick[] ticks =
        [
            Tick(0, [], (1, MessageType.PlayerSpawn)),
            Tick(1, [1], (2, MessageType.PlayerSpawn)),
            Tick(2, [1, 2]),
            // A respawn does not move the anchor: only first spawns count.
            Tick(3, [1, 2], (1, MessageType.PlayerSpawn)),
        ];

        Assert.Equal(1, FixtureAnchors.LastExactAnchor(ticks));
    }

    /// <summary>
    /// A ghost the map reload removed — observed only before the anchor, never
    /// spawning — does not poison the recording. <c>closedcombat</c>'s slot 6.
    /// </summary>
    [Fact]
    public void AGhostThatDiesBeforeTheAnchorIsAdmitted()
    {
        TraceTick[] ticks =
        [
            Tick(0, [6]),
            Tick(1, [6], (1, MessageType.PlayerSpawn)),
            Tick(2, [1], (2, MessageType.PlayerSpawn)),
            Tick(3, [1, 2]),
        ];

        Assert.Equal(2, FixtureAnchors.LastExactAnchor(ticks));
    }

    /// <summary>
    /// A slot still holding a character after the last first-spawn, with no
    /// spawn of its own, can only be anchored from a quantised broadcast — the
    /// recording is open-loop only and the derivation must say so.
    /// </summary>
    [Fact]
    public void AGhostThatOutlivesTheAnchorRefusesTheRecording()
    {
        TraceTick[] ticks =
        [
            Tick(0, [6]),
            Tick(1, [6], (1, MessageType.PlayerSpawn)),
            Tick(2, [1, 6], (2, MessageType.PlayerSpawn)),
            Tick(3, [1, 2, 6]),
        ];

        Assert.Null(FixtureAnchors.LastExactAnchor(ticks));
    }

    [Fact]
    public void ARecordingWithNoSpawnsHasNoAnchor()
    {
        Assert.Null(FixtureAnchors.LastExactAnchor([Tick(0, [1]), Tick(1, [1])]));
    }
}

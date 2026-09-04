using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The <c>[Classlimits]</c> section reaching the simulation, per class.
/// </summary>
/// <remarks>
/// <para>
/// The enforcement itself — <c>checkClasslimits</c>, its quote-last-resort
/// quirk and its draw from the shared RNG — is <c>TeamBalanceTests</c>'s.
/// <see cref="ServerOptions"/> exposes per-class limits so a server can cap
/// each class independently rather than only uniformly or not at all.
/// </para>
/// <para>
/// <b>A limit is PER TEAM.</b> <c>countClassmembersExcept</c> counts one
/// team's members, so <c>medic=1</c> is one medic each and two on the map. This
/// is the misreading that makes a server look like it is not enforcing.
/// </para>
/// </remarks>
public sealed class ClassLimitConfigTests
{
    /// <summary>
    /// A per-class limit is enforced against the class it names, and only it.
    /// </summary>
    /// <remarks>
    /// End-to-end through <c>GameServer.AddBot</c>, which routes the requested
    /// class through <c>Classlimits.Pick</c> exactly as <c>botAdd.gml:26</c>
    /// does. <b>Asserting on the class handed BACK, not on the request.</b>
    /// The refusal is silent — a bot that asked for medic simply becomes
    /// something else — so a server that ignored the limit and one that applied
    /// it look identical from the request side.
    /// </remarks>
    [Fact]
    public void APerClassLimitBindsOnlyTheClassItNames()
    {
        using var harness = new ServerHarness(options =>
        {
            options.ClassLimits = ServerOptions.ClassLimitsBy((PlayerClass.Medic, 1));
        });

        var first = AddBot(harness, "medic one", Team.Red, PlayerClass.Medic);
        var second = AddBot(harness, "medic two", Team.Red, PlayerClass.Medic);

        Assert.Equal(PlayerClass.Medic, harness.Server.ClassOf(first));
        Assert.NotEqual(PlayerClass.Medic, harness.Server.ClassOf(second));

        // The other team is counted separately, so red being full says
        // nothing about blue. A reader that counted the whole server would fail
        // here and nowhere else.
        var blue = AddBot(harness, "medic three", Team.Blue, PlayerClass.Medic);
        Assert.Equal(PlayerClass.Medic, harness.Server.ClassOf(blue));

        // And a class with no limit is untouched by the medic one.
        var heavy = AddBot(harness, "heavy one", Team.Red, PlayerClass.Heavy);
        var heavyToo = AddBot(harness, "heavy two", Team.Red, PlayerClass.Heavy);
        Assert.Equal(PlayerClass.Heavy, harness.Server.ClassOf(heavy));
        Assert.Equal(PlayerClass.Heavy, harness.Server.ClassOf(heavyToo));
    }

    /// <summary>
    /// The single-scalar behaviour still works, because it is a real
    /// configuration and not only the old default.
    /// </summary>
    [Fact]
    public void OneLimitAcrossEveryClassStillCapsEveryClass()
    {
        using var harness = new ServerHarness(options =>
        {
            var limits = new byte[10];
            Array.Fill(limits, (byte)1);
            options.ClassLimits = limits;
        });

        var first = AddBot(harness, "one", Team.Red, PlayerClass.Scout);
        var second = AddBot(harness, "two", Team.Red, PlayerClass.Scout);

        Assert.Equal(PlayerClass.Scout, harness.Server.ClassOf(first));
        Assert.NotEqual(PlayerClass.Scout, harness.Server.ClassOf(second));
    }

    /// <summary>
    /// <c>GameServerCreate.gml:42-46</c>: a limit at or above the player limit
    /// is stored as 255.
    /// </summary>
    /// <remarks>
    /// <b>Both the enforcement and the wire must see the clamped value.</b>
    /// The reference mutates <c>global.classlimits</c> in place before its first
    /// <c>serializeState</c>, so a client's HUD and the server's own check are
    /// the same numbers. Clamping in one place only would put a limit on the HUD
    /// that the server does not apply, or apply one the client does not show,
    /// and neither shows up as an error anywhere — which is why
    /// <see cref="ServerOptions.EffectiveClassLimits"/> exists and both callers
    /// use it.
    /// </remarks>
    [Fact]
    public void ALimitThatCannotBindIsStoredAsUnlimited()
    {
        var options = new ServerOptions
        {
            PlayerLimit = 10,
            MapDirectory = "maps",
            ClassLimits = ServerOptions.ClassLimitsBy(
                (PlayerClass.Medic, 10),   // == the player limit
                (PlayerClass.Spy, 11),     // above it
                (PlayerClass.Scout, 2)),   // below it, and stays
        };

        var effective = options.EffectiveClassLimits();

        Assert.Equal(255, effective[(int)PlayerClass.Medic]);
        Assert.Equal(255, effective[(int)PlayerClass.Spy]);
        Assert.Equal(2, effective[(int)PlayerClass.Scout]);

        // Non-destructive: the configured array is what the operator wrote and
        // is not rewritten under them, unlike the reference's in-place mutation.
        Assert.Equal(10, options.ClassLimits[(int)PlayerClass.Medic]);
    }

    /// <summary>
    /// A <see cref="ServerOptions.ClassLimits"/> of the wrong length is
    /// refused at startup rather than indexed out of bounds mid-tick.
    /// </summary>
    [Fact]
    public void AClassLimitsArrayOfTheWrongLengthIsRefused()
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            ClassLimits = [1, 2, 3],
        };

        var thrown = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("ClassLimits", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default is ten 255s, and a bare <c>new byte[10]</c> is not it.
    /// </summary>
    /// <remarks>
    /// A zeroed array means every class is FULL, and <c>Classlimits.Pick</c>'s
    /// answer to "nothing is free" is to hand out the requested class anyway —
    /// so the mistake produces a server that looks exactly like an unlimited one
    /// until a class fills up. Same shape as <c>CharacterState.Fresh</c>.
    /// </remarks>
    [Fact]
    public void TheDefaultIsUnlimitedRatherThanZeroed()
    {
        Assert.All(new ServerOptions().ClassLimits, limit => Assert.Equal(255, limit));
        Assert.All(ServerOptions.NoClassLimits(), limit => Assert.Equal(255, limit));

        var named = ServerOptions.ClassLimitsBy((PlayerClass.Sniper, 0));
        Assert.Equal(0, named[(int)PlayerClass.Sniper]);
        Assert.Equal(255, named[(int)PlayerClass.Scout]);
    }

    private static int AddBot(ServerHarness harness, string name, Team team, PlayerClass playerClass) =>
        harness.Server.AddBot(name, team, playerClass, new NullInputSource());

    /// <summary>An input source that never presses anything.</summary>
    private sealed class NullInputSource : IInputSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) => default;
    }
}

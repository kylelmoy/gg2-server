using System.Runtime.CompilerServices;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// A fixed-capacity inline buffer, one entry per roster slot.
/// </summary>
/// <remarks>
/// <see cref="InlineArrayAttribute"/> rather than <c>T[]</c> on purpose. An
/// array field would make <see cref="WorldState"/> a handle to memory it does
/// not own: copying the state would alias the characters, and docs/BOTAI.md
/// item 1 — the one item it says cannot be retrofitted — asks that cloning a
/// world be a <c>memcpy</c>. Inlining the storage makes the assignment operator
/// the clone.
/// </remarks>
[InlineArray(WorldState.MaxSlots)]
public struct SlotBuffer<T>
{
    private T _element0;
}

/// <summary>
/// A fixed-capacity inline buffer, one entry per control point.
/// </summary>
/// <remarks>
/// Inline for the same reason <see cref="SlotBuffer{T}"/> is. Five is the most
/// any shipped map places (<c>controlPoint1</c> to <c>controlPoint5</c>); eight
/// leaves room without making a clone measurably bigger.
/// </remarks>
[InlineArray(WorldState.MaxControlPoints)]
public struct ControlPointBuffer<T>
{
    private T _element0;
}

/// <summary>
/// A fixed-capacity inline buffer, one entry per projectile pool slot.
/// </summary>
/// <remarks>
/// Separate from <see cref="SlotBuffer{T}"/> only because it is a different
/// length; the reason it is inline is the same one.
/// </remarks>
[InlineArray(WorldState.MaxProjectiles)]
public struct ProjectileBuffer<T>
{
    private T _element0;
}

/// <summary>
/// A fixed-capacity inline buffer, one entry per moving platform.
/// </summary>
/// <remarks>
/// Inline for the same reason the others are. Sixteen because no shipped map
/// places a moving platform at all, so there is no measured maximum to size
/// against — see <see cref="WorldState.MaxMovingPlatforms"/>.
/// </remarks>
[InlineArray(WorldState.MaxMovingPlatforms)]
public struct MovingPlatformBuffer<T>
{
    private T _element0;
}

/// <summary>
/// Everything one match's simulation can read or write, and nothing else.
/// </summary>
/// <remarks>
/// <b>A value type, deliberately, and with no reference in it anywhere.</b>
/// docs/BOTAI.md item 1: rollout search forks the world, plays a candidate
/// action sequence forward, scores it and throws it away, thousands of times per
/// decision — so a clone has to be a copy of bytes, not a graph walk. That rules
/// out a field pointing at a map, a config object, a logger or an event sink.
/// Read-only inputs are passed <em>to</em> <see cref="MatchSimulation.Tick"/>
/// alongside the state (the <see cref="CollisionWorld"/> is exactly that), never
/// held by it.
/// <para>
/// It also makes docs/BOTAI.md item 2 nearly free: a flat state is a
/// serialisable one.
/// </para>
/// <para>
/// <b>Not the whole match yet.</b> Characters, their weapons, their sentries and
/// the projectiles in the air — which is phases 4 and 5. Objectives, scores and
/// the HUD still live in <c>Gg2.Server.MatchState</c>; they belong here as the
/// phases that own them land, and <c>MatchState</c> should shrink as they do
/// rather than the other way round.
/// </para>
/// </remarks>
public struct WorldState
{
    /// <summary>
    /// Roster slots this state has room for.
    /// </summary>
    /// <remarks>
    /// The reference clamps network clients to 48 and neither slot 0 nor a bot
    /// counts against that (<c>getNumberOfOccupiedSlots</c>), so 64 leaves room
    /// for a full server plus its bots. A roster index is a <c>ubyte</c> on the
    /// wire, so 256 is the format's own ceiling if this ever needs raising —
    /// the cost is only the size of a clone.
    /// </remarks>
    public const int MaxSlots = 64;

    /// <summary>
    /// Projectiles that can be in the air at once, across the whole match.
    /// </summary>
    /// <remarks>
    /// <b>The reference has no such limit and this is the one place the port
    /// can lose to it.</b> GM8 creates instances until it runs out of memory;
    /// here a pool full means <see cref="SpawnProjectile"/> hands back
    /// <see cref="ProjectileHandle.None"/> and a shot that the reference would
    /// have fired does not exist. That is a divergence, not a graceful
    /// degradation, so the number is chosen to be unreachable rather than
    /// merely comfortable: a full 64-slot server of heavies firing every other
    /// tick with a 40-tick shot lifetime is about 640 live shots on paper, and
    /// no real server is 64 heavies. Raise it before trimming it.
    /// <para>
    /// The cost is paid on every clone, which is why it is not larger still:
    /// each slot is a <see cref="ProjectileState"/> plus a generation plus an
    /// order entry, and at this capacity the pool is about 43 KB of the whole
    /// state's 66 KB. <c>WorldStateTests.ACloneStaysWithinItsBudget</c> is the
    /// tripwire on that number.
    /// </para>
    /// <para>
    /// <b>The pool is now the majority of a clone, and that changes where to
    /// look for savings.</b> Stage 3 grew a projectile from 48 bytes to 80 —
    /// an <c>image_angle</c>, a lifetime, a damage figure and a perseverance
    /// flag — which put two thirds of the state into a structure that is almost
    /// always mostly empty. Before raising the capacity again, the thing to
    /// weigh is a smaller <see cref="ProjectileState"/> rather than a smaller
    /// pool: the reference has no ceiling, and a shot that does not exist is a
    /// divergence.
    /// </para>
    /// </remarks>
    public const int MaxProjectiles = 512;

    /// <summary>Each slot's character. Only meaningful where <see cref="Alive"/> is set.</summary>
    public SlotBuffer<CharacterState> Characters;

    /// <summary>
    /// GM8's instance counter: the next value for
    /// <see cref="CharacterState.CreationOrder"/> and
    /// <see cref="SentryState.CreationOrder"/>.
    /// </summary>
    /// <remarks>
    /// <b>One counter for both, because GM8 has one.</b> An instance id comes
    /// from a single sequence whatever object is being created, so a sentry
    /// built between two spawns sits between them. Nothing in the port compares
    /// a character against a sentry today — a collision event runs over one
    /// object at a time — but sharing the counter costs nothing and keeps the
    /// concept honest.
    /// <para>
    /// In the state rather than static for the same reason the RNG is: a forked
    /// rollout has to fork it, and a default <see cref="WorldState"/> has to be
    /// reproducible. It is never broadcast — only the <em>order</em> it induces
    /// is observable, and that reaches the wire as which character a rocket
    /// exploded on.
    /// </para>
    /// </remarks>
    public uint NextInstanceOrder;

    /// <summary>Whether a slot has a character at all — spectators and the dead do not.</summary>
    public SlotBuffer<bool> Alive;

    /// <summary>
    /// The player behind each slot — the state that survives its character.
    /// Only meaningful where <see cref="HasPlayer"/> is set.
    /// </summary>
    /// <remarks>
    /// Phase 6: team, class and the respawn timer live here rather than at the
    /// server seam, so the simulation can respawn a character and guard a
    /// sentry on its owner's class without asking the roster.
    /// </remarks>
    public SlotBuffer<PlayerState> Players;

    /// <summary>Whether a slot holds a connected player. Slot 0 always does on a live server.</summary>
    public SlotBuffer<bool> HasPlayer;

    /// <summary>
    /// <c>player.dominationKills</c> for every player at once:
    /// <c>[killer][victim]</c> is how many times that killer has killed that
    /// victim without dying to them since.
    /// </summary>
    /// <remarks>
    /// A square matrix rather than a map per player, because the state has to
    /// stay a value type — the reference's <c>ds_map</c> is a handle and an
    /// absent key reads as zero, which a zeroed row gives for free.
    /// <para>
    /// ⚠️ <b>Both axes are roster indices, so <see cref="RemoveSlot"/> has to
    /// renumber both.</b> It is the only two-dimensional thing in the state and
    /// therefore the only one where the usual one-dimensional renumbering is not
    /// enough.
    /// </para>
    /// <para>
    /// 4 KB of the clone. See <see cref="Dominations"/> for why it is simulation
    /// rather than a scoreboard detail.
    /// </para>
    /// </remarks>
    public SlotBuffer<SlotBuffer<byte>> DominationKills;

    /// <summary>
    /// <c>global.Server_Respawntime</c>, in ticks — what a death arms
    /// <see cref="PlayerState.RespawnTimer"/> to.
    /// </summary>
    /// <remarks>
    /// Match configuration rather than tick state, but it lives in the state
    /// for the same reason the RNG does: <c>Tick</c> stays a pure function of
    /// its arguments. <b>0 disables death-armed respawn entirely</b> — the
    /// default, and what the parity replayer needs: a recording carries the
    /// reference's own <c>PLAYER_SPAWN</c>s (with the reference's random spawn
    /// point picks inside them), so a replay injects those and must not race
    /// them with picks of its own. <c>game_init.gml</c> floors the reference's
    /// value at one tick, so 0 is unreachable there and free to mean "off"
    /// here.
    /// </remarks>
    public int RespawnTime;

    /// <summary>
    /// Each slot's weapon. Only meaningful where <see cref="Alive"/> is set.
    /// </summary>
    /// <remarks>
    /// Beside the character rather than inside it because that is what it is:
    /// the reference's <c>currentWeapon</c> is a separate instance created and
    /// destroyed with its owner, and keeping it a separate struct means the
    /// character's own layout does not change when a weapon gains a field.
    /// </remarks>
    public SlotBuffer<WeaponState> Weapons;

    /// <summary>Each slot's sentry. Only meaningful where <see cref="HasSentry"/> is set.</summary>
    public SlotBuffer<SentryState> Sentries;

    /// <summary>
    /// Whether a slot has a sentry built or building.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="Alive"/> on purpose: a sentry outlives its
    /// engineer's character and keeps shooting while they respawn.
    /// </remarks>
    public SlotBuffer<bool> HasSentry;

    /// <summary>
    /// The projectile pool. Sparse: a slot is live only where
    /// <see cref="ProjectileState.Kind"/> is not <see cref="ProjectileKind.None"/>,
    /// and <see cref="ProjectileOrder"/> says which those are.
    /// </summary>
    public ProjectileBuffer<ProjectileState> Projectiles;

    /// <summary>
    /// How many times each pool slot has been handed out. Bumped on every
    /// spawn, so a <see cref="ProjectileHandle"/> into a recycled slot fails to
    /// resolve instead of naming the projectile that took its place.
    /// </summary>
    public ProjectileBuffer<uint> ProjectileGenerations;

    /// <summary>
    /// The live pool slots, dense in <c>[0, <see cref="LiveProjectileCount"/>)</c>
    /// and <b>in creation order</b>.
    /// </summary>
    /// <remarks>
    /// <b>The order is the point of this array.</b> docs/SIMULATION.md: "within
    /// a phase, instances run in creation order", and that is observable
    /// wherever projectiles interact — two rockets reaching the same character
    /// on the same tick, a mine detonated by a blast that also kills its owner.
    /// Pool slots are recycled lowest-index-first, so walking
    /// <see cref="Projectiles"/> directly would walk them in some order that has
    /// nothing to do with when they were fired. Walk this instead.
    /// <para>
    /// docs/SIMULATION.md's remaining open question — instance ordering under
    /// heavy churn — is a question about what creation order <em>means</em> when
    /// things are created and destroyed in the same tick. This structure can
    /// answer it either way; it does not decide it.
    /// </para>
    /// </remarks>
    public ProjectileBuffer<ushort> ProjectileOrder;

    /// <summary>How many projectiles are in the air.</summary>
    public int LiveProjectileCount;

    // --- the objective -------------------------------------------------------

    /// <summary>
    /// The red team's briefcase, and its base. Inert on a map with no intel.
    /// </summary>
    /// <remarks>
    /// Placed by <see cref="Intel.Place"/> when the map loads. Two of these
    /// rather than a two-entry buffer because <see cref="Team"/> has four
    /// members and only two of them can own one; <see cref="IntelOf"/> is the
    /// indexed view where a caller has a team in hand.
    /// </remarks>
    public IntelState RedIntel;

    /// <inheritdoc cref="RedIntel"/>
    public IntelState BlueIntel;

    /// <summary>
    /// <c>global.redCaps</c>: how many times blue's briefcase has reached red's
    /// base.
    /// </summary>
    /// <remarks>
    /// An <c>int</c> rather than the wire's <c>ubyte</c> because the reference
    /// counts in a real and <c>write_ubyte</c> clamps on the way out; the round
    /// ends at <c>global.capLimit</c> long before 255 anyway, once the round
    /// ending is simulated at all.
    /// </remarks>
    public int RedCaps;

    /// <inheritdoc cref="RedCaps"/>
    public int BlueCaps;

    /// <summary>
    /// The red team's generator. Inert on a map with none.
    /// </summary>
    /// <remarks>
    /// Placed by <see cref="Generators.Place"/> when the map loads, and two
    /// fields rather than a buffer for the reason the briefcases are: only two
    /// of <see cref="Team"/>'s four members can own one.
    /// <see cref="Generators.Of"/> is the indexed view.
    /// </remarks>
    public GeneratorState RedGenerator;

    /// <inheritdoc cref="RedGenerator"/>
    public GeneratorState BlueGenerator;

    /// <summary>Control points this state has room for. Five is the most a map places.</summary>
    public const int MaxControlPoints = 8;

    /// <summary>
    /// The map's moving platforms, in level-data order. Only the first
    /// <see cref="MovingPlatformCount"/> are real.
    /// </summary>
    /// <remarks>
    /// Their fixed configuration is <em>not</em> here — it is a
    /// <see cref="MovingPlatformDefinition"/> passed to the tick beside the
    /// <see cref="CollisionWorld"/>. What a platform carries per tick is only the
    /// three values that go on the wire.
    /// </remarks>
    public MovingPlatformBuffer<MovingPlatformState> MovingPlatforms;

    /// <summary>How many of <see cref="MovingPlatforms"/> the map placed.</summary>
    public int MovingPlatformCount;

    /// <summary>
    /// Moving platforms this state has room for.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>No shipped map places one</b>, so this ceiling has never been
    /// approached and 16 is a guess rather than a measurement. It is the same
    /// class of ceiling as <see cref="MaxProjectiles"/>: overrunning it silently
    /// drops platforms a custom map placed, which is a divergence rather than
    /// graceful degradation. <see cref="MovingPlatforms.Reset"/> is where it
    /// clamps, and raising the number is the fix.
    /// </remarks>
    public const int MaxMovingPlatforms = 16;

    // --- the round -----------------------------------------------------------

    /// <summary>
    /// Which gamemode's rules the round runs under, from the map.
    /// </summary>
    /// <remarks>
    /// Decided by <c>Gg2.Maps.GameModes.Of</c> at load and constant while a map
    /// is loaded. A zeroed state says <see cref="GameMode.CaptureTheFlag"/>,
    /// which is harmless only because <see cref="CapLimit"/> and
    /// <see cref="TimeLimitMinutes"/> both default to "off"; see them.
    /// </remarks>
    public GameMode GameMode;

    /// <summary>
    /// <c>global.timeLimitMins</c>. On the wire in six of the eight modes'
    /// HUD blocks, and what <see cref="RoundTimer"/> was started from.
    /// </summary>
    /// <remarks>
    /// <b>0 means there is no time limit</b>, the same escape
    /// <see cref="RespawnTime"/> uses and for the same reason:
    /// <c>game_init.gml:96</c> is
    /// <c>max(1, min(255, ini_read_real(...)))</c>, so 0 is unreachable in the
    /// reference and free to mean "off" here. It is the default because a
    /// zeroed <see cref="RoundTimer"/> would otherwise read as a round that
    /// ended before it started — which is what a parity replay, a unit test and
    /// a bot rollout would all get.
    /// </remarks>
    public int TimeLimitMinutes;

    /// <summary>
    /// <c>CTFHUD.timer</c>: ticks left in the round. Counts down to 0 and stops.
    /// </summary>
    public int RoundTimer;

    /// <summary>
    /// <c>global.caplimit</c>: captures that end the round.
    /// </summary>
    /// <remarks>
    /// <b>0 means there is no cap limit</b> — see <see cref="TimeLimitMinutes"/>,
    /// which is floored the same way (<c>game_init.gml:101</c>).
    /// </remarks>
    public int CapLimit;

    /// <summary>
    /// Whether the round has been decided. False until it is.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A flag rather than a sentinel in <see cref="Winner"/>, on
    /// purpose.</b> The reference spells "nobody has won" as
    /// <c>global.winners = -1</c>, and <see cref="Team"/> has no -1 —
    /// <c>Team.Red</c> is <b>0</b>, so a zeroed state with a plain
    /// <c>Winner</c> field would say red had already won the match. The
    /// slot-0 trap in its fourth costume. <c>GameServer</c> reconstitutes the
    /// -1 on the way to the wire.
    /// </remarks>
    public bool RoundDecided;

    /// <summary>
    /// <c>global.killLimit</c>: kills that end a Team Deathmatch round.
    /// </summary>
    /// <remarks>
    /// <b>0 means there is no kill limit</b> — the same escape
    /// <see cref="CapLimit"/> and <see cref="TimeLimitMinutes"/> use, and for
    /// the same reason: <c>game_init.gml:103</c> is
    /// <c>max(1, min(65535, ...))</c>, so 0 is unreachable in the reference.
    /// <para>
    /// ⚠️ <b>The kills themselves are counted in <see cref="RedCaps"/> and
    /// <see cref="BlueCaps"/></b>, not in a field of their own. Team Deathmatch
    /// reuses the capture counters — <c>doEventPlayerDeath.gml:313</c> — which
    /// is why it needs no per-player statistics to decide a round, and why its
    /// HUD draws them where CTF draws captures.
    /// </para>
    /// </remarks>
    public int KillLimit;

    /// <summary>
    /// <c>global.tdmInvulnerabilityTicks</c>: what a Team Deathmatch spawn seeds
    /// <see cref="CharacterState.DeathmatchInvulnerable"/> to.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A fifth of the ticks it buys</b> —
    /// <c>seconds * 30 * 0.2</c>, spent at 0.2 a tick. See
    /// <see cref="CharacterState.DeathmatchInvulnerable"/>.
    /// <para>
    /// In the state rather than at the server seam because the spawn reads it,
    /// and a spawn is <see cref="MatchSimulation.Tick"/>'s business. It also
    /// reaches the wire, on a <c>FULL_UPDATE</c> only, so that a client can draw
    /// the shrinking bubble.
    /// </para>
    /// </remarks>
    public int TdmInvulnerabilityTicks;

    /// <summary>
    /// <c>global.autobalance</c>: whether the server moves players across when
    /// the teams are two apart.
    /// </summary>
    /// <remarks>
    /// <b>Off by default here and on in the reference</b>, which is the same
    /// choice <see cref="RespawnTime"/> makes and for the same reason: a unit
    /// test, a parity replay and a bot rollout must not have players moved
    /// between teams behind their backs. <c>GameServer</c> turns it on from the
    /// ini, where <c>game_init.gml:105</c> defaults it to 1.
    /// </remarks>
    public bool Autobalance;

    /// <summary>
    /// <c>GameServer.serverbalance</c>: a balance notice is outstanding and the
    /// fuse is burning.
    /// </summary>
    /// <remarks>
    /// A bool rather than the reference's 0/1 real, which never holds any other
    /// value. See <see cref="TeamBalance"/>.
    /// </remarks>
    public bool BalanceNoticeGiven;

    /// <summary>
    /// <c>GameServer.balancecounter</c>: ticks since the notice went out.
    /// </summary>
    /// <remarks>
    /// ⚠️ Counted every tick while <see cref="BalanceNoticeGiven"/>, but only
    /// <em>read</em> when something calls <see cref="TeamBalance.Check"/> — a
    /// death, a departure or a team change.
    /// </remarks>
    public int BalanceCounter;

    /// <summary>
    /// <c>global.classlimits</c>: how many of each class one team may field.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A zeroed state means every class is full</b>, which would send
    /// every team change through <c>checkClasslimits</c>' random pick. The
    /// reference's default is 255 and <c>GameServer</c> sets it; a test that
    /// builds a world by hand and cares about classes has to as well. See
    /// <see cref="Classlimits.Pick"/>, which is a no-op at 255.
    /// </remarks>
    public ClassLimitBuffer ClassLimits;

    /// <summary>
    /// Who won, where <see cref="RoundDecided"/>. <see cref="Team.Spectator"/>
    /// is the reference's spelling of a draw and goes out as a 2.
    /// </summary>
    public Team Winner;

    /// <summary>
    /// <c>CTFHUD.overtime</c>: the clock is out and the round is still open,
    /// because a briefcase is away from its base in a drawn match.
    /// </summary>
    /// <remarks>
    /// Display-only in the reference — <c>CTFHUD.events/Draw.xml</c> is the one
    /// reader and it is not serialised. Carried because it is a fact about the
    /// round the simulation already knows, and a bot has as much right to it as
    /// a player looking at the screen.
    /// </remarks>
    public bool Overtime;

    /// <summary>
    /// <c>global.mapchanging</c>: the round is over and the win is on screen.
    /// </summary>
    /// <remarks>
    /// Ten seconds during which every gate opens, nobody may hold a briefcase,
    /// everybody has full ammo and nobody respawns. See
    /// <see cref="Rounds.BeginMapChange"/> for the whole list and where each
    /// clause lives.
    /// </remarks>
    public bool MapChanging;

    // --- control points ------------------------------------------------------

    /// <summary>
    /// The map's control points, in the order the HUD block serialises them.
    /// Only the first <see cref="ControlPointCount"/> are real.
    /// </summary>
    /// <remarks>
    /// ⚠️ A zeroed entry is owned by red and being captured by red — see
    /// <see cref="ControlPoints.Neutral"/>, which is the factory.
    /// </remarks>
    public ControlPointBuffer<ControlPointState> ControlPoints;

    /// <summary>How many of <see cref="ControlPoints"/> the map placed.</summary>
    public int ControlPointCount;

    /// <summary>
    /// Whether this is the attack/defend arrangement of the control-point mode
    /// rather than the symmetrical one.
    /// </summary>
    /// <remarks>
    /// <c>ControlPointHUD.events/Create.xml</c> decides it from the map:
    /// <c>if instance_exists(ControlPointSetupGate)</c>. One mode enum covers
    /// both because the wire block is the same shape; almost everything else
    /// about them differs — who starts owning what, how long each point takes,
    /// and who wins.
    /// </remarks>
    public bool AttackDefend;

    /// <summary>
    /// <c>global.setupTimer</c>: ticks of setup left before the gates open.
    /// </summary>
    /// <remarks>
    /// 1,800 in Invasion and 900 in attack/defend, zero everywhere else. While
    /// it is above zero <c>areSetupGatesClosed()</c> is true and every
    /// <c>ControlPointSetupGate</c> on the map is solid — to characters and
    /// bullets alike, the one solidity rule both lists agree on.
    /// <para>
    /// ⚠️ At <b>30</b>, not 0, the round clock is reset to the full time limit:
    /// the match proper starts a second before the gates actually open.
    /// </para>
    /// </remarks>
    public int SetupTimer;

    // --- arena ---------------------------------------------------------------

    /// <summary>Which phase of an Arena map the match is in.</summary>
    /// <remarks>
    /// A zeroed state says <see cref="ArenaState.Waiting"/>, which is the
    /// reference's own starting value — the one enum in this file whose default
    /// happens to be right.
    /// </remarks>
    public ArenaState ArenaState;

    /// <summary>Rounds each team has won on this map.</summary>
    public int ArenaRedWins;

    /// <inheritdoc cref="ArenaRedWins"/>
    public int ArenaBlueWins;

    /// <summary>
    /// <c>endCount</c>: ticks of scoreboard left after a round.
    /// </summary>
    public int ArenaEndCount;

    /// <summary>
    /// <c>roundStart</c>: ticks of setup left before a round begins.
    /// </summary>
    /// <remarks>
    /// ⚠️ On the wire it is <c>floor(roundStart/2)</c> in a single byte, so the
    /// client sees it to the nearest two ticks. 300 fits in a byte; the halving
    /// is the reference's and is not a rounding this port gets to choose.
    /// </remarks>
    public int ArenaRoundStart;

    /// <summary>
    /// <c>ArenaHUD.winners</c>: who won the <em>round</em>, not the map.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not <see cref="Winner"/>, which is <c>global.winners</c> and ends the
    /// map. This one is <see cref="Team.Spectator"/> between rounds and is sent
    /// as a signed byte — where the reference's -1 never appears, because it is
    /// initialised to <c>TEAM_SPECTATOR</c> rather than to -1.
    /// </remarks>
    public Team ArenaWinner;

    /// <summary>
    /// The filter every projectile and hitscan collides with —
    /// <c>gunSetSolids</c>'s list, with the two world flags that change it.
    /// </summary>
    /// <remarks>
    /// One construction site rather than sixteen. Both flags are easy to forget
    /// at a call site and each is silent when missed: a bullet would pass a
    /// closed setup gate, or stop at a team gate the victory screen has opened.
    /// </remarks>
    public readonly CollisionFilter BulletFilter =>
        CollisionFilter.ForBullet(SetupTimer > 0, MapChanging);

    /// <summary>
    /// <c>KothHUD.cpUnlock</c>: ticks of pregame before the point can be taken.
    /// </summary>
    /// <remarks>
    /// 900 — thirty seconds — from <c>KothHUD.events/Create.xml</c>, counted
    /// down every tick and, at zero, unlocking every point at once
    /// (<c>doEventUnlockCP</c>). On the wire in the KotH and DKotH HUD blocks.
    /// </remarks>
    public int ControlPointUnlock;

    /// <summary>
    /// <c>KothHUD.redTimer</c>: ticks red must hold the hill for. Starts at
    /// 5,400 — three minutes.
    /// </summary>
    public int RedTimer;

    /// <inheritdoc cref="RedTimer"/>
    public int BlueTimer;

    /// <summary>
    /// <c>GameServer.syncTimer = 1</c>: something the scoreboard shows has moved
    /// far enough that clients are owed an extra <c>CAPS_UPDATE</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Bandwidth, not correctness — and it is still on the wire.</b> The
    /// reference asks for one when a point is captured, when a capture meter
    /// crosses three quarters, a quarter or a tenth of the way
    /// (<c>ControlPoint.events/Step.xml:217</c>), and five seconds before the
    /// pregame lock comes off. A client that missed them all would still be
    /// right, only laggier — but the extra messages are bytes a byte-diff sees,
    /// so they are transcribed.
    /// <para>
    /// Cleared by the server when it sends the message. The intel's capture
    /// reaches the same flag through <c>TickEvents.ScoredIntel</c>, because that
    /// one is already an event.
    /// </para>
    /// </remarks>
    public bool ScoreboardSyncRequested;

    /// <summary>
    /// Whether the pregame lock came off during the tick just run —
    /// <c>UNLOCKCP</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>In the state rather than in <see cref="TickEvents"/>, and that is
    /// deliberate.</b> Every other broadcast the simulation produces is an
    /// event; this one is a fact about the world that happens to be worth one
    /// message, and a client that misses it recovers from the <c>cpUnlock</c> in
    /// the next <c>CAPS_UPDATE</c> (<c>KothHUD.events/User Event 13.xml</c>
    /// unlocks the point itself when it reads a zero). Cleared by the server
    /// when it sends the message, and by a map change.
    /// </remarks>
    public bool UnlockedControlPointsThisTick;

    // One team's briefcase, by team, is Intel.Of(ref state, team) — a static on
    // Intel rather than a method here, because C# will not let a struct return
    // one of its own fields by reference (CS8170) and copying an IntelState out
    // and back at every call site is exactly the kind of silent aliasing bug
    // this file exists to avoid.

    /// <summary>
    /// The match's own random stream — GM8's global one, as the host uses it.
    /// </summary>
    /// <remarks>
    /// <b>In the state rather than beside it, and it is four bytes.</b> A weapon
    /// draws <c>irandom(65535)</c> from the global stream and broadcasts the
    /// result (<c>Scattergun.events/User Event 1.xml</c>), so the tick needs a
    /// source of seeds. Keeping it here rather than in a field of the server
    /// means <see cref="MatchSimulation.Tick"/> stays a pure function of state
    /// and inputs, and a bot rollout that forks the world forks the stream with
    /// it — otherwise two rollouts from the same state would diverge for a
    /// reason that has nothing to do with the actions being compared
    /// (docs/BOTAI.md items 1 and 2).
    /// <para>
    /// <b>Which seeds come out is free.</b> docs/SIMULATION.md's "server-side
    /// RNG is free" covers the <em>draw</em>: no client can predict it and every
    /// client is told the answer. What is not free is what the seed is then used
    /// for, which is <see cref="Gg2.Protocol.GmlRandom"/>'s business.
    /// </para>
    /// <para>
    /// A zeroed world therefore starts at seed 0, which is a legal GM8 state
    /// (<c>random_set_seed(0)</c>) and makes a default state fully
    /// deterministic.
    /// </para>
    /// </remarks>
    public GmlRandom Random;

    /// <summary>Whether the given slot currently has a character.</summary>
    public readonly bool IsAlive(int slot) => (uint)slot < MaxSlots && Alive[slot];

    /// <summary>
    /// A player joined: a fresh spectator scout with the respawn alarm at rest.
    /// </summary>
    /// <remarks>
    /// Through <see cref="PlayerState.Fresh"/>, never a zeroed struct — a
    /// defaulted player is a red scout ready to respawn, which is the slot-0
    /// trap wearing yet another hat.
    /// </remarks>
    public void AddPlayer(int slot)
    {
        if ((uint)slot >= MaxSlots)
            return;

        Players[slot] = PlayerState.Fresh;
        HasPlayer[slot] = true;
    }

    /// <summary>Removes a slot's character: it died, left, or went spectator.</summary>
    /// <remarks>
    /// The character is cleared as well as flagged, so a stale body cannot leak
    /// into the next thing that spawns in this slot and so two states that look
    /// the same really are the same bytes — which is what makes docs/BOTAI.md
    /// item 2's "restore to a bit-identical state" checkable.
    /// <para>
    /// <b>The weapon goes and the sentry stays.</b> That is the reference: a
    /// weapon is created by <c>Character.events/Create.xml</c> and dies with it,
    /// while a sentry belongs to the <em>player</em> and keeps firing while its
    /// engineer respawns. Projectiles already in the air also stay, and keep
    /// naming this slot — their <c>ownerPlayer</c> is a player and not a
    /// character, which is what lets a rocket still credit a kill to somebody
    /// who died before it landed.
    /// </para>
    /// </remarks>
    public void Clear(int slot)
    {
        if ((uint)slot >= MaxSlots)
            return;

        // Minegun.events/Destroy.xml: `with(Mine) if ownerPlayer ==
        // other.ownerPlayer instance_destroy();`
        //
        // ⚠️ The one exception to "a projectile outlives its owner". A weapon is
        // destroyed with its character, and the Minegun takes its mines with it
        // — silently, without exploding them. A rocket already in the air does
        // not care that its shooter died; a mine does. Before the fields are
        // cleared, so the decrement above still finds the weapon.
        for (var i = LiveProjectileCount - 1; i >= 0; i--)
        {
            var handle = LiveProjectileHandle(i);
            ref var projectile = ref Projectiles[handle.Index];

            // Blade.events/Destroy.xml does the same for both of Quote's, in
            // two `with` blocks: `with (BladeB) if (ownerPlayer ==
            // other.ownerPlayer) instance_destroy();` and the same for Bubble.
            // So a Quote who dies, changes class or disconnects takes their
            // whole screen of bubbles and the blade in flight with them —
            // the Minegun's rule, applied to two more projectiles.
            if (projectile.OwnerSlot != slot)
                continue;

            // Revolver.events/Destroy.xml is the same again for the knife:
            // `with (StabMask) if (ownerPlayer == other.ownerPlayer)
            // instance_destroy();`. Its StabAnim block needs nothing here —
            // the freeze is a field on the character and is zeroed below.
            if (projectile.Kind is ProjectileKind.Mine or ProjectileKind.Blade
                or ProjectileKind.Bubble or ProjectileKind.Stab)
            {
                FreeProjectile(handle);
            }
        }

        // Character.events/Destroy.xml: `if (intel) doEventDropIntel(player);`
        //
        // ⚠️ Every way a character stops existing drops the flag, and none of
        // them broadcasts a DROP_INTEL. sendEventDropIntel's own comment says
        // why: the drop would be a nested event inside the death, and the
        // client would process the death first and then error out dropping a
        // flag for a player it has already destroyed. Clients infer it from the
        // PLAYER_DEATH — which is exactly why this belongs here, in the shared
        // Destroy path, rather than in Deaths.Kill: a class or team change
        // destroys a character too (PlayerCommands.BidFarewell), and so does a
        // recorded PLAYER_CHANGETEAM in the parity harness.
        //
        // Before the fields are cleared, so the carrier's position and its
        // charged intelRecharge are still readable.
        //
        // ⚠️ The `lastDamageSource in (KILL_BOX, FRAG_BOX, PITFALL)` arm
        // below it — which sends the flag home instead of leaving it in the pit
        // — is **not here**, and that is deliberate: it is *broadcast* where this
        // drop is not, so it lives in Deaths.Kill, which has the events. Only a
        // death can carry one of those three sources, because a box zeroes hit
        // points the moment it is touched. See KillBoxes.
        Intel.Drop(ref this, slot);

        // Medigun.events/Destroy.xml: `if(instance_exists(healTarget)) if
        // (healTarget.object != -1) healTarget.object.healer = -1;`
        //
        // The beam does not outlive the medic. Left set, the patient would keep
        // naming a slot with no medic in it, and Deaths.Resolve would hand out
        // an assist the reference does not — the same class of bug as a stale
        // roster index, and just as quiet. Before the fields are cleared, so the
        // heal target is still readable.
        Healing.ReleasePatient(ref this, slot);

        Characters[slot] = default;
        Weapons[slot] = default;
        Alive[slot] = false;
    }

    /// <summary>
    /// Removes a slot and shifts every slot above it down by one.
    /// </summary>
    /// <remarks>
    /// <b>Not the same operation as <see cref="Clear"/>, and the difference is a
    /// class of bug this project already has a warning about.</b> A slot index
    /// here <em>is</em> a roster index, and the roster compacts on
    /// <c>PLAYER_LEAVE</c> — both the reference and the client delete the entry
    /// and renumber everything above it (<c>ClientBeginStep.gml:212-221</c>). A
    /// character left in place while the roster shifted underneath it would
    /// belong to whoever moved into that index: the wrong player, only after
    /// somebody leaves, which is exactly the intermittent off-by-one CLAUDE.md
    /// warns about.
    /// <para>
    /// <b>Everything else that names a slot renumbers with it.</b> A projectile's
    /// owner, a character's last damage dealer, the player who set them on fire,
    /// the medic beaming them: each is a roster index and each shifts here. Miss
    /// one and a kill is credited to the wrong player some seconds after an
    /// unrelated disconnect — the same bug wearing a different hat.
    /// </para>
    /// <para>
    /// Slot 0 is the server's own reserved player and never leaves, so this is
    /// never called with it — but nothing here depends on that.
    /// </para>
    /// </remarks>
    public void RemoveSlot(int slot)
    {
        if ((uint)slot >= MaxSlots)
            return;

        // Player.events/Destroy.xml, first line: `if(object != -1) with(object)
        // instance_destroy();`
        //
        // ⚠️ **A departure destroys the character, and that is the *whole* of
        // Character.events/Destroy.xml** — the weapon, the mines that die with
        // the Minegun, Quote's blade and bubbles, the knife hitbox, the medic's
        // beam and the intel drop. This path used to do the flag alone, on the
        // reading that a departure is not a death; it is not a death, but it
        // *is* a destruction, and the reference spells it with the same event.
        //
        // ⚠️ That line is invisible to every code-extracting grep of the
        // reference. GM8 writes an event's code inside
        // `<argument kind="STRING">`, and this one sits on the *same line* as
        // the opening tag — so anything that filters out lines beginning with a
        // tag eats it and hands back a Destroy event that starts at
        // `ds_map_destroy(rewards)`. The same shape as the two drag-and-drop
        // actions this project has already been caught by; read the raw XML.
        //
        // The `object != -1` guard is this port's Alive[]: a player who is dead
        // or has never picked a team has no character to destroy, and no flag to
        // drop either, since a carried briefcase lives on the character.
        if (Alive[slot])
            Clear(slot);

        // Player.events/Destroy.xml:11-15, the player's own sweep:
        //
        //     with(Rocket)            if(ownerPlayer == other.id) instance_destroy();
        //     with(BurningProjectile) if(ownerPlayer == other.id) instance_destroy();
        //     with(Shot)              if(ownerPlayer == other.id) instance_destroy();
        //     with(Needle)            if(ownerPlayer == other.id) instance_destroy();
        //
        // ⚠️ **A departure takes everything in the air with it, where a death
        // takes almost nothing.** That is the sharpest difference between the
        // two paths and it runs the opposite way from the intuition: a rocket
        // outlives its shooter's death precisely so it can still credit the
        // kill (`ownerPlayer` is a *player*, not a character) — and the moment
        // that player is gone there is nothing left to credit, so it is
        // destroyed rather than left ownerless. Without this a soldier could
        // disconnect and still kill somebody a second later.
        //
        // Flame and Flare declare `<parent>BurningProjectile</parent>`, and a
        // GM8 `with` walks an object's descendants, so one line covers both.
        // Nothing else in the pool does: Needle has no parent and needs its own
        // line, which is why the reference has four and not three.
        //
        // Mine, Blade, Bubble and Stab are deliberately absent here and absent
        // from the reference's list for the same reason — they belong to the
        // *weapon*, so the character's destruction above has already taken
        // them. ⚠️ Adding them here is a **proved equivalent mutant**: the two
        // sets are unreachably disjoint by the time this runs, because the only
        // way to own one of those four is to have a weapon, and the only way to
        // still have one after Clear is to have had no character — in which
        // case the character that owned them was cleared earlier and took them
        // then. Measured, not argued: the mutation that sweeps all nine kinds
        // survives the whole suite. It is written the reference's way so that
        // the two rules stay distinguishable.
        for (var i = LiveProjectileCount - 1; i >= 0; i--)
        {
            var handle = LiveProjectileHandle(i);
            ref readonly var projectile = ref Projectiles[handle.Index];

            if (projectile.OwnerSlot != slot)
                continue;

            if (projectile.Kind is ProjectileKind.Rocket or ProjectileKind.Flame
                or ProjectileKind.Flare or ProjectileKind.Shot or ProjectileKind.Needle)
            {
                FreeProjectile(handle);
            }
        }

        // `with(Sentry) if(ownerPlayer == other.id) instance_destroy();` is the
        // fifth line of that sweep and needs nothing of its own: a sentry is
        // held in the departing player's own slot, and the compaction below
        // overwrites it. Asserted rather than assumed — see RemoveSlotTests.
        //
        // `with(DeathCam)` and `with(ghost)` are the client's, and the rewards
        // map and badge list are presentation.

        // Player.events/Destroy.xml:8 is `clearPlayerDominations(id)`, and it
        // is deliberately *not* transcribed here. ⚠️ It is load-bearing in the
        // reference and redundant in the port, for the same reason: the
        // reference's tables are keyed by player *instance* and survive the
        // roster list compacting, so a departing player has to be deleted out of
        // them by hand — whereas this matrix is keyed by slot on both axes, and
        // the shift below overwrites every cell the clear would have zeroed.
        // Proved by mutation: deleting the call changes nothing.

        for (var i = slot; i < MaxSlots - 1; i++)
        {
            Characters[i] = Characters[i + 1];
            Weapons[i] = Weapons[i + 1];
            Alive[i] = Alive[i + 1];
            Sentries[i] = Sentries[i + 1];
            HasSentry[i] = HasSentry[i + 1];
            Players[i] = Players[i + 1];
            HasPlayer[i] = HasPlayer[i + 1];

            // The domination matrix's rows. Its columns are shifted below,
            // because they have to be done for every row and not only the ones
            // that moved.
            DominationKills[i] = DominationKills[i + 1];
        }

        Characters[MaxSlots - 1] = default;
        Weapons[MaxSlots - 1] = default;
        Alive[MaxSlots - 1] = false;
        Sentries[MaxSlots - 1] = default;
        HasSentry[MaxSlots - 1] = false;

        // A PlayerState holds no roster index, so shifting it is the whole of
        // its renumbering.
        Players[MaxSlots - 1] = default;
        HasPlayer[MaxSlots - 1] = false;
        DominationKills[MaxSlots - 1] = default;

        // ⚠️ The domination table is indexed by roster slot on *both* axes, so
        // it is the one thing in the state that needs shifting twice. The rows
        // moved with the loop above; this moves the columns inside each of them.
        for (var i = 0; i < MaxSlots; i++)
        {
            for (var j = slot; j < MaxSlots - 1; j++)
                DominationKills[i][j] = DominationKills[i][j + 1];

            DominationKills[i][MaxSlots - 1] = 0;
        }

        RenumberReferences(slot);
    }

    /// <summary>
    /// Rewrites every stored roster index after the slot below it was removed.
    /// </summary>
    private void RenumberReferences(int removed)
    {
        for (var i = 0; i < MaxSlots; i++)
        {
            if (Alive[i])
            {
                ref var character = ref Characters[i];
                character.LastDamageDealer = Renumber(character.LastDamageDealer, removed);
                character.SecondToLastDamageDealer = Renumber(character.SecondToLastDamageDealer, removed);
                character.BurnedBy = Renumber(character.BurnedBy, removed);
                character.Healer = Renumber(character.Healer, removed);
            }

            if (HasSentry[i])
                Sentries[i].LastDamageDealer = Renumber(Sentries[i].LastDamageDealer, removed);

            // A medic's heal target survives its owner's character, so it is
            // renumbered whether or not the slot is alive.
            Weapons[i].HealTarget = Renumber(Weapons[i].HealTarget, removed);
        }

        for (var i = 0; i < LiveProjectileCount; i++)
        {
            ref var projectile = ref Projectiles[ProjectileOrder[i]];
            projectile.OwnerSlot = Renumber(projectile.OwnerSlot, removed);
            projectile.Reflector = Renumber(projectile.Reflector, removed);

            // A rocket's set of already-passed team-mates names slots too, so it
            // shifts with them. Leaving it alone would give a rocket a free pass
            // through a stranger, or charge it 100 of its travel budget twice,
            // seconds after an unrelated disconnect.
            projectile.PlayersPassed = RenumberMask(projectile.PlayersPassed, removed);

            // And a burning projectile's set of people it has already burnt, for
            // the same reason: leaving it alone would let a flame burn a
            // stranger twice, or refuse to burn them at all, seconds after an
            // unrelated disconnect.
            projectile.EnemiesHit = RenumberMask(projectile.EnemiesHit, removed);
        }
    }

    /// <summary>
    /// A set of roster slots, held one bit each, after the slot below it left.
    /// </summary>
    /// <remarks>
    /// The bit for the departing slot is dropped rather than following the
    /// compaction, which is <see cref="Renumber"/>'s rule for a single index
    /// said in bits.
    /// </remarks>
    private static ulong RenumberMask(ulong mask, int removed)
    {
        var below = mask & ((1UL << removed) - 1);

        // C# masks a shift count to 63, so `mask >> 64` would be `mask >> 0` and
        // would resurrect every bit rather than clearing them. The last slot has
        // nothing above it by definition.
        var above = removed >= MaxSlots - 1 ? 0UL : mask >> (removed + 1);

        return below | (above << removed);
    }

    /// <summary>
    /// One stored roster index after <paramref name="removed"/> left.
    /// </summary>
    /// <remarks>
    /// A reference to the departing slot itself becomes "nobody" rather than
    /// following the compaction: the player it named is gone, and letting it
    /// point at whoever slid down into their index is the off-by-one this whole
    /// path exists to avoid. 255 is left alone because it already means nobody.
    /// </remarks>
    private static byte Renumber(byte stored, int removed)
    {
        if (stored == CharacterState.Nobody)
            return CharacterState.Nobody;

        if (stored == removed)
            return CharacterState.Nobody;

        return stored > removed ? (byte)(stored - 1) : stored;
    }

    /// <summary>
    /// Puts a freshly spawned character in a slot, at an exact spawn point.
    /// </summary>
    /// <remarks>
    /// Everything here is <c>Character.events/Create.xml</c>'s own initial
    /// state: <c>hp = maxHp</c> (line 37), <c>moveStatus = 0</c> (line 166) and
    /// <c>_last_xscale = image_xscale</c> (line 120) with no assignment to
    /// <c>image_xscale</c> before it, so both facings start at GM8's default of
    /// +1. <c>instance_create</c> leaves both speeds at zero.
    /// <para>
    /// The position comes from the map rather than from anything rounded, which
    /// is the property docs/PARITY.md leans on: a spawn is the one moment a
    /// character's state is known exactly rather than to within a wire
    /// quantisation bin.
    /// </para>
    /// <para>
    /// The weapon is created here too, because the reference creates it in the
    /// same event — the last thing <c>Character.events/Create.xml</c> does
    /// before its cosmetics. A character with no weapon is not a state the
    /// reference can be in, and it would write the wrong bytes if it were.
    /// </para>
    /// </remarks>
    public bool Spawn(SpawnPoints spawnPoints, int slot, PlayerClass playerClass, Team team, int spawnpointId, int group)
    {
        ArgumentNullException.ThrowIfNull(spawnPoints);

        if ((uint)slot >= MaxSlots)
            return false;

        if (!spawnPoints.TryGet(team, group, spawnpointId, out var x, out var y))
            return false;

        // Write-through to the player: a spawn is always *of* this slot's
        // player, so the class and team passed in become the player's. Keeps
        // every pre-phase-6 caller — which held these beside the state —
        // consistent with the one copy the simulation now reads.
        var timer = HasPlayer[slot] ? Players[slot].RespawnTimer : Timers.Inactive;

        // Read before the player is rebuilt, and deliberately not carried into
        // the new one: the bypass is spent by the spawn it feeds, and the next
        // protected death sets it again.
        var bypass = HasPlayer[slot] ? Players[slot].DeathmatchRespawnBypass : 0;

        Players[slot] = new PlayerState { Team = team, Class = playerClass, RespawnTimer = timer };
        HasPlayer[slot] = true;

        // Every sentinel this needs is in CharacterState.Fresh, which the parity
        // harness's anchor shares — see the warning on it about what a zeroed
        // character claims.
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;

        // GM8 issues an instance id here, and the order it induces is
        // observable — see CharacterState.CreationOrder. Pre-incremented so no
        // spawned character can share zero with a directly-placed one.
        character.CreationOrder = ++NextInstanceOrder;

        // Character.events/Create.xml:192-205, "Temporary invuln on spawn for
        // deathmatch players". Here rather than in CharacterState.Fresh because
        // it is the only part of the Create event that reads the *world*: the
        // gamemode, the configured amount, and what the last life had left.
        //
        // ⚠️ The bypass is spent, not merely read — a player only carries their
        // protection across one death, and the arm that set it
        // (Character.events/Destroy.xml:40) writes it again if the next death is
        // protected too.
        if (GameMode == GameMode.TeamDeathmatch)
        {
            character.DeathmatchInvulnerable = bypass != 0 ? bypass : TdmInvulnerabilityTicks;
            character.DeathmatchInvulnDecay = CharacterState.SpawnInvulnDecay;
        }

        Characters[slot] = character;

        // The weapon starts where instance_create put it: on the character's
        // own unrounded coordinates. End Step moves it to round(x) from this
        // tick onward — see WeaponState.X.
        Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        Alive[slot] = true;
        return true;
    }

    /// <summary>
    /// The live characters in <see cref="CharacterState.CreationOrder"/>, which
    /// is the order GM8 runs a collision event over them.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not slot order.</b> See <see cref="CharacterState.CreationOrder"/>
    /// for what that costs and the recording that measured it.
    /// <para>
    /// An insertion sort because <see cref="MaxSlots"/> is eight and the caller
    /// hands in a stack buffer: no allocation, and a comparison sort's setup
    /// would cost more than the whole loop. Stable, so two characters sharing a
    /// creation order — only possible for harness-placed anchors, which are all
    /// zero — keep slot order between them.
    /// </para>
    /// </remarks>
    /// <param name="order">At least <see cref="MaxSlots"/> long.</param>
    /// <returns>How many entries were written.</returns>
    public readonly int LiveCharactersInCreationOrder(Span<byte> order)
    {
        var count = 0;

        for (var slot = 0; slot < MaxSlots; slot++)
        {
            if (!Alive[slot])
                continue;

            var key = Characters[slot].CreationOrder;
            var at = count;

            while (at > 0 && Characters[order[at - 1]].CreationOrder > key)
            {
                order[at] = order[at - 1];
                at--;
            }

            order[at] = (byte)slot;
            count++;
        }

        return count;
    }

    /// <summary>
    /// The live sentries in <see cref="SentryState.CreationOrder"/>, which is
    /// the order GM8 runs a collision event over them.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Slot order is not build order.</b> The engineer in slot 5 can build
    /// before the one in slot 2, and a projectile explodes on the first sentry
    /// its collision event fires against — the same rule
    /// <see cref="CharacterState.CreationOrder"/> was measured for.
    /// <para>
    /// ⚠️ <b>Unlike the character case, no recording contradicts the old
    /// behaviour.</b> Seeing it needs two sentries overlapping one projectile's
    /// box, which no fixture holds. This is written because the rule was proven
    /// next door and applying it costs nothing — <b>not</b> because a divergence
    /// forced it, and the distinction is worth keeping if it ever has to be
    /// re-argued.
    /// </para>
    /// <para>
    /// Deliberately a second loop rather than a shared sort: the key lives in a
    /// different buffer for each list, and the abstraction that hides which
    /// buffer is being read is exactly the one that can silently sort by the
    /// wrong number.
    /// </para>
    /// </remarks>
    public readonly int LiveSentriesInCreationOrder(Span<byte> order)
    {
        var count = 0;

        for (var slot = 0; slot < MaxSlots; slot++)
        {
            if (!HasSentry[slot])
                continue;

            var key = Sentries[slot].CreationOrder;
            var at = count;

            while (at > 0 && Sentries[order[at - 1]].CreationOrder > key)
            {
                order[at] = order[at - 1];
                at--;
            }

            order[at] = (byte)slot;
            count++;
        }

        return count;
    }

    // --- projectiles ---------------------------------------------------------

    /// <summary>
    /// Adds a projectile to the pool and returns a handle to it.
    /// </summary>
    /// <remarks>
    /// The new projectile goes on the end of <see cref="ProjectileOrder"/>, so
    /// it is the last thing any creation-order walk reaches — which is what
    /// <c>instance_create</c> gives a GM8 instance.
    /// <para>
    /// Returns <see cref="ProjectileHandle.None"/> if the pool is full. That is
    /// the one failure mode the reference does not have; see
    /// <see cref="MaxProjectiles"/>. Callers should treat it as a bug in the
    /// capacity rather than as a condition to handle quietly.
    /// </para>
    /// </remarks>
    public ProjectileHandle SpawnProjectile(in ProjectileState projectile)
    {
        if (projectile.Kind == ProjectileKind.None)
            throw new ArgumentException("a projectile with no kind is a free pool slot", nameof(projectile));

        var slot = FindFreeProjectileSlot();
        if (slot < 0)
            return ProjectileHandle.None;

        Projectiles[slot] = projectile;

        // Bumped on spawn rather than on free, so the first generation ever
        // issued is 1 and ProjectileHandle.None's zero can never match a live
        // slot — which is what makes a defaulted handle field safe.
        ProjectileGenerations[slot]++;

        ProjectileOrder[LiveProjectileCount] = (ushort)slot;
        LiveProjectileCount++;

        return new ProjectileHandle(slot, ProjectileGenerations[slot]);
    }

    /// <summary>Removes a projectile: the reference's <c>instance_destroy</c>.</summary>
    /// <returns>Whether the handle named a live projectile.</returns>
    public bool FreeProjectile(ProjectileHandle handle)
    {
        if (!IsLive(handle))
            return false;

        // Mine.events/Destroy.xml: `owner.currentWeapon.lobbed -= 1`. On *any*
        // destruction — exploded, flown into a gate, or killed off with its
        // owner — because a Destroy event does not care why. `lobbed` gates
        // firing and goes on the wire, so losing a decrement permanently costs
        // the demoman a mine.
        ref var dying = ref Projectiles[handle.Index];
        if (dying.Kind == ProjectileKind.Mine && dying.OwnerSlot != ProjectileState.NoOwner)
            Weapons[dying.OwnerSlot].Lobbed = Math.Max(0, Weapons[dying.OwnerSlot].Lobbed - 1);

        // Bubble.events/Destroy.xml: `if(instance_exists(owner)) gun.bubbleCount
        // -= 1;` — guarded on the *character* and then dereferencing the
        // *weapon*, which is safe because one is destroyed with the other.
        if (dying.Kind == ProjectileKind.Bubble && dying.OwnerSlot != ProjectileState.NoOwner)
        {
            Weapons[dying.OwnerSlot].BubbleCount =
                Math.Max(0, Weapons[dying.OwnerSlot].BubbleCount - 1);
        }

        // BladeB.events/Destroy.xml, both lines: give the slot back, and charge
        // the owner for whatever of the refire cost the flight did not already
        // drain. ⚠️ The refund is *unguarded* — a blade whose owner has gone
        // still runs it in the reference, on a weapon instance that is being
        // destroyed in the same breath.
        if (dying.Kind == ProjectileKind.Blade && dying.OwnerSlot != ProjectileState.NoOwner)
        {
            ref var weapon = ref Weapons[dying.OwnerSlot];
            weapon.BladesOut = Math.Max(0, weapon.BladesOut - 1);

            var refireTime = WeaponStats.For(WeaponKind.Blade).RefireTime;
            if (Gml.Less(dying.AmmoDrained, refireTime))
                weapon.AmmoCount -= refireTime - dying.AmmoDrained;
        }

        Projectiles[handle.Index] = default;

        for (var i = 0; i < LiveProjectileCount; i++)
        {
            if (ProjectileOrder[i] != handle.Index)
                continue;

            // Shifted down rather than swapped with the last entry: a swap
            // would be O(1) and would scramble creation order, which is the
            // only reason this array exists.
            for (var j = i; j < LiveProjectileCount - 1; j++)
                ProjectileOrder[j] = ProjectileOrder[j + 1];

            LiveProjectileCount--;
            ProjectileOrder[LiveProjectileCount] = 0;
            return true;
        }

        return false;
    }

    /// <summary>Whether a handle still names the projectile it was issued for.</summary>
    public readonly bool IsLive(ProjectileHandle handle)
    {
        if (handle.IsNone || (uint)handle.Index >= MaxProjectiles)
            return false;

        return Projectiles[handle.Index].Kind != ProjectileKind.None
               && ProjectileGenerations[handle.Index] == handle.Generation;
    }

    /// <summary>Reads a projectile through a handle, refusing a stale one.</summary>
    public readonly bool TryGetProjectile(ProjectileHandle handle, out ProjectileState projectile)
    {
        if (!IsLive(handle))
        {
            projectile = default;
            return false;
        }

        projectile = Projectiles[handle.Index];
        return true;
    }

    /// <summary>
    /// The pool slot of the <paramref name="index"/>th live projectile in
    /// creation order.
    /// </summary>
    /// <remarks>
    /// Returns the slot rather than the projectile so a caller can take a
    /// <c>ref</c> into <see cref="Projectiles"/> and mutate in place, the way
    /// <see cref="MatchSimulation.Tick"/> already does with
    /// <see cref="Characters"/>.
    /// </remarks>
    public readonly int LiveProjectileSlot(int index)
    {
        if ((uint)index >= (uint)LiveProjectileCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "not a live projectile");

        return ProjectileOrder[index];
    }

    /// <summary>A handle to the <paramref name="index"/>th live projectile in creation order.</summary>
    public readonly ProjectileHandle LiveProjectileHandle(int index)
    {
        var slot = LiveProjectileSlot(index);
        return new ProjectileHandle(slot, ProjectileGenerations[slot]);
    }

    /// <summary>
    /// The lowest free pool slot, or -1.
    /// </summary>
    /// <remarks>
    /// Lowest-first rather than round-robin so that the choice is a function of
    /// the state alone: two worlds holding the same projectiles pick the same
    /// slot for the next one, which keeps a saved and restored world
    /// bit-identical (docs/BOTAI.md item 2). It costs a scan, and nothing
    /// observable depends on which slot is picked — creation order lives in
    /// <see cref="ProjectileOrder"/>, not in the slot numbers.
    /// </remarks>
    private readonly int FindFreeProjectileSlot()
    {
        if (LiveProjectileCount >= MaxProjectiles)
            return -1;

        for (var i = 0; i < MaxProjectiles; i++)
        {
            if (Projectiles[i].Kind == ProjectileKind.None)
                return i;
        }

        return -1;
    }
}

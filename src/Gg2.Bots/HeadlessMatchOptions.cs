using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// How a <see cref="HeadlessMatch"/> is set up. A record struct, so a caller
/// varies one field of a known-good configuration with <c>with</c>.
/// </summary>
/// <remarks>
/// ⚠️ <b>Use <see cref="Default"/>, never <c>new()</c>.</b> A record struct's
/// primary-constructor defaults do not apply to the zeroing constructor, so
/// <c>new HeadlessMatchOptions()</c> is a match on a null map with no bots and
/// no time limit — every one of them silent. The same trap as
/// <see cref="BotPopulationOptions.Default"/>, <c>CharacterState.Fresh</c> and
/// <c>PlayerState.Fresh</c>: <b>in this codebase a zeroed value type is
/// routinely not a valid one.</b>
/// </remarks>
public readonly record struct HeadlessMatchOptions(
    string Map,
    int Area,
    int Bots,
    int Difficulty,
    int CapLimit,
    int TimeLimitMinutes,
    int KillLimit,
    int RespawnTimeSeconds,
    int MaxTicks,
    uint Seed,
    int? RedBots = null,
    int? BlueBots = null,
    PlayerClass[]? AllowedClasses = null,
    BotBrainSpec? Brain = null,
    Team? HybridTeam = null)
{
    /// <summary>
    /// A short match on <c>ctf_truefort</c> with twelve bots. ⚠️ The values are
    /// spelled out rather than delegating to <c>new()</c> — writing <c>new()</c>
    /// here makes the factory a no-op that reads exactly like a fix.
    /// </summary>
    public static HeadlessMatchOptions Default => new(
        Map: "ctf_truefort",
        Area: 1,
        Bots: 12,
        // ⚠️ 5, matching BotPopulationOptions, changed 2026-09-02 with it.
        // Moving the SERVER's default without moving the instrument's would mean
        // `eval` measured tier-3 bots while the server shipped tier-5 ones — the
        // arms comparison would stop describing the configuration people play
        // against, which is the failure this repo keeps finding in other clothes.
        // ⛔ Every eval number recorded before this date was taken at tier 3.
        Difficulty: 5,
        CapLimit: 5,
        TimeLimitMinutes: 15,
        KillLimit: 30,
        RespawnTimeSeconds: 5,

        // ⚠️ A match MUST have a tick ceiling and it is not a safety net. CTF
        // overtime is unbounded — a tie with a flag off its base at 0:00 stays
        // open indefinitely — so a batch of matches with no ceiling has no
        // bound on its own running time. The rotation test in the bots lane hit
        // exactly this once bots started reaching the enemy flag.
        MaxTicks: 30 * 60 * 20,
        Seed: 1);

    /// <summary>
    /// Whether this configuration names the teams explicitly rather than
    /// filling <see cref="Bots"/> evenly.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Nullable rather than "0 means unset"</b>, because <b>0 is a value
    /// somebody wants</b> — <c>RedBots: 8, BlueBots: 0</c> is an uncontested
    /// attack, which is exactly the experiment that separates "the bots cannot
    /// return the flag" from "the bots cannot get past six defenders". An
    /// unspecified team and an empty one have to be different things, and this
    /// codebase's own rule is that a zeroed field meaning "off" is a trap.
    /// </remarks>
    public bool HasExplicitTeams => RedBots is not null || BlueBots is not null;
}

namespace GG2.Protocol;

/// <summary>
/// What killed somebody, as the <c>PLAYER_DEATH</c> byte carries it.
/// </summary>
/// <remarks>
/// <b>These ids are positional and there is no table of them in the source.</b>
/// <c>Scripts/Kill Log/initializeDamageSources.gml</c> calls
/// <c>createGlobalDamageSource</c> once per source in the order below, and
/// <c>createDamageSource</c> returns <c>ds_list_size</c> <em>after</em> the add —
/// so the first source is 1, not 0, and the numbering is exactly the order of
/// those calls. Reordering that script would renumber the wire; do not sort this
/// enum.
/// <para>
/// <see cref="None"/> is the reference's <c>lastDamageSource = -1</c>
/// (<c>Character.events/Create.xml</c>), stored as 0 here because the field is a
/// <c>byte</c> and 0 is the one value the id space leaves free — which is what
/// the comment in <c>createDamageSource</c> is hedging about.
/// </para>
/// </remarks>
public enum DamageSource : byte
{
    /// <summary>The reference's <c>-1</c>: nothing has damaged this target.</summary>
    None = 0,

    Needlegun = 1,
    Rifle = 2,
    RifleCharged = 3,
    Minegun = 4,
    Minigun = 5,
    Flamethrower = 6,
    Scattergun = 7,
    Shotgun = 8,
    Rocketlauncher = 9,
    Revolver = 10,
    SentryTurret = 11,
    Blade = 12,
    Bubble = 13,
    ReflectedRocket = 14,
    ReflectedSticky = 15,
    Knife = 16,
    Backstab = 17,
    Flare = 18,
    ReflectedFlare = 19,
    KillBox = 20,
    FragBox = 21,
    Pitfall = 22,
    FinishedOff = 23,
    FinishedOffGib = 24,
    BidFarewell = 25,
    GeneratorExplosion = 26,
}

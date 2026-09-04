
namespace Gg2.Env;

/// <summary>How an episode ended, or that it has not.</summary>
public enum Termination : byte
{
    Running = 0,

    /// <summary>Somebody died. In a duel that is the episode.</summary>
    Death = 1,

    /// <summary>The tick cap was reached with both still alive.</summary>
    TickCap = 2,
}

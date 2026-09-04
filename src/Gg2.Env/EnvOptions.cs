namespace Gg2.Env;

/// <summary>
/// <c>gg2-env</c>'s command line, parsed — and the composition root that turns it
/// into a server.
/// </summary>
/// <remarks>
/// <b>Top-level statements cannot be called by a test.</b> A dropped argument
/// in <c>Program.cs</c> — parsing <c>--nav</c> into a local and then building
/// <c>new EnvServer(maps)</c> without it — is otherwise ungateable: it compiles
/// because <see cref="EnvServer"/>'s nav directory is an optional parameter, so
/// every served world silently gets a null nav graph and
/// <c>EnvDriver.Scripted</c> is refused for a reason the caller can do nothing
/// about, while every test upstream passes because every test constructs
/// <see cref="EnvServer"/> itself. The fix is structural: the parse and the
/// wiring both live here, <c>Program.cs</c> is the shell around them, and
/// <c>EnvOptionsTests</c> drives <see cref="CreateServer"/> — the line that
/// actually matters.
/// <para>
/// This is also the general failure class: <c>docs/ENV.md</c> can document a
/// driver the served env cannot actually produce, and only a caller who acts on
/// the description finds out.
/// </para>
/// </remarks>
public sealed record EnvOptions
{
    /// <summary><c>serve</c> or <c>smoke</c>.</summary>
    public required string Command { get; init; }

    /// <summary>The port to bind. 0 asks the OS for a free one.</summary>
    public int Port { get; init; }

    public int Worlds { get; init; } = 1;

    public int Duels { get; init; } = 1000;

    public int Threads { get; init; } = 1;

    /// <summary>The reference's <c>Included Files</c>.</summary>
    public required string Maps { get; init; }

    /// <summary>
    /// Where the bot nav caches live, or null.
    /// </summary>
    /// <remarks>
    /// <b>Null is a real answer and not "unset"</b> — it means
    /// <see cref="EnvDriver.Scripted"/> is refused, which is the right behaviour
    /// for an env with no graphs and the wrong one when the caller passed
    /// <c>--nav</c>.
    /// </remarks>
    public string? Nav { get; init; }

    /// <summary>Where the maps live if neither <c>--maps</c> nor the env var says.</summary>
    public static string DefaultMaps =>
        Environment.GetEnvironmentVariable("GG2_REFERENCE") is { Length: > 0 } root
            ? Path.Combine(root, "Source", "gg2", "Included Files")
            : Path.Combine("D:", "code", "Gang-Garrison-2", "Source", "gg2", "Included Files");

    /// <summary>
    /// Parses an argument vector, or explains why it will not.
    /// </summary>
    /// <returns>False with <paramref name="error"/> set, rather than throwing.</returns>
    public static bool TryParse(string[] args, out EnvOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = new EnvOptions { Command = string.Empty, Maps = DefaultMaps };
        error = string.Empty;

        if (args.Length == 0)
        {
            error = "no command";
            return false;
        }

        if (args[0] is not ("serve" or "smoke"))
        {
            error = $"unknown command '{args[0]}'; try --help";
            return false;
        }

        var parsed = options with { Command = args[0] };

        for (var i = 1; i < args.Length; i++)
        {
            // Every option takes a value, so a trailing flag would read past
            // the end. Refused with the flag's own name rather than an
            // IndexOutOfRangeException from inside a parse.
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 >= args.Length)
            {
                error = $"'{args[i]}' wants a value";
                return false;
            }

            switch (args[i])
            {
                case "--port":
                    if (!int.TryParse(args[++i], out var port))
                    {
                        error = $"--port wants a number, got '{args[i]}'";
                        return false;
                    }

                    parsed = parsed with { Port = port };
                    break;

                case "--maps":
                    parsed = parsed with { Maps = args[++i] };
                    break;

                case "--nav":
                    parsed = parsed with { Nav = args[++i] };
                    break;

                case "--worlds":
                    if (!int.TryParse(args[++i], out var worlds))
                    {
                        error = $"--worlds wants a number, got '{args[i]}'";
                        return false;
                    }

                    parsed = parsed with { Worlds = worlds };
                    break;

                case "--duels":
                    if (!int.TryParse(args[++i], out var duels))
                    {
                        error = $"--duels wants a number, got '{args[i]}'";
                        return false;
                    }

                    parsed = parsed with { Duels = duels };
                    break;

                case "--threads":
                    if (!int.TryParse(args[++i], out var threads))
                    {
                        error = $"--threads wants a number, got '{args[i]}'";
                        return false;
                    }

                    parsed = parsed with { Threads = threads };
                    break;

                default:
                    error = $"unknown option '{args[i]}'";
                    return false;
            }
        }

        options = parsed;
        return true;
    }

    /// <summary>
    /// The composition root: the server these options describe.
    /// </summary>
    /// <remarks>
    /// <b>One line, and it decides everything.</b> Everything about the served
    /// env's capability is decided here, so this is what a test has to call —
    /// asserting that <see cref="Nav"/> parses is not the same claim as
    /// asserting it reaches the server.
    /// </remarks>
    public EnvServer CreateServer() => new(Maps, Nav);
}

using System.Text.Json;

namespace GG2.Server.Rooms;

/// <summary>What a player chooses for a room (<c>docs/ROOMS.md</c>, "Room settings").</summary>
/// <param name="Map"><c>rotation</c>, or one map from <see cref="RoomTemplate.Maps"/>.</param>
/// <param name="Bots"><c>off</c>, <c>easy</c>, <c>normal</c>, <c>hard</c> or <c>adaptive</c>.</param>
/// <param name="TeamSize"><c>small</c>, <c>medium</c> or <c>large</c>: how many players bots fill to.</param>
public sealed record RoomChoices(string Map = RoomTemplate.Rotation, string Bots = "off", string TeamSize = "medium")
{
    /// <summary>
    /// Parses a request body: a JSON object with at most these three string
    /// fields, each optional. Anything else is refused, not ignored, so a client
    /// that misspells a field learns it rather than getting a default.
    /// </summary>
    /// <exception cref="ArgumentException">The body is not that object.</exception>
    public static RoomChoices Parse(string json)
    {
        var choices = new RoomChoices();

        if (string.IsNullOrWhiteSpace(json))
            return choices;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"not JSON: {e.Message}", e);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("expected a JSON object");

            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException($"'{field.Name}' must be a string");

                var value = field.Value.GetString()!;

                choices = field.Name switch
                {
                    "map" => choices with { Map = value },
                    "bots" => choices with { Bots = value },
                    "teamSize" => choices with { TeamSize = value },
                    _ => throw new ArgumentException($"unknown field '{field.Name}'; expected map, bots or teamSize"),
                };
            }
        }

        return choices;
    }
}

/// <summary>
/// The fixed room template, filled in with a player's <see cref="RoomChoices"/>.
/// </summary>
/// <remarks>
/// <b>Everything not chosen comes from the host's base config</b> (the
/// <c>rooms</c> command's <c>--config</c>), then the fixed values below
/// overwrite it, then <see cref="RoomHost.CreateRoom"/> switches off what is
/// per-process. So the base supplies the bots' mode and nets, the rules and the
/// rotation, and a room cannot be talked out of the fixed values.
/// </remarks>
public static class RoomTemplate
{
    /// <summary>The map choice that means the base config's rotation.</summary>
    public const string Rotation = "rotation";

    public const int PlayerLimit = 12;

    public const int ExitWhenEmptySeconds = 120;

    public const int MaxLifetimeMinutes = 360;

    /// <summary>
    /// The maps a room may be set to: the reference's default rotation plus
    /// <c>ctf_oldfort</c>. Built in to every stock client, so nothing is ever
    /// downloaded. <c>gg_debug</c> is shipped and left out.
    /// </summary>
    public static IReadOnlyList<string> Maps { get; } = [.. ServerConfigFile.DefaultRotation, "ctf_oldfort"];

    private static readonly Dictionary<string, (bool Enabled, int Difficulty, bool Adaptive)> BotChoices =
        new(StringComparer.Ordinal)
        {
            ["off"] = (false, 3, false),
            ["easy"] = (true, 1, false),
            ["normal"] = (true, 3, false),
            ["hard"] = (true, 5, false),
            ["adaptive"] = (true, 3, true),
        };

    private static readonly Dictionary<string, int> TeamSizes = new(StringComparer.Ordinal)
    {
        ["small"] = 6,
        ["medium"] = 10,
        ["large"] = 16,
    };

    /// <summary>
    /// Fills <paramref name="options"/>, a fresh copy of the base config, in place.
    /// </summary>
    /// <exception cref="ArgumentException">A choice that is not on the list.</exception>
    public static void Apply(ServerOptions options, RoomChoices choices)
    {
        var bots = BotChoices.TryGetValue(choices.Bots, out var b)
            ? b
            : throw new ArgumentException($"bots '{choices.Bots}': expected {string.Join(", ", BotChoices.Keys)}");

        var fill = TeamSizes.TryGetValue(choices.TeamSize, out var f)
            ? f
            : throw new ArgumentException($"teamSize '{choices.TeamSize}': expected {string.Join(", ", TeamSizes.Keys)}");

        if (choices.Map == Rotation)
        {
            if (options.MapRotation.Count == 0)
                options.MapRotation = ServerConfigFile.DefaultRotation;

            options.ShuffleRotation = ShuffleMode.AvoidArenaFirst;

            // Empty, so the first map is the SHUFFLED rotation's first: an
            // explicit Map beats it (GameServer.Maps.cs, the opening map).
            options.Map = string.Empty;
        }
        else if (Maps.Contains(choices.Map, StringComparer.Ordinal))
        {
            options.Map = choices.Map;
            options.MapRotation = [choices.Map];
            options.ShuffleRotation = ShuffleMode.None;
        }
        else
        {
            throw new ArgumentException($"map '{choices.Map}': expected '{Rotation}' or one of {string.Join(", ", Maps)}");
        }

        options.Bots = options.Bots with
        {
            Enabled = bots.Enabled,
            Difficulty = bots.Difficulty,
            FillToPlayers = fill,
            MaxBots = fill,
        };
        options.AutoDifficulty = bots.Adaptive;

        options.PlayerLimit = PlayerLimit;
        options.Chat = options.Chat with { Enabled = true };
        options.ExitWhenEmptySeconds = ExitWhenEmptySeconds;
        options.MaxLifetimeMinutes = MaxLifetimeMinutes;
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>
/// The operator verbs, and the one place they are implemented.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Two transports, one dispatch.</b> <see cref="ControlPort"/> speaks a
/// line of JSON over loopback TCP and <see cref="WebAdminServer"/> speaks HTTP
/// to a browser somewhere else, and the day they each grow their own copy of
/// "what does <c>step</c> mean" is the day one of them starts doing something
/// the other cannot. This is CLAUDE.md's "three places" rule applied one level
/// up: the transports are transport, and everything an operator can ask for
/// lives here.
/// </para>
/// <para>
/// ⚠️ <b>Every method here runs on the tick thread.</b> Both callers arrange
/// that — the control port is serviced from inside <see cref="GameServer.Tick"/>
/// and the web admin marshals through a queue drained there — because
/// <c>WorldState</c> has no locks and this reads it. A third caller that skipped
/// the marshalling would be a data race whose failures are the irreproducible
/// kind.
/// </para>
/// <para>
/// ⚠️ <b>It cannot do anything a client cannot, except operator things.</b>
/// Freezing, stepping, teleporting, kicking and changing the map are operator
/// actions no client can ask for. Anything a client <em>can</em> ask for goes
/// through <c>command</c>, which submits a <see cref="PlayerCommand"/> to the
/// same <c>Execute</c> the socket switch and the bots reach.
/// </para>
/// </remarks>
public static class ControlCommands
{
    /// <summary>
    /// Runs one command, given as a JSON object, and returns the JSON that
    /// answers it.
    /// </summary>
    /// <remarks>
    /// Never throws for a bad request: a malformed command is an error reply,
    /// because the caller most in need of reading the answer is the one that got
    /// the request wrong.
    /// </remarks>
    public static string Dispatch(GameServer server, string line)
    {
        JsonElement request;

        try
        {
            request = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException e)
        {
            return Error($"not JSON: {e.Message}");
        }

        if (request.ValueKind != JsonValueKind.Object)
            return Error("a command is a JSON object");

        if (!request.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.String)
            return Error("no \"cmd\"");

        return Dispatch(server, cmd.GetString()!, request);
    }

    /// <inheritdoc cref="Dispatch(GameServer, string)"/>
    public static string Dispatch(GameServer server, string cmd, JsonElement request)
    {
        try
        {
            return Execute(server, cmd, request);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException)
        {
            return Error(e.Message);
        }
    }

    private static string Execute(GameServer server, string cmd, JsonElement request) => cmd switch
    {
        "ping" => Ok($"\"frame\":{server.Frame}"),

        "state" => State(server),

        "freeze" => Do(server.Freeze),

        "resume" => Do(server.Resume),

        "step" => Do(() => server.StepSimulation(Int(request, "ticks", 1))),

        "speed" => Do(() => server.SetSpeed(Speed(request))),

        "bots" => Do(() => server.SetBotFill(Int(request, "fill", 0))),

        "map" => server.RequestMapChange(Text(request, "name"), Int(request, "area", 1))
            ? Ok()
            : Error("no such map, or a map change is already running"),

        "teleport" => server.Teleport(
            Int(request, "slot", -1),
            Double(request, "x"),
            Double(request, "y"))
            ? Ok()
            : Error("no live character in that slot"),

        // ⚠️ An operator action with no client equivalent, and the only one that
        // ends a connection. A bot and the reserved slot 0 are refused BY NAME
        // rather than in silence: a kick that appeared to work on a bot would be
        // the panel lying about what it did, and the population manager would
        // put the bot straight back.
        "kick" => Kick(server, request),

        "command" => Command(server, request),

        _ => Error($"unknown cmd \"{cmd}\""),
    };

    private static string Do(Action action)
    {
        action();
        return Ok();
    }

    private static string Kick(GameServer server, JsonElement request)
    {
        var slot = Int(request, "slot", -1);

        return server.KickPlayer(slot, Optional(request, "reason") ?? string.Empty) switch
        {
            KickOutcome.Kicked => Ok(),
            KickOutcome.NotAConnection => Error(
                $"slot {slot} is a bot or the server's own reserved player, and has no "
                + "connection to close. Change the bot count with \"bots\"."),
            _ => Error($"slot {slot} is not on the roster"),
        };
    }

    /// <summary>
    /// Submits a <see cref="PlayerCommand"/> on a slot's behalf.
    /// </summary>
    /// <remarks>
    /// ⚠️ The whole point of this command: everything a client can do reaches
    /// the server through one dispatch, and an operator surface uses that
    /// dispatch rather than growing its own copy of each action.
    /// </remarks>
    private static string Command(GameServer server, JsonElement request)
    {
        var kindText = Text(request, "kind");

        if (!Enum.TryParse<PlayerCommandKind>(kindText, ignoreCase: true, out var kind)
            || kind == PlayerCommandKind.None)
        {
            return Error($"unknown command kind \"{kindText}\"");
        }

        var command = new PlayerCommand
        {
            Kind = kind,
            Argument = (byte)Int(request, "argument", 0),
        };

        return server.ExecuteFor(Int(request, "slot", -1), command)
            ? Ok()
            : Error("no such slot");
    }

    /// <summary>
    /// Everything an operator surface can show, as one JSON object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Additive only.</b> The control port has answered <c>state</c> since
    /// phase 8 and scripts read named fields out of it, so a field may be added
    /// here and one that already exists may not change meaning. The web panel's
    /// whole dashboard is built out of this same object, which is why it now
    /// carries much more than the control port ever needed.
    /// </para>
    /// <para>
    /// ⚠️ <b>Exact positions, not the wire's.</b> <c>TryGetExactPosition</c>
    /// hands back the unquantised doubles for the reason the control port wanted
    /// them: an operator surface exists to answer "where is it really", and one
    /// that silently reported the wire's rounding would make every sub-pixel
    /// question unanswerable.
    /// </para>
    /// </remarks>
    public static string State(GameServer server)
    {
        var json = new StringBuilder(4096);

        json.Append("{\"ok\":true");
        json.Append(",\"frame\":").Append(server.Frame);
        json.Append(",\"map\":\"").Append(Escape(server.CurrentMap)).Append('"');
        json.Append(",\"frozen\":").Append(server.IsFrozen ? "true" : "false");
        json.Append(",\"speed\":").Append(Number(server.Options.SpeedMultiplier));
        json.Append(",\"round\":{\"timer\":").Append(server.RoundTimer);
        json.Append(",\"decided\":").Append(server.RoundDecided ? "true" : "false");
        json.Append(",\"winner\":\"").Append(server.RoundWinner).Append("\"}");

        // --- everything below exists for the web panel ------------------------
        json.Append(",\"server\":{\"name\":\"").Append(Escape(server.Options.ServerName)).Append('"');
        json.Append(",\"port\":").Append(server.BoundPort);
        json.Append(",\"slots\":").Append(server.Options.PlayerLimit);
        json.Append(",\"uptimeSeconds\":").Append(Number(Math.Round(server.UptimeSeconds, 1)));
        json.Append(",\"tickMs\":").Append(Number(Math.Round(server.TickWorkMilliseconds, 3)));
        json.Append(",\"tickBudgetMs\":")
            .Append(Number(Math.Round(server.TickInterval.TotalMilliseconds, 3)));
        json.Append(",\"idle\":").Append(server.IsIdle ? "true" : "false");
        json.Append(",\"botFill\":").Append(server.Options.Bots.FillToPlayers);

        // ⚠️ Sent because it is the reason a bot count set from the panel can
        // appear to do nothing: below MinHumans the population manager's target
        // is ZERO rather than the fill, so an empty server with the default of 1
        // stays empty. The panel says so rather than leaving an operator to
        // conclude the button is broken.
        json.Append(",\"botMinHumans\":").Append(server.Options.Bots.MinHumans).Append('}');

        json.Append(",\"match\":{\"mode\":\"").Append(server.GameMode).Append('"');
        json.Append(",\"width\":").Append(server.MapWidth);
        json.Append(",\"height\":").Append(server.MapHeight);
        json.Append(",\"area\":").Append(server.CurrentMapArea);
        json.Append(",\"areas\":").Append(server.TotalMapAreas);
        json.Append(",\"redCaps\":").Append(server.RedCaps);
        json.Append(",\"blueCaps\":").Append(server.BlueCaps);
        json.Append(",\"capLimit\":").Append(server.Options.CapLimit);
        json.Append(",\"setupTimer\":").Append(server.SetupTimer);
        json.Append(",\"nextMap\":\"").Append(Escape(server.NextMap)).Append('"');
        json.Append(",\"changeIn\":").Append(server.MapChangeCountdown).Append('}');

        AppendIntel(json, "redIntel", server, Team.Red);
        AppendIntel(json, "blueIntel", server, Team.Blue);
        AppendControlPoints(json, server);
        AppendPlayers(json, server);

        json.Append('}');
        return json.ToString();
    }

    private static void AppendIntel(StringBuilder json, string name, GameServer server, Team team)
    {
        var intel = server.IntelOf(team);

        json.Append(",\"").Append(name).Append("\":{\"present\":")
            .Append(intel.HasBase ? "true" : "false");
        json.Append(",\"onField\":").Append(intel.OnField ? "true" : "false");
        json.Append(",\"x\":").Append(Number(intel.OnField ? intel.X : intel.HomeX));
        json.Append(",\"y\":").Append(Number(intel.OnField ? intel.Y : intel.HomeY));

        // ⚠️ The home position is sent as well as the current one, because
        // OnField does NOT mean "dropped": it means the briefcase instance
        // exists rather than being carried, which is also true of one sitting in
        // its own base. A panel that called both "dropped" would report a CTF
        // emergency on every quiet round, and only comparing the two positions
        // can tell them apart.
        json.Append(",\"homeX\":").Append(Number(intel.HomeX));
        json.Append(",\"homeY\":").Append(Number(intel.HomeY));
        json.Append(",\"carrier\":").Append(server.IntelCarrier(team)).Append('}');
    }

    private static void AppendControlPoints(StringBuilder json, GameServer server)
    {
        json.Append(",\"points\":[");

        for (var i = 0; i < server.ControlPointCount; i++)
        {
            if (i > 0)
                json.Append(',');

            var point = server.ControlPointAt(i);

            json.Append("{\"owner\":\"")
                .Append(point.Owned ? point.Team.ToString() : "None").Append('"');
            json.Append(",\"capping\":\"")
                .Append(point.BeingCaptured ? point.CappingTeam.ToString() : "None").Append('"');
            // ⚠️ CapTime is PER POINT, not a constant: dual-koth halves it.
            json.Append(",\"progress\":").Append(Number(
                point.CapTime > 0 ? Math.Round(point.Capping / point.CapTime, 4) : 0));
            json.Append('}');
        }

        json.Append(']');
    }

    private static void AppendPlayers(StringBuilder json, GameServer server)
    {
        json.Append(",\"players\":[");

        for (var slot = 0; slot < server.RosterCount; slot++)
        {
            if (slot > 0)
                json.Append(',');

            var stats = server.StatisticsOf(slot);

            json.Append("{\"slot\":").Append(slot);
            json.Append(",\"name\":\"").Append(Escape(server.NameOf(slot))).Append('"');
            json.Append(",\"team\":\"").Append(server.TeamOf(slot)).Append('"');
            json.Append(",\"class\":\"").Append(server.ClassOf(slot)).Append('"');
            json.Append(",\"bot\":").Append(
                Gg2.Bots.ScriptedBot.Of(server.SourceOf(slot)) is not null ? "true" : "false");
            json.Append(",\"connected\":").Append(server.IsConnected(slot) ? "true" : "false");
            json.Append(",\"address\":\"").Append(Escape(server.AddressOf(slot))).Append('"');
            json.Append(",\"alive\":").Append(server.IsAlive(slot) ? "true" : "false");
            json.Append(",\"intel\":").Append(server.IsCarryingIntel(slot) ? "true" : "false");
            json.Append(",\"kills\":").Append(stats.Kills);
            json.Append(",\"deaths\":").Append(stats.Deaths);
            json.Append(",\"caps\":").Append(stats.Caps);
            json.Append(",\"assists\":").Append(stats.Assists);
            json.Append(",\"points\":").Append(stats.Points);

            if (server.TryGetExactPosition(slot, out var x, out var y, out var hp))
            {
                json.Append(",\"x\":").Append(Number(x));
                json.Append(",\"y\":").Append(Number(y));
                json.Append(",\"hp\":").Append(Number(hp));
            }

            json.Append('}');
        }

        json.Append(']');
    }

    public static string Ok(string? extra = null) =>
        extra is null ? "{\"ok\":true}" : "{\"ok\":true," + extra + "}";

    public static string Error(string message) =>
        "{\"ok\":false,\"error\":\"" + Escape(message) + "\"}";

    /// <summary>
    /// Escapes a string for a JSON literal.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><see cref="JsonEncodedText"/> rather than three <c>Replace</c>
    /// calls, and the difference is a reply that does not parse.</b> The
    /// hand-rolled version handled backslash, quote and newline — and
    /// <c>ArgumentOutOfRangeException.Message</c> is multi-line with a
    /// <em>carriage return</em> in it, which is a raw control character inside a
    /// JSON string and therefore invalid. So the port answered an invalid speed
    /// with an unparseable error: the one reply a caller most needs to read.
    /// Measured — it failed the test that sends <c>"value":0</c>.
    /// </remarks>
    public static string Escape(string value) => JsonEncodedText.Encode(value).ToString();

    /// <remarks>
    /// ⚠️ Invariant culture and "R", because a machine reads this. A decimal
    /// comma would make the reply un-parseable on exactly the machines whose
    /// locale uses one, and nowhere else — the worst kind of bug to be handed.
    /// Infinity is not JSON, so an unpaced server reports its speed as the
    /// string it is set with.
    /// </remarks>
    public static string Number(double value) =>
        double.IsPositiveInfinity(value)
            ? "\"max\""
            : value.ToString("R", CultureInfo.InvariantCulture);

    private static string Text(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"\"{name}\" must be a string");

    private static string? Optional(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement request, string name, int fallback) =>
        request.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static double Double(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number
            : throw new ArgumentException($"\"{name}\" must be a number");

    /// <summary>Accepts a number or the string <c>"max"</c>, as <c>--speed</c> does.</summary>
    private static double Speed(JsonElement request)
    {
        if (!request.TryGetProperty("value", out var value))
            throw new ArgumentException("\"value\" must be a number or \"max\"");

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!.Equals("max", StringComparison.OrdinalIgnoreCase)
                ? double.PositiveInfinity
                : throw new ArgumentException("the only speed word is \"max\"");
        }

        return value.GetDouble();
    }
}

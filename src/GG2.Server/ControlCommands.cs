using System.Globalization;
using System.Text;
using System.Text.Json;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

/// <summary>
/// The operator verbs, and the one place they are implemented.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two transports, one dispatch.</b> <see cref="ControlPort"/> speaks a
/// line of JSON over loopback TCP and <see cref="WebAdminServer"/> speaks HTTP
/// to a browser somewhere else, and the day they each grow their own copy of
/// "what does <c>step</c> mean" is the day one of them starts doing something
/// the other cannot. This is CLAUDE.md's "three places" rule applied one level
/// up: the transports are transport, and everything an operator can ask for
/// lives here.
/// </para>
/// <para>
/// <b>Every method here runs on the tick thread.</b> Both callers arrange
/// that — the control port is serviced from inside <see cref="GameServer.Tick"/>
/// and the web admin marshals through a queue drained there — because
/// <c>WorldState</c> has no locks and this reads it. A third caller that skipped
/// the marshalling would be a data race whose failures are the irreproducible
/// kind.
/// </para>
/// <para>
/// <b>It cannot do anything a client cannot, except operator things.</b>
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
        // ⚠️ IOException and UnauthorizedAccessException are here for "config-set",
        // which is the only verb that touches the disk. A config file on a
        // read-only mount, or one another process is holding, is an operator
        // problem with a clear message — and this runs on the TICK THREAD, where
        // an escaping exception is a tick fault charged against
        // TickFaultBudget rather than a reply anybody reads.
        catch (Exception e) when (e is ArgumentException
                                      or FormatException
                                      or InvalidOperationException
                                      or IOException
                                      or UnauthorizedAccessException)
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

        // The operator's end of the /bots chat command. ⛔ It is here rather
        // than only in the chat plugins because otherwise a player can take the
        // bots away for up to a day and the operator has no verb that ends it:
        // the panel's bot field would accept a number, report it back and change
        // nothing, which is this file's recurring failure with no way to see the
        // cause. `minutes` 0, or absent, resumes.
        // ⚠️ It ignores [Bots] SuspendMinutes. That gates what a PLAYER may ask
        // for; an operator naming a duration is naming it.
        "bots-suspend" => BotsSuspend(server, Int(request, "minutes", 0)),

        "map" => server.RequestMapChange(Text(request, "name"), Int(request, "area", 1))
            ? Ok()
            : Error("no such map, or a map change is already running"),

        "teleport" => server.Teleport(
            Int(request, "slot", -1),
            Double(request, "x"),
            Double(request, "y"))
            ? Ok()
            : Error("no live character in that slot"),

        // An operator action with no client equivalent, and the only one that
        // ends a connection. A bot and the reserved slot 0 are refused BY NAME
        // rather than in silence: a kick that appeared to work on a bot would be
        // the panel lying about what it did, and the population manager would
        // put the bot straight back.
        "kick" => Kick(server, request),

        "command" => Command(server, request),

        // A line as the server's slot-0 spectator: chat through a chat plugin,
        // or an on-screen notice without one. `via` says which, because the two
        // look very different to a player. GameServer.Say.
        "say" => Ok($"\"via\":\"{server.Say(Text(request, "text"))}\""),

        // Operator moderation. Like "kick" these have no client equivalent, so
        // they do not go through Execute — but they live HERE rather than in
        // either transport, which is the rule the control port and the panel
        // both depend on: one verb table, so the two surfaces cannot drift into
        // slightly different servers.
        "ban" => Ban(server, request),

        "unban" => Unban(server, request),

        "bans" => Bans(server),

        // Read-only, and here rather than in the panel for the rule this whole
        // table exists for: the control port gets it too, so a script can pull
        // the same figures the Analytics view draws without scraping a page.
        "analytics" => Ok("\"analytics\":{" + server.Analytics.ToJson(DateTime.UtcNow) + "}"),

        // The configuration verbs. Like "kick" they have no client equivalent,
        // and like every verb here they live in ONE place so the control port and
        // the panel cannot drift into slightly different servers.
        "config" => Config(server),

        "config-set" => ConfigSet(server, request),

        // A whole file from a fleet hub, with host-local keys kept and a
        // compare-and-swap on the current file. ConfigInstall.
        "config-install" => ConfigInstall.Run(server, request),

        "restart" => Do(server.RequestRestart),

        // --- live settings -----------------------------------------------------
        //
        // The admin panel's main page. Each applies to the RUNNING server and is
        // never written to the config file — a restart puts the file's values
        // back. GameServer.Live says which settings can be changed this way and
        // why the rest cannot.
        "password" => Do(() => server.SetGamePassword(Optional(request, "value") ?? string.Empty)),

        "player-limit" => Do(() => server.SetPlayerLimit(Int(request, "value", -1))),

        "autobalance" => Do(() => server.SetAutobalance(Bool(request, "enabled"))),

        // Takes the GAME offline and leaves the process, the panel and the
        // control port up: everyone disconnected, no new connections, no bots.
        // GameServer.SetOffline.
        "offline" => Do(() => server.SetOffline(Bool(request, "enabled"))),

        "end-round" => server.RequestRoundEnd()
            ? Ok()
            : Error("the round is already over, or a map change is running"),

        "restart-round" => server.RequestRoundRestart()
            ? Ok()
            : Error("the round is already over, or a map change is running"),

        "rotation" => Do(() => server.SetRotation(Strings(request, "maps"))),

        "team" => Do(() => server.SetPlayerTeam(Int(request, "slot", -1), TeamOf(Text(request, "team")))),

        "bot-limit" => Do(() => server.SetBotLimit(Int(request, "max", -1))),

        "bot-brain" => Do(() => server.SetBotBrain(ServerSettings.ParseBotMode(Text(request, "mode")))),

        "bot-difficulty" => Do(() => server.SetBotDifficulty(Int(request, "tier", -1))),

        _ => Error($"unknown cmd \"{cmd}\""),
    };

    /// <summary>
    /// <c>ban</c>: by <c>address</c> (an address or CIDR) or by <c>slot</c>.
    /// </summary>
    /// <remarks>
    /// <b>By slot is the one an operator actually uses</b> — they are watching
    /// the panel and know who is being a nuisance by their name, not by their
    /// address — so the slot is resolved to an address here. By address is for
    /// somebody who is not currently connected.
    /// <para>
    /// ⛔ <c>minutes</c> is optional and omitting it means <b>permanent</b>.
    /// That is the right default for a deliberate operator action, and
    /// <see cref="BanList"/> is where the case against leaning on any of this
    /// is written down: an address is not a person.
    /// </para>
    /// </remarks>
    private static string Ban(GameServer server, JsonElement request)
    {
        var text = Optional(request, "address");

        if (string.IsNullOrWhiteSpace(text))
        {
            var slot = Int(request, "slot", -1);
            text = slot > 0 ? server.AddressOf(slot) : string.Empty;

            if (string.IsNullOrEmpty(text))
            {
                return Error(
                    $"slot {slot} is not a connected player; ban by \"address\" instead, "
                    + "or use \"state\" to find the slot");
            }
        }

        if (!IpRange.TryParse(text, out var range))
            return Error($"\"{text}\" is not an address or CIDR block");

        var minutes = Int(request, "minutes", 0);

        if (minutes < 0)
            return Error("\"minutes\" must not be negative; omit it for a permanent ban");

        var kicked = server.BanAddress(
            range,
            minutes > 0 ? TimeSpan.FromMinutes(minutes) : null,
            Optional(request, "reason") ?? string.Empty);

        return Ok($"\"range\":\"{Escape(range.ToString())}\",\"kicked\":{kicked}");
    }

    /// <summary>
    /// <c>unban</c>: lifts the ban on exactly this range.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Exact, not "whatever covers this address".</b> Unbanning
    /// <c>1.2.3.4</c> must not silently delete a <c>1.2.3.0/24</c> somebody put
    /// there on purpose — the message names what is actually in the list so an
    /// operator can see what to type instead.
    /// </remarks>
    private static string Unban(GameServer server, JsonElement request)
    {
        var text = Text(request, "address");

        if (!IpRange.TryParse(text, out var range))
            return Error($"\"{text}\" is not an address or CIDR block");

        if (server.UnbanAddress(range))
            return Ok();

        var covering = server.Bans.Entries
            .Where(entry => entry.Range != range && !entry.Range.IsSingleAddress)
            .Select(entry => entry.Range.ToString())
            .ToArray();

        return Error(
            $"{range} is not banned"
            + (covering.Length == 0
                ? string.Empty
                : $"; these blocks are, and have to be lifted by name: {string.Join(", ", covering)}"));
    }

    private static string Bans(GameServer server)
    {
        var json = new StringBuilder(256);
        json.Append("\"persistent\":").Append(server.Bans.IsPersistent ? "true" : "false");
        json.Append(",\"bans\":[");

        var first = true;

        foreach (var entry in server.Bans.Entries)
        {
            if (!first)
                json.Append(',');

            first = false;

            json.Append("{\"range\":\"").Append(Escape(entry.Range.ToString())).Append('"');
            json.Append(",\"expires\":");

            if (entry.ExpiresUtc is { } expiry)
                json.Append('"').Append(expiry.ToString("o", CultureInfo.InvariantCulture)).Append('"');
            else
                json.Append("null");

            json.Append(",\"reason\":\"").Append(Escape(entry.Reason)).Append("\"}");
        }

        json.Append(']');
        return Ok(json.ToString());
    }

    /// <summary>
    /// <c>config</c>: every setting, its current value, and where it is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>A secret's VALUE is never in this reply.</b> Five settings are marked
    /// <see cref="ServerSetting.Secret"/> — the game password, the admin
    /// password, the certificate's — and each is reported as <c>"set": true</c> or
    /// <c>false</c> with no <c>value</c> at all. The panel is the one surface here
    /// meant to be reachable from the network, so an endpoint that echoed the
    /// admin password would turn a stolen session cookie into the credential
    /// itself. <c>WebAdminTests.TheConfigEndpointNeverEchoesASecret</c>.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>overridden</c> is the field that stops the settings page from
    /// lying.</b> A flag beats the config file, so a setting pinned on the command
    /// line can be edited, saved and restarted with nothing changing — see
    /// <see cref="ServerOptions.CommandLineOverrides"/>.
    /// </para>
    /// </remarks>
    private static string Config(GameServer server)
    {
        var running = server.Options;
        var writable = running.ConfigFile.Length != 0;

        // ⚠️ THE VALUES ARE THE FILE'S, NOT THE RUNNING SERVER'S, and getting this
        // backwards is a settings page that appears to throw every save away:
        // nothing is applied live, so a page that re-read the running options
        // after a successful save showed the operator the OLD value back — with a
        // "saved" toast beside it. Measured in a browser, not reasoned about.
        // What the fields edit is what the next start will use.
        //
        // With no config file there is nothing to read, and the page is read-only
        // anyway, so the running options are shown instead of a screen of defaults
        // that describe no server at all.
        var shown = running;

        if (writable && File.Exists(running.ConfigFile))
        {
            try
            {
                var fromFile = ServerOptions.CreateDefault();
                ServerConfigFile.Apply(fromFile, running.ConfigFile);
                shown = fromFile;
            }
            catch (Exception e) when (e is IOException or FormatException)
            {
                // A file that has been hand-edited into something unreadable since
                // startup. Falling back to the running options is better than
                // refusing to open the page, and the operator is about to see the
                // same message from any save they attempt.
                shown = running;
            }
        }

        var json = new StringBuilder(12288);

        json.Append("{\"ok\":true");
        json.Append(",\"file\":\"").Append(Escape(running.ConfigFile)).Append('"');

        // Whether a save is possible at all. A server started with no --config has
        // nowhere to write, and the page has to say that BEFORE somebody types
        // into thirty fields — not after they press save.
        json.Append(",\"writable\":").Append(writable ? "true" : "false");

        // Whether this file is managed by deploy.sh --config, and what that means
        // for a save here: kept, but the next push stops for this server until it
        // is forced. See PushedConfigWatcher and docs/CONFIG.md, "Many servers,
        // one configuration".
        var (managed, edited, waiting) = server.PushedConfig?.Describe() ?? (false, false, false);
        json.Append(",\"managed\":").Append(managed ? "true" : "false");
        json.Append(",\"editedSincePush\":").Append(edited ? "true" : "false");
        json.Append(",\"pushWaiting\":").Append(waiting ? "true" : "false");

        json.Append(",\"overridden\":[");

        for (var i = 0; i < running.CommandLineOverrides.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            json.Append('"').Append(Escape(running.CommandLineOverrides[i])).Append('"');
        }

        json.Append(']');
        json.Append(",\"settings\":[");

        var first = true;

        foreach (var setting in ServerSettings.All)
        {
            if (!first)
                json.Append(',');

            first = false;

            json.Append("{\"id\":\"").Append(Escape(setting.Id)).Append('"');
            json.Append(",\"section\":\"").Append(Escape(setting.Section)).Append('"');
            json.Append(",\"key\":\"").Append(Escape(setting.Key)).Append('"');
            json.Append(",\"kind\":\"").Append(setting.Kind.ToString().ToLowerInvariant()).Append('"');
            json.Append(",\"secret\":").Append(setting.Secret ? "true" : "false");

            if (setting.Secret)
            {
                json.Append(",\"set\":")
                    .Append(setting.Get(shown).Length != 0 ? "true" : "false");
            }
            else
            {
                json.Append(",\"value\":\"").Append(Escape(setting.Get(shown))).Append('"');
            }

            // ⚠️ Whether the FILE and the RUNNING server disagree — which is what
            // "a restart is pending" means, and the only honest way to say it.
            // Reported for a secret too: that two passwords differ is not either
            // password, and an operator who saved one needs to know it has not
            // taken effect yet.
            //
            // ⛔ EXCEPT where a flag pinned it, and leaving that out was a false
            // alarm that could never be cleared: a --web-password server's file
            // and running value differ for ever, a restart will not reconcile
            // them, and the page would have shown a permanent "restart pending"
            // on a setting whose real problem is the flag. That is what
            // "overridden" above says, and saying both would say neither.
            if (setting.Get(shown) != setting.Get(running)
                && !running.CommandLineOverrides.Contains(setting.Id, StringComparer.Ordinal))
            {
                json.Append(",\"pending\":true");
            }

            if (setting.Choices.Count != 0)
            {
                json.Append(",\"choices\":[");

                for (var i = 0; i < setting.Choices.Count; i++)
                {
                    if (i > 0)
                        json.Append(',');

                    json.Append('"').Append(Escape(setting.Choices[i])).Append('"');
                }

                json.Append(']');
            }

            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    /// <summary>
    /// <c>config-set</c>: applies settings to the config FILE, not to the running
    /// server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>It changes the file and nothing else, and that is the design rather
    /// than a limitation.</b> Every subsystem reads its options once, at
    /// <see cref="GameServer.Start"/> — the listener has bound its port, the ban
    /// list has opened its file, the panel has hashed its password — so writing
    /// new values into a running server's options object would apply some of them
    /// and not others with nothing telling an operator which. The reply says
    /// <c>"restartRequired": true</c> and <c>restart</c> is the verb that applies
    /// it. The three things that CAN change on a live server — the map, the bot
    /// count, the speed — have had their own verbs since phase 8.
    /// </para>
    /// <para>
    /// ⚠️ <b>The candidate is built from the FILE, not from the running options.</b>
    /// The running options carry command-line overrides, and writing those back
    /// would silently promote every flag on the command line into the config file
    /// the first time anybody saved anything.
    /// </para>
    /// <para>
    /// ⚠️ <b>Validated before anything is written.</b> A rejected setting leaves
    /// the file exactly as it was, so a typo cannot produce a config that stops
    /// the server coming back up — which, for a restart button, is the failure
    /// that matters.
    /// </para>
    /// <para>
    /// ⚠️ <b>This does file I/O on the tick thread</b>, as every verb here runs
    /// there. It is a few kilobytes, once, when an operator presses a button —
    /// against a 33.3 ms budget and beside a map load that does far more. It must
    /// not become something that happens per tick.
    /// </para>
    /// </remarks>
    private static string ConfigSet(GameServer server, JsonElement request)
    {
        var path = server.Options.ConfigFile;

        if (path.Length == 0)
        {
            return Error(
                "this server was started without --config, so there is no file to save to. "
                + "Restart it with --config <path> — the file does not have to exist yet.");
        }

        if (!request.TryGetProperty("settings", out var changes)
            || changes.ValueKind != JsonValueKind.Object)
        {
            return Error("\"settings\" must be an object of id -> value");
        }

        // ⚠️ Read the file fresh rather than reusing whatever was parsed at
        // startup: another operator, or a hand edit, may have changed it since,
        // and saving one field must not silently revert theirs.
        var candidate = ServerOptions.CreateDefault();

        if (File.Exists(path))
            ServerConfigFile.Apply(candidate, path);

        var applied = new List<string>();

        foreach (var change in changes.EnumerateObject())
        {
            var setting = ServerSettings.Find(change.Name);

            if (setting is null)
                return Error($"unknown setting \"{change.Name}\"");

            if (change.Value.ValueKind != JsonValueKind.String)
                return Error($"\"{change.Name}\" must be a string; every value here is ini text");

            setting.Set(candidate, change.Value.GetString()!);
            applied.Add(setting.Id);
        }

        // Before the write, so a configuration the server could not start on never
        // reaches the disk. Validate throws ArgumentException and friends, which
        // Dispatch turns into an error reply naming the option.
        candidate.Validate();

        ServerConfigFile.Write(candidate, path);

        // Which of the just-saved settings the command line will still beat. The
        // page greys these out, but an operator can also reach this verb from the
        // control port, where there is nothing to grey.
        var shadowed = applied
            .Where(id => server.Options.CommandLineOverrides.Contains(id, StringComparer.Ordinal))
            .ToArray();

        var json = new StringBuilder(256);
        json.Append("\"file\":\"").Append(Escape(Path.GetFullPath(path))).Append('"');
        json.Append(",\"saved\":").Append(applied.Count);
        json.Append(",\"restartRequired\":true");
        json.Append(",\"shadowed\":[");

        for (var i = 0; i < shadowed.Length; i++)
        {
            if (i > 0)
                json.Append(',');

            json.Append('"').Append(Escape(shadowed[i])).Append('"');
        }

        json.Append(']');
        return Ok(json.ToString());
    }

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
    /// The whole point of this command: everything a client can do reaches
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
    /// <b>Additive only.</b> The control port has answered <c>state</c> since
    /// phase 8 and scripts read named fields out of it, so a field may be added
    /// here and one that already exists may not change meaning. The web panel's
    /// whole dashboard is built out of this same object, which is why it now
    /// carries much more than the control port ever needed.
    /// </para>
    /// <para>
    /// <b>Exact positions, not the wire's.</b> <c>TryGetExactPosition</c>
    /// hands back the unquantised doubles for the reason the control port wanted
    /// them: an operator surface exists to answer "where is it really", and one
    /// that silently reported the wire's rounding would make every sub-pixel
    /// question unanswerable.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Holds the bot population at zero for a while, or ends a suspension.
    /// </summary>
    /// <remarks>
    /// Answered with the state that resulted rather than with a bare <c>ok</c>:
    /// "resume" on a server that was not suspended and "suspend" for zero minutes
    /// are both no-ops, and a caller that could not tell them apart from a
    /// successful one would have no way to check its own request landed.
    /// </remarks>
    private static string BotsSuspend(GameServer server, int minutes)
    {
        if (minutes < 0 || minutes > 1440)
            return Error("minutes must be 0..1440; 0 resumes");

        if (minutes == 0)
            server.ResumeBots();
        else
            server.SuspendBots(minutes);

        return Ok($"\"suspendedSeconds\":{server.BotSuspensionSecondsLeft}");
    }

    // Probed once: the panel polls `state` every second, and a directory walk per
    // poll is file I/O for an answer that only a redeploy changes.
    private static readonly Lazy<bool> ShippedCheckpointsPresent =
        new(() => MapDirectory.ResolveCheckpoints().Count > 0);

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

        // Which build is answering. A deploy that copied binaries and restarted
        // is otherwise unverifiable from outside the host: `systemctl is-active`
        // says a process is running, not which one. BuildInfo says why the
        // commit here is HEAD-at-build-time and not a statement about the
        // working tree it came from.
        json.Append(",\"version\":\"").Append(Escape(BuildInfo.Version)).Append('"');
        json.Append(",\"commit\":\"").Append(Escape(BuildInfo.Commit)).Append('"');
        json.Append(",\"port\":").Append(server.BoundPort);
        json.Append(",\"slots\":").Append(server.Options.PlayerLimit);
        json.Append(",\"uptimeSeconds\":").Append(Number(Math.Round(server.UptimeSeconds, 1)));
        json.Append(",\"tickMs\":").Append(Number(Math.Round(server.TickWorkMilliseconds, 3)));
        json.Append(",\"tickBudgetMs\":")
            .Append(Number(Math.Round(server.TickInterval.TotalMilliseconds, 3)));
        json.Append(",\"idle\":").Append(server.IsIdle ? "true" : "false");

        // Since-startup totals. The per-second history behind the panel's graphs
        // is PerfHistory, over its own stream; these are the running counts a
        // number beside a graph wants.
        json.Append(",\"lateTicks\":").Append(server.LateTicks);
        json.Append(",\"worstOverrunMs\":")
            .Append(Number(Math.Round(server.WorstOverrunMilliseconds, 1)));
        json.Append(",\"bytesSent\":").Append(server.BytesSent);

        // ⚠️ Zero on a healthy server for its whole life. Anything else is a bug
        // that has already happened and been absorbed — the server carried on,
        // which is not the same as nothing being wrong. GameServer.TickFaults.
        json.Append(",\"tickFaults\":").Append(server.TickFaults);

        // The managed heap, which is what the connection limits actually bound.
        // ⛔ NOT the process's working set: that includes the runtime, the JIT
        // and the nav tables, so it neither moves the way a connection flood
        // moves nor sits still when one stops. tools/abuse/ reads this.
        json.Append(",\"heapMb\":")
            .Append(Number(Math.Round(GC.GetTotalMemory(forceFullCollection: false) / 1048576.0, 1)));

        json.Append(",\"joining\":").Append(server.JoiningCount);
        json.Append(",\"bansActive\":").Append(server.Bans.Entries.Count);
        json.Append(",\"botFill\":").Append(server.Options.Bots.FillToPlayers);
        json.Append(",\"botLimit\":").Append(server.Options.Bots.MaxBots);

        // Whether a policy brain can be chosen live: with a checkpoint the server
        // was started with, or else the shipped bundle — GameServer.SetBotBrain.
        json.Append(",\"policy\":")
            .Append(server.Options.PolicyPaths.Count > 0 || ShippedCheckpointsPresent.Value ? "true" : "false");

        // ⛔ Whether there IS a join password, never the password: the panel is
        // reachable from the network, and this is the config verb's rule too.
        json.Append(",\"passworded\":").Append(server.Passworded ? "true" : "false");
        json.Append(",\"autobalance\":").Append(server.Options.Autobalance ? "true" : "false");
        json.Append(",\"joins\":").Append(server.JoinsSinceStart);
        json.Append(",\"offline\":").Append(server.IsOffline ? "true" : "false");

        json.Append(",\"rotation\":[");

        for (var i = 0; i < server.Rotation.Count; i++)
            json.Append(i == 0 ? "\"" : ",\"").Append(Escape(server.Rotation[i])).Append('"');

        json.Append("],\"rotationIndex\":").Append(server.CurrentRotationIndex);

        // Which plugin the panel's chat box speaks through, or null for the
        // MESSAGE_STRING fallback — see GameServer.Say.
        json.Append(",\"chatPlugin\":")
            .Append(server.ChatPluginName is { } chat ? $"\"{Escape(chat)}\"" : "null");

        // Sent because it is the reason a bot count set from the panel can
        // appear to do nothing: below MinHumans the population manager's target
        // is ZERO rather than the fill, so an empty server with the default of 1
        // stays empty. The panel says so rather than leaving an operator to
        // conclude the button is broken.
        json.Append(",\"botMinHumans\":").Append(server.Options.Bots.MinHumans);

        // The second reason a bot count set from the panel can appear to do
        // nothing, and the one an operator cannot otherwise see at all: a player
        // typed /bots off and the population is being held at zero until this
        // reaches zero. Sent unconditionally, so the panel can say which of the
        // two explanations applies.
        json.Append(",\"botSuspendSeconds\":").Append(server.BotSuspensionSecondsLeft);
        json.Append(",\"botSuspendMinutes\":").Append(server.Options.BotSuspendMinutes).Append('}');

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

        // Zero while nothing is pending, and on a multi-area map inside the
        // countdown it differs from "area" above — which keeps naming the area
        // being played. See GameServer.NextMapArea.
        json.Append(",\"nextArea\":").Append(server.NextMapArea);
        json.Append(",\"changeIn\":").Append(server.MapChangeCountdown).Append('}');

        AppendBots(json, server);
        AppendIntel(json, "redIntel", server, Team.Red);
        AppendIntel(json, "blueIntel", server, Team.Blue);
        AppendControlPoints(json, server);
        AppendPlayers(json, server);

        json.Append('}');
        return json.ToString();
    }

    /// <summary>
    /// Which brain this server's bots run, and whether it is actually driving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mode is what was CONFIGURED and the tiers are what was BUILT</b>,
    /// and both are sent because they can disagree — <c>BotBrain.Build</c> clears
    /// the brain when a reconcile leaves no controller, so a panel showing the
    /// mode alone would report a policy server whose bots are all scripted. That
    /// is the failure this whole block exists to make visible: a controller that
    /// is configured, reported and never consulted.
    /// </para>
    /// <para>
    /// <b>The class sets are read off a live controller, never re-derived.</b>
    /// <c>IMicroController.Drives</c> is the one place a tier's class set is
    /// decided — a policy's own, narrowed by <c>[Bots] ClassPolicy</c> — and a
    /// second copy here would be the copy nothing gates. The cost is that a
    /// server with no bots on it sends no class sets, which is honest: with no
    /// bot built there is no controller to ask.
    /// </para>
    /// <para>
    /// <b>The arbiter's budget is read live, not from the options.</b>
    /// <c>MicroBudgetController</c> moves <c>UnitsPerTick</c> while the server
    /// runs, so the configured number and the one in force are different
    /// questions and this answers the second.
    /// </para>
    /// </remarks>
    private static void AppendBots(StringBuilder json, GameServer server)
    {
        var brain = server.Options.Bots.Brain;

        json.Append(",\"bots\":{\"mode\":\"")
            .Append(server.Options.BotMode.ToString().ToLowerInvariant()).Append('"');

        // The 1-5 tier, which is the scripted layer's combat skill whatever the
        // brain: a policy tier drives inside an engagement, the scripted aim
        // chain outside it.
        json.Append(",\"difficulty\":").Append(server.Options.Bots.Difficulty);

        // BotBrainSpec.Name — the same string tools/bench prints in a result
        // table, so a bot watched on the panel and a number read off the bench
        // name the same arm.
        json.Append(",\"brain\":\"").Append(brain?.Name ?? ScriptedBrain).Append('"');

        // ⛔ The gate every tier is behind, and the reason a correctly configured
        // brain can drive nothing at all. HumansOnly is the LIVE SERVER's default
        // (BotBrainSpec's own is Anyone), so on a server with no humans on it no
        // controller is ever handed the wheel — a fact that looks exactly like a
        // broken checkpoint from the outside.
        // ⚠️ Absent, not "humans", when there is no brain: mode `scripted` has no
        // gate at all, and a field naming one would describe a restriction that
        // does not exist.
        if (brain is not null)
        {
            json.Append(",\"engage\":\"")
                .Append(brain.Engage == GG2.Bots.EngageOpponents.Anyone ? "anyone" : "humans")
                .Append('"');
        }

        json.Append(",\"tiers\":[");

        if (brain is not null)
        {
            // The first wrapped bot on the roster. Every bot shares the tier
            // ORDER and every controller of one tier was built from one spec, so
            // any of them answers for all of them.
            var built = LiveTiers(server);

            for (var i = 0; i < brain.Tiers.Count; i++)
            {
                if (i > 0)
                    json.Append(',');

                json.Append("{\"name\":\"").Append(Escape(brain.Tiers[i].Name)).Append('"');

                if (built is not null && i < built.Count)
                    AppendDrives(json, built[i]);

                json.Append('}');
            }
        }

        json.Append(']');

        if (brain?.Arbiter is GG2.Bots.DecisionQuotaArbiter quota)
        {
            json.Append(",\"arbiterMicroseconds\":").Append(quota.UnitsPerTick);
            json.Append(",\"arbiterSpentMicroseconds\":").Append(quota.SpentThisTick);
        }

        json.Append('}');
    }

    /// <summary>The tiers of the first wrapped bot on the roster, or null.</summary>
    private static IReadOnlyList<GG2.Bots.IMicroController>? LiveTiers(GameServer server)
    {
        for (var slot = 0; slot < server.RosterCount; slot++)
        {
            if (GG2.Bots.HybridBot.Of(server.SourceOf(slot)) is { } hybrid)
                return hybrid.Tiers;
        }

        return null;
    }

    /// <summary>The classes a controller will drive, asked of the controller.</summary>
    /// <remarks>
    /// <b>Quote is the ceiling</b>, the same one <c>BotBrain.ParseClassPolicies</c>
    /// uses: the fill never draws a Quote bot, but the duel server makes one for a
    /// Quote human, and a tier assigned Quote would otherwise show no class.
    /// </remarks>
    private static void AppendDrives(StringBuilder json, GG2.Bots.IMicroController tier)
    {
        json.Append(",\"classes\":[");

        var first = true;

        for (var playerClass = PlayerClass.Scout; playerClass <= PlayerClass.Quote; playerClass++)
        {
            if (!tier.Drives(playerClass))
                continue;

            if (!first)
                json.Append(',');

            first = false;
            json.Append('"').Append(playerClass).Append('"');
        }

        json.Append(']');
    }

    private static void AppendIntel(StringBuilder json, string name, GameServer server, Team team)
    {
        var intel = server.IntelOf(team);

        json.Append(",\"").Append(name).Append("\":{\"present\":")
            .Append(intel.HasBase ? "true" : "false");
        json.Append(",\"onField\":").Append(intel.OnField ? "true" : "false");
        json.Append(",\"x\":").Append(Number(intel.OnField ? intel.X : intel.HomeX));
        json.Append(",\"y\":").Append(Number(intel.OnField ? intel.Y : intel.HomeY));

        // The home position is sent as well as the current one, because
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
            // CapTime is PER POINT, not a constant: dual-koth halves it.
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
            var source = server.SourceOf(slot);
            var bot = GG2.Bots.ScriptedBot.Of(source) is not null;

            json.Append(",\"bot\":").Append(bot ? "true" : "false");

            if (bot)
                AppendBrain(json, source, server.ClassOf(slot), server.TeamOf(slot), server.Options.Bots.Brain);
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

    /// <summary>What is driving one bot, and whether it is driving right now.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>brain</c> is what COULD drive this slot and <c>driving</c> is what
    /// did.</b> A controller only holds the wheel inside the engagement window —
    /// a small fraction of ticks — so a panel showing the instant alone would
    /// report a policy server as scripted almost all the time, and one showing
    /// the configuration alone could not tell a working controller from one an
    /// arbiter never admits. <c>microTicks</c> and <c>scriptedTicks</c> are the
    /// running counts behind the second question: they differ by exactly the
    /// ticks a controller drove, and a brain named here whose <c>microTicks</c>
    /// stay at zero is a brain that is configured and never consulted.
    /// </para>
    /// <para>
    /// <b>An unwrapped <c>ScriptedBot</c> reports the scripted brain rather than
    /// nothing.</b> Bot mode <c>scripted</c> is arm 1 — a complete bot, not a
    /// degraded one — and an absent field would read as a bot whose brain could
    /// not be determined.
    /// </para>
    /// </remarks>
    private static void AppendBrain(
        StringBuilder json,
        IInputSource source,
        PlayerClass playerClass,
        Team team,
        GG2.Bots.BotBrainSpec? brain)
    {
        // A bot off its brain's team (--bot-brain-team) is a plain scripted bot.
        if (GG2.Bots.HybridBot.Of(source) is not { } hybrid
            || (GG2.Bots.TeamGatedBot.Of(source) is { } gated && gated.Team != team))
        {
            json.Append(",\"brain\":\"").Append(ScriptedBrain).Append('"');
            json.Append(",\"driving\":false");
            return;
        }

        // -1 when every tier refuses this class — it falls back to the scripted
        // layer entirely, which is a real answer and not an error.
        var tier = hybrid.TierIndexFor(playerClass);

        // ⛔ Named from the SPEC, not from the controller. A controller's own name
        // need not say which checkpoint it holds, so two tiers could report one
        // name and the panel's tier rows —
        // which are the spec's — could not be matched to a single bot. The two
        // lists are index-aligned by BotBrainSpec.Wrap; the length check is for a
        // bot wrapped by some other spec, where the controller's own name is the
        // only honest answer left.
        json.Append(",\"brain\":\"").Append(Escape(
            tier < 0 ? ScriptedBrain
            : brain is not null && tier < brain.Tiers.Count ? brain.Tiers[tier].Name
            : hybrid.Tiers[tier].Name)).Append('"');

        json.Append(",\"driving\":").Append(hybrid.MicroDrove ? "true" : "false");
        json.Append(",\"microTicks\":").Append(hybrid.MicroTicks);
        json.Append(",\"scriptedTicks\":").Append(hybrid.ScriptedTicks);
    }

    /// <summary>
    /// What the state calls the macro layer driving alone.
    /// </summary>
    /// <remarks>
    /// <b>The same word <c>--bot-mode</c> takes and <c>BotBrainSpec.Name</c>
    /// would never produce</b>, so a reader can compare a slot's brain against the
    /// server's tier list without a second vocabulary. It is a bot brain, not the
    /// absence of one.
    /// </remarks>
    private const string ScriptedBrain = "scripted";

    public static string Ok(string? extra = null) =>
        extra is null ? "{\"ok\":true}" : "{\"ok\":true," + extra + "}";

    public static string Error(string message) =>
        "{\"ok\":false,\"error\":\"" + Escape(message) + "\"}";

    /// <summary>
    /// Escapes a string for a JSON literal.
    /// </summary>
    /// <remarks>
    /// <b><see cref="JsonEncodedText"/> rather than three <c>Replace</c>
    /// calls, and the difference is a reply that does not parse.</b> A
    /// hand-rolled version handling only backslash, quote and newline misses
    /// that <c>ArgumentOutOfRangeException.Message</c> is multi-line with a
    /// <em>carriage return</em> in it, a raw control character inside a JSON
    /// string and therefore invalid — an unescaped one turns an invalid speed
    /// into an unparseable error, the one reply a caller most needs to read.
    /// </remarks>
    public static string Escape(string value) => JsonEncodedText.Encode(value).ToString();

    /// <remarks>
    /// Invariant culture and "R", because a machine reads this. A decimal
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

    private static bool Bool(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException($"\"{name}\" must be true or false");

    private static string[] Strings(JsonElement request, string name)
    {
        if (!request.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"\"{name}\" must be an array of strings");

        var strings = new List<string>();

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"\"{name}\" must be an array of strings");

            strings.Add(item.GetString()!);
        }

        return [.. strings];
    }

    private static Team TeamOf(string text) => text.Trim().ToLowerInvariant() switch
    {
        "red" => Team.Red,
        "blue" => Team.Blue,
        "spectator" or "spec" => Team.Spectator,
        _ => throw new ArgumentException($"no team '{text}'; red, blue or spectator"),
    };

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

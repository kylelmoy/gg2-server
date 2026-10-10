/*
 * Every piece of text the web panel shows, in one place.
 *
 * To reword the panel, edit a value here. To translate it, copy this file,
 * translate the values and set "lang". Nothing else in the panel holds
 * display text: index.html and editor.html name a key with data-t (and
 * data-t-title, data-t-placeholder, data-t-aria-label, data-t-html), and the
 * scripts call t('key') or t('key', { name: value }).
 *
 * The rules, gated by WebAssetTests:
 *
 *   - Between the `const STRINGS =` and the final `;` is strict JSON — double
 *     quotes, no comments, no trailing commas — because the tests parse it.
 *   - A value is a string, or a plural object ({ "one": …, "other": … }, any of
 *     the Intl.PluralRules categories) chosen by the `count` variable.
 *   - {name} in a value is replaced by the variable of that name. A translation
 *     must keep every placeholder its English value has.
 *   - Every key the pages and scripts use exists here, and every key here is
 *     used, so a stale entry cannot linger.
 *   - Values under data-t-html are inserted as HTML and may carry markup
 *     (<code>, <b>, <a>); every other value is inserted as text.
 */
const STRINGS = {
  "lang": "en",
  "common": {
    "duration": {
      "seconds": "{s}s",
      "minutes": "{m}m",
      "hours": "{h}h {m}m",
      "days": "{d}d {h}h"
    }
  },
  "page": {
    "analytics": {
      "joins": "Joins",
      "lastSeen": "Last seen",
      "mapLabel": "Map",
      "metricJoins": "joins",
      "metricNew": "new",
      "metricPeak": "peak",
      "metricPlayers": "players",
      "metricPlaytime": "playtime",
      "mostPlayed": "Top by playtime",
      "perDay": "Per day",
      "place": "Place",
      "places": "Places",
      "placesEmpty": "No places resolved.",
      "played": "Played",
      "player": "Player",
      "players": "Players",
      "range14d": "14d",
      "range30d": "30d",
      "range90d": "90d",
      "rangeAll": "all",
      "title": "Analytics",
      "topEmpty": "No connections yet.",
      "us": "United States",
      "whatToPlot": "Metric",
      "whichServer": "Server",
      "where": "Connection origins",
      "world": "World"
    },
    "boot": {
      "loading": "Loading"
    },
    "bots": {
      "brain": "Brain",
      "brainChange": "Changing it replaces all bots.",
      "brainKey": "brain",
      "brainLabel": "Bot brain",
      "difficulty": "Difficulty",
      "difficultyHint": "Aim and reaction speed. 5 is full strength. Applies to running bots immediately.",
      "difficultyLabel": "Bot difficulty",
      "fewer": "Fewer bots",
      "fillHint": "Target total players, humans + bots. 0 disables bots. Humans displace bots.",
      "fillTo": "Fill target",
      "limit": "Bot limit",
      "limitHint": "Max concurrent bots, regardless of fill.",
      "lowerLimit": "Lower bot limit",
      "more": "More bots",
      "policy": "Policy",
      "raiseLimit": "Raise bot limit",
      "scripted": "Scripted",
      "search": "Search",
      "tiered": "Tiered",
      "title": "Bots"
    },
    "brand": "gg2-server",
    "chat": {
      "empty": "No messages.",
      "messageLabel": "Message",
      "placeholder": "Message",
      "send": "Send",
      "title": "Chat"
    },
    "common": {
      "apply": "Apply",
      "close": "Close",
      "loading": "Loading…",
      "refresh": "Refresh",
      "timeRange": "Time range"
    },
    "confirm": {
      "cancel": "Cancel",
      "confirm": "Confirm",
      "title": "Confirm"
    },
    "fleet": {
      "allOffline": "All offline",
      "allOnline": "All online",
      "build": "Build",
      "empty": "No members. Pair one below.",
      "endRoundEverywhere": "End round on all",
      "everyServer": "All servers",
      "map": "Map",
      "member": "Member",
      "name": "Name",
      "namePlaceholder": "us-east",
      "pair": "Pair",
      "pairHint": "On the member, set <code>[Web] Certificate=auto</code>, <code>FleetMember=1</code>, <code>Bind=0.0.0.0</code> and <code>AllowFrom=</code>&lt;hub address&gt;, then restart. The pairing code is printed at startup.",
      "pairTitle": "Pair a server",
      "pairingCode": "Pairing code",
      "players": "Players",
      "say": "Say",
      "sayPlaceholder": "Message to all servers",
      "server": "Server",
      "settings": "Fleet settings",
      "sub": "Servers managed by this hub.",
      "tick": "Tick",
      "title": "Fleet",
      "url": "Panel URL",
      "urlPlaceholder": "https://203.0.113.7:8191/"
    },
    "foot": "gg2-server web admin",
    "game": {
      "autobalance": "Autobalance teams",
      "clear": "Clear",
      "endRound": "End round",
      "fewerSeats": "Fewer seats",
      "joinPassword": "Join password",
      "limitHint": "Maximum number of players allowed to connect. Does not disconnect existing players.",
      "liveNote": "Applied live until restart; not written to the config file. Use Settings for that.",
      "moreSeats": "More seats",
      "newPassword": "New password",
      "offlineHint": "Disconnects all players, refuses connections, removes bots. The panel stays up.",
      "passwordHint": "Applies to new joins; connected players stay.",
      "passwordNone": "none",
      "playerLimit": "Player limit",
      "restartRound": "Restart round",
      "roundHint": "End round: next map in rotation. Restart round: same map from the start. Both run the 10 s round end.",
      "server": "Server",
      "set": "Set",
      "takeOffline": "Take offline",
      "title": "Game"
    },
    "hosts": {
      "label": "Servers"
    },
    "liveMap": {
      "empty": "no map loaded",
      "teleport": "Click to teleport",
      "teleportTitle": "Click the map to teleport the selected player",
      "title": "Live map"
    },
    "log": {
      "follow": "Follow",
      "title": "Log"
    },
    "login": {
      "insecure": "Unencrypted connection: the password is sent in cleartext.",
      "password": "Password",
      "signIn": "Sign in"
    },
    "mapCard": {
      "add": "Add",
      "addMap": "Add map",
      "changeHint": "Ends the current round immediately.",
      "changeTo": "Change to",
      "go": "Go",
      "rotation": "Rotation",
      "rotationEmpty": "No rotation; the current map repeats.",
      "search": "Search maps",
      "title": "Map"
    },
    "perf": {
      "fiveMinutesAgo": "5 min ago",
      "gcPerSecond": "gc/s",
      "lateTicks": "late ticks",
      "now": "now",
      "range1m": "1m",
      "range3m": "3m",
      "range5m": "5m",
      "sent": "sent",
      "ticksPerSecond": "ticks/s",
      "title": "Performance",
      "worstOverrun": "worst overrun"
    },
    "players": {
      "caps": "Caps",
      "class": "Class",
      "deaths": "D",
      "empty": "No players connected.",
      "filter": "Filter",
      "filterLabel": "Filter players",
      "health": "Health",
      "kills": "K",
      "player": "Player",
      "points": "Pts",
      "title": "Players"
    },
    "round": {
      "blu": "BLU",
      "label": "Round",
      "red": "RED",
      "round": "round",
      "setup": "SETUP"
    },
    "settings": {
      "filter": "Filter settings",
      "restartNote": "Saved settings apply on restart.",
      "revert": "Revert",
      "save": "Save",
      "saveRestart": "Save & restart",
      "sections": "Sections",
      "title": "Settings"
    },
    "system": {
      "title": "System"
    },
    "tiles": {
      "connections": "connections",
      "joinsNote": "joins since start, incl. repeats",
      "label": "Server health",
      "ms": "ms",
      "ofBudget": "of budget",
      "players": "players",
      "running": "running",
      "tick": "tick",
      "uptime": "uptime"
    },
    "title": "gg2-server",
    "topbar": {
      "analytics": "Analytics",
      "connecting": "connecting",
      "fleet": "Fleet",
      "map": "map",
      "mapTitle": "Current map",
      "mode": "mode",
      "modeTitle": "Gamemode",
      "restart": "Restart",
      "settings": "Settings",
      "signOut": "Sign out"
    }
  },
  "app": {
    "toast": {
      "signedOut": "signed out",
      "refused": "refused",
      "noAnswer": "no response from server"
    },
    "done": {
      "botFill": "Bot fill: {fill}",
      "map": "Map change: {name}",
      "kick": "Kicked",
      "ban": "Banned",
      "offline": "Server offline",
      "online": "Server back online",
      "endRound": "Round ended",
      "restartRound": "Round restarting",
      "autobalanceOn": "Autobalance on",
      "autobalanceOff": "Autobalance off",
      "playerLimit": "Player limit: {value}",
      "passwordSet": "Join password set",
      "passwordRemoved": "Join password removed",
      "rotation": "Rotation updated",
      "team": "Moved to {team}",
      "botLimit": "Bot limit: {max}",
      "botBrain": "Brain: {mode}; bots replaced",
      "botDifficulty": "Bot difficulty: {tier}",
      "teleport": "Teleported",
      "configSet": "Saved",
      "restart": "Restarting",
      "generic": "Done"
    },
    "live": {
      "live": "live",
      "lost": "reconnecting",
      "connecting": "connecting"
    },
    "banner": {
      "changingMap": "Map change to {map} in {seconds}s"
    },
    "clock": {
      "stalemate": "stalemate",
      "wins": "{team} wins",
      "firstTo": "first to {count}"
    },
    "tiles": {
      "tickNote": "{share}% of {budget}ms",
      "population": "{humans}, {bots}",
      "humans": {
        "one": "{count} human",
        "other": "{count} humans"
      },
      "bots": {
        "one": "{count} bot",
        "other": "{count} bots"
      },
      "offline": "offline",
      "frozen": "frozen",
      "idle": "idle (2 Hz, empty)",
      "running": "running",
      "slots": "/ {slots}"
    },
    "chat": {
      "hintPlugin": "Sent as {name} via {plugin}.",
      "theServer": "server",
      "hintNotice": "No chat plugin loaded; sent as an on-screen notice to all players.",
      "teamOnly": "(team)"
    },
    "bots": {
      "suspended": {
        "one": "Bots disabled via /bots for {count} more minute. Setting a bot count here cancels it.",
        "other": "Bots disabled via /bots for {count} more minutes. Setting a bot count here cancels it."
      },
      "blocked": {
        "one": "Bots held until {count} human connects (--bots-min-humans); {humans} connected.",
        "other": "Bots held until {count} humans connect (--bots-min-humans); {humans} connected."
      }
    },
    "brain": {
      "arbiter": "Arbiter: {budget} µs/tick budget, {spent} used last tick.",
      "noBots": "No bots running.",
      "humansOnly": "Controllers engage human enemies only (--bot-engages-bots enables bot-vs-bot).",
      "drives": "Drives {classes}.",
      "tierEmpty": "no bots",
      "tierBots": {
        "one": "{count} bot",
        "other": "{count} bots"
      },
      "tierShare": "{driving} driving now. {micro} of {polls} ticks driven since spawn.",
      "tierScripted": "Macro layer only; no controller accepts these classes.",
      "needsPolicy": "Requires --policy at startup",
      "changeTitle": "Change bot brain?",
      "changeBody": "All bots are removed and re-added running {mode}. Search and policy brains cost more tick time than scripted."
    },
    "roster": {
      "noMatch": "No players match.",
      "nobody": "No players connected.",
      "botTag": "BOT",
      "brainShare": "{brain}: {share}% of ticks ({micro}/{polls})",
      "intelTag": "INTEL",
      "actionsFor": "Actions for {name}"
    },
    "objective": {
      "takenBy": "taken by {name}",
      "atBase": "at base",
      "dropped": "dropped",
      "redIntel": "RED intel",
      "blueIntel": "BLU intel",
      "owner": {
        "None": "neutral",
        "Red": "RED",
        "Blue": "BLUE"
      },
      "point": "point {number}"
    },
    "map": {
      "noImage": "no image for {name}",
      "changeTitle": "Change map?",
      "changeBody": "Ends the round and moves all players to {name}.",
      "tip": "{name} · {class} · {hp} hp",
      "selectFirst": "select a player first"
    },
    "charts": {
      "tick": {
        "title": "Tick time",
        "unit": "ms",
        "budget": "{ms}ms budget",
        "mean": "mean",
        "worst": "worst"
      },
      "cpu": {
        "title": "CPU",
        "unit": "% of one core, 5s",
        "cpu": "cpu"
      },
      "mem": {
        "title": "Memory",
        "unit": "MB",
        "resident": "resident",
        "heap": "managed heap"
      },
      "late": {
        "title": "Late ticks",
        "unit": "per second",
        "late": "late"
      },
      "net": {
        "title": "Sent",
        "unit": "KB/s",
        "out": "out"
      },
      "load": {
        "title": "Load",
        "unit": "count",
        "alive": "alive",
        "bots": "bots",
        "humans": "humans"
      },
      "aria": "{title} ({unit}), last 5 min",
      "minutesAgo": {
        "one": "{count} minute ago",
        "other": "{count} minutes ago"
      },
      "waiting": "awaiting first sample",
      "caption": "{title} · {unit}"
    },
    "kpi": {
      "worstOverrun": "+{ms}ms",
      "none": "none"
    },
    "units": {
      "b": "{value} B",
      "kb": "{value} KB",
      "mb": "{value} MB",
      "gb": "{value} GB",
      "ghz": "{value} GHz",
      "mhz": "{value} MHz"
    },
    "menu": {
      "moveRed": "Move to Red",
      "moveBlue": "Move to Blue",
      "moveSpectators": "Move to spectators",
      "kick": "Kick",
      "kickTitle": "Kick player?",
      "kickBody": "Disconnects {name} immediately.",
      "ban": "Ban",
      "banTitle": "Ban player?",
      "banBodyAddress": "Disconnects {name} and permanently bans {address}, including anyone else on it.",
      "banBody": "Disconnects {name} and permanently bans their address, including anyone else on it.",
      "gone": "{name} left or changed slot; no action taken",
      "thatPlayer": "That player"
    },
    "system": {
      "unknown": "unknown",
      "clockNominal": "{clock} (nominal)",
      "clockNow": "{clock} (now)",
      "cores": {
        "one": "{count} core",
        "other": "{count} cores"
      },
      "debugBuild": "DEBUG BUILD",
      "osTitle": "Operating system",
      "os": "os",
      "architecture": "architecture",
      "host": "host",
      "uptime": "uptime",
      "cpuTitle": "Processor",
      "model": "model",
      "clock": "clock",
      "logicalCores": "logical cores",
      "memoryTitle": "Memory",
      "total": "total",
      "available": "available",
      "reportedBy": "reported by",
      "thisProcess": "this process",
      "managedHeap": "managed heap",
      "runtimeTitle": "Runtime",
      "framework": "framework",
      "identifier": "identifier",
      "build": "build",
      "gc": "gc",
      "gcMode": "{kind}, {concurrency}",
      "gcServer": "server",
      "gcWorkstation": "workstation",
      "gcConcurrent": "concurrent",
      "gcNonConcurrent": "non-concurrent",
      "collections": "collections",
      "threads": "threads",
      "process": "process",
      "processValue": "pid {pid}, up {uptime}",
      "timer": "timer",
      "storageTitle": "Storage",
      "mount": "mount",
      "free": "free",
      "freeValue": "{free} of {total}",
      "maps": "maps",
      "nav": "nav",
      "recordings": "recordings"
    },
    "rotation": {
      "now": "NOW",
      "moveUp": "Move up",
      "moveDown": "Move down",
      "remove": "Remove",
      "noMap": "no map '{name}'"
    },
    "offline": {
      "bringOnline": "Bring online",
      "takeOffline": "Take offline",
      "hintOffline": "Offline: connections refused, no bots. Live settings still apply.",
      "hintOnline": "Disconnects all players, refuses connections, removes bots.",
      "askTitle": "Take server offline?",
      "askNobody": "No players connected.",
      "askPeople": {
        "one": "Disconnects {count} player.",
        "other": "Disconnects {count} players."
      },
      "askBody": "Refuses connections and removes bots until brought back online. The process and panel keep running."
    },
    "password": {
      "set": "set",
      "none": "none"
    },
    "settings": {
      "unsaved": {
        "one": "{count} unsaved change.",
        "other": "{count} unsaved changes."
      },
      "waiting": {
        "one": "{count} saved setting pending restart.",
        "other": "{count} saved settings pending restart."
      },
      "agree": "File matches running server.",
      "takeEffect": "Settings apply on restart.",
      "loading": "Loading…",
      "noAnswer": "No response from server.",
      "noFile": "no config file (started without --config)",
      "readonly": "Started without --config; nothing to save to. Restart with --config <path> (the file need not exist) to edit.",
      "managed": "Managed by scripts/deploy.sh --config. Saves here affect this server only; the next push stops on them unless they are copied into deploy/config or the push is forced.",
      "editedSincePush": "Edited here since the last push.",
      "pushWaiting": "Pushed configuration pending; applied when the server is next empty.",
      "noMatch": "No settings match.",
      "pendingTag": "restart pending",
      "pendingTitle": "Saved; running server still has the old value",
      "pinnedTag": "pinned",
      "pinnedTitle": "Set by a command-line flag, which overrides the file",
      "pinnedNote": "Pinned by a command-line flag: the flag's value persists across restarts regardless of this file.",
      "secretSet": "set; type to replace",
      "secretNotSet": "not set",
      "saved": {
        "one": "Saved {count} setting",
        "other": "Saved {count} settings"
      },
      "stillPinned": "Still pinned by command line: {names}"
    },
    "restart": {
      "title": "Restart server?",
      "body": "Disconnects all players and discards the current round. The process stays up and reloads the config file.",
      "toast": "Restarting; the panel reconnects automatically",
      "afterSave": "Saved settings will be applied."
    },
    "login": {
      "wrongPassword": "wrong password"
    },
    "analytics": {
      "refused": "refused by server",
      "sub": "{server} · {file}",
      "noAnswer": "{name} did not answer and is not in these figures.",
      "off": "{name} has analytics disabled and adds nothing."
    },
    "round": {
      "endTitle": "End round?",
      "endBody": "No winner; advances to the next map in rotation after the 10 s round end.",
      "restartTitle": "Restart round?",
      "restartBody": "No winner; {map} restarts after the 10 s round end.",
      "thisMap": "this map"
    },
    "fleet": {
      "hubTab": "Hub",
      "hubTitle": "This server (hub)",
      "tabTitle": {
        "one": "{name} — {map}, {count} player",
        "other": "{name} — {map}, {count} players"
      },
      "noMap": "no map",
      "unreachable": "unreachable",
      "notMember": "not a member of this hub",
      "asking": "Querying members…",
      "hubNoAnswerSentence": "No response from hub.",
      "players": "{humans} + {bots} bots / {slots}",
      "tick": "{ms} ms",
      "open": "Open",
      "remove": "Remove",
      "members": {
        "one": "{count} member, certificate-pinned.",
        "other": "{count} members, certificate-pinned."
      },
      "removeTitle": "Remove {name}?",
      "removeBody": "The hub discards its token and stops managing it. The server keeps running; re-pairing needs its pairing code.",
      "removed": "Removed {name}",
      "hubNoAnswer": "no response from hub",
      "memberRefused": "{name}: {error}",
      "doneOn": {
        "one": "{done} on {count} server",
        "other": "{done} on {count} servers"
      },
      "said": "Said",
      "offlineTitle": "Take all servers offline?",
      "offlineBody": "Disconnects all players on every server and refuses connections until brought back online.",
      "offline": "Offline",
      "online": "Online",
      "endRoundTitle": "End round on all servers?",
      "endRoundBody": "No winner; each server advances to its next map.",
      "roundEnded": "Round ended",
      "pairing": "Pairing…",
      "paired": "Paired with {name}",
      "pair": "Pair"
    },
    "server": {
      "port": ":{port}"
    }
  },
  "analytics": {
    "off": "Analytics disabled: nothing is recorded. To enable, {hint}, then restart.",
    "offHint": "set an analytics file",
    "notCollecting": "not collecting",
    "fleet": {
      "all": "All servers",
      "allTitle": "Every server added together",
      "file": {
        "one": "{count} server added together",
        "other": "{count} servers added together"
      }
    },
    "players": {
      "one": "{count} player",
      "other": "{count} players"
    },
    "joins": {
      "one": "{count} join",
      "other": "{count} joins"
    },
    "kpi": {
      "players": "players all time",
      "playersSummed": {
        "one": "summed over {count} server",
        "other": "summed over {count} servers"
      },
      "playersSince": "since {date}",
      "joins": "joins",
      "joinsPerPlayer": "{rate} per player",
      "timePlayed": "time played",
      "timePlayedNote": "total connected time",
      "peak": "peak concurrent",
      "peakUtc": "{time} UTC",
      "peakOnHost": "{host}, {at}",
      "today": "today",
      "todayNew": "{count} new",
      "todayNobody": "none yet",
      "connected": "connected now",
      "connectedNote": {
        "one": "player",
        "other": "players"
      }
    },
    "days": {
      "none": "no days recorded",
      "peak": "peak {count}"
    },
    "map": {
      "noGeometry": "map geometry failed to load",
      "onMap": {
        "one": "{n} player on the map",
        "other": "{n} players on the map"
      },
      "byState": "{n} placed by state",
      "outOfView": "{n} outside the US",
      "noMarker": "{n} in unmapped places",
      "noState": "{n} without state ({type} is country-level)",
      "unresolved": "{n} unresolved",
      "noGeoip": "No geo database loaded; pass --geoip <path.mmdb>",
      "caption": "{parts}."
    },
    "places": {
      "unresolved": "not resolved",
      "usState": "{state}, United States"
    },
    "top": {
      "noName": "(no name)",
      "onNow": "on now",
      "now": "now"
    },
    "foot": {
      "player": "\"Player\" = source address, hashed with a per-file salt. The protocol has no accounts: players sharing an address count once; one person across changing addresses counts more than once.",
      "fleet": {
        "one": "Sum of {count} server's figures. Salts differ per server, so a player seen on two servers counts twice; daily peak is the busiest single server's.",
        "other": "Sum of {count} servers' figures. Salts differ per server, so a player seen on two servers counts twice; daily peak is the busiest single server's."
      },
      "geoipRegions": "Geo: {type}, built {built} (subdivisions).",
      "geoipCountries": "Geo: {type}, built {built} (countries only).",
      "noGeoip": "No geo database loaded.",
      "trimmed": "{n} oldest player records dropped to stay under {cap}; returning players from before then count twice in the all-time total.",
      "kept": "{n} of {cap} player records kept."
    }
  },
  "settings": {
    "listPlaceholder": "comma,separated"
  },
  "settingHelp": {
    "Settings": {
      "HostingPort": "Game port. 0 picks a free port.",
      "PlayerLimit": "Network client seats, 1..48. Slot 0 and bots excluded.",
      "MultiClientLimit": "Max connections per address. Loopback exempt.",
      "PlayerName": "Name of reserved roster slot 0 (permanent spectator).",
      "UseLobby": "Announce to the ganggarrison.com lobby (the client's server list). The only outbound connection this server makes.",
      "Rewards": "Answer REWARD_REQUEST by relaying each player's reward id to the auth server."
    },
    "Server": {
      "ServerName": "Shown in the server browser and on join.",
      "WelcomeMessage": "Sent to each joining player. Empty: none.",
      "ChatJoinMessage": "Chat line sent to each player on joining chat. Empty: none. Requires a chat plugin in ServerPluginList.",
      "Password": "Game password. Empty: none (no PASSWORD_REQUEST).",
      "Map": "Starting map. Empty: first rotation entry.",
      "Rotation": "Map rotation, in order. Duplicates allowed.",
      "ShuffleRotation": "Rotation order at startup. arena-last shuffles but never opens on an arena map.",
      "VotableMaps": "Maps a plugin map vote may offer. Empty: the rotation, else every map in the maps directory.",
      "ServerPluginList": "Server-sent plugins clients load, comma-separated.",
      "ServerPluginsRequired": "Clients must load the plugin list to join.",
      "AutoBalance": "Move a player when one team leads by two.",
      "AimCheck": "Aim input no mouse could produce: off, log it, or kick after 30.",
      "AimAnalysis": "Log players whose aim statistics no recorded human matches.",
      "CapLimit": "Captures to win a round, 1..255. 0 clamps to 1, not unlimited.",
      "Time Limit": "Round length in minutes, 1..255. 0 clamps to 1.",
      "Deathmatch Kill Limit": "Kills to win a team-deathmatch round, 1..65535.",
      "Team Deathmatch Invulnerability Seconds": "Team-deathmatch spawn protection, seconds. 0 is none.",
      "Respawn Time": "Respawn delay, seconds. 0 is one tick.",
      "Total bandwidth limit for map downloads in bytes per second": "Total across all concurrent custom-map downloads, not per client.",
      "BanFile": "Operator ban list. Empty: bans do not survive a restart.",
      "MapDirectory": "Map PNG directory.",
      "RecordingDirectory": "Write replayable .trace recordings of the broadcast stream here. Empty: disabled.",
      "AuthServerHost": "Reward auth service host. Used only when Rewards is on.",
      "AuthServerPort": "Reward auth service port."
    },
    "Analytics": {
      "File": "Persistent player analytics. Empty: disabled. Addresses are stored as salted hashes only.",
      "GeoIpDatabase": "MaxMind .mmdb for the analytics map. Not bundled. Country-level databases resolve no US states."
    },
    "Classlimits": {
      "Scout": "Per-team limit for Scout. 255 = unlimited; 0: class banned.",
      "Pyro": "Per-team limit for Pyro. 255 = unlimited; 0: class banned.",
      "Soldier": "Per-team limit for Soldier. 255 = unlimited; 0: class banned.",
      "Heavy": "Per-team limit for Heavy. 255 = unlimited; 0: class banned.",
      "Demoman": "Per-team limit for Demoman. 255 = unlimited; 0: class banned.",
      "Medic": "Per-team limit for Medic. 255 = unlimited; 0: class banned.",
      "Engineer": "Per-team limit for Engineer. 255 = unlimited; 0: class banned.",
      "Spy": "Per-team limit for Spy. 255 = unlimited; 0: class banned.",
      "Sniper": "Per-team limit for Sniper. 255 = unlimited; 0: class banned.",
      "Quote": "Per-team limit for Quote. 255 = unlimited; 0: class banned."
    },
    "Log": {
      "File": "Also write the log to this file. Console output continues.",
      "MaxBytes": "Rotate the log file at this size, bytes. Min 1024.",
      "Keep": "Rotated log files kept, 0..1000."
    },
    "Limits": {
      "MaxReceiveBufferBytes": "Max unread bytes buffered per client before disconnect. A stock client holds a few bytes.",
      "MaxReceiveBytesPerTick": "Max bytes read per socket per tick. Bounds tick time, not memory.",
      "MaxSendBacklogBytes": "Max queued outbound bytes per client before disconnect. Bounds the connect-join-stop-reading attack.",
      "MaxInvalidCommandBytes": "Max invalid bytes per connection before disconnect.",
      "MaxAcceptsPerTick": "Max new connections accepted per tick.",
      "MaxJoiningConnections": "Max concurrent incomplete handshakes.",
      "MaxHandshakeSeconds": "Hard limit on join duration, regardless of activity. Separate from the 30 s idle timeout, which PING resets.",
      "ConnectionBurstPerAddress": "Max connections per address within the burst window. 0 disables rate limiting.",
      "ConnectionBurstWindowSeconds": "Burst window length, seconds.",
      "ChatBubbleIntervalMs": "Min interval between one player's chat bubbles, ms.",
      "TcpKeepAlive": "Detect vanished peers. Off: a dead client holds its slot ~15 min. Spectators send nothing, so this is the only liveness check.",
      "KeepAliveIdleSeconds": "Idle seconds before the first keepalive probe.",
      "KeepAliveIntervalSeconds": "Seconds between keepalive probes.",
      "KeepAliveProbeCount": "Unanswered probes before the peer is dropped."
    },
    "Nav": {
      "Directory": "Bot nav graph directory.",
      "Generate": "Build missing graphs on map change (required for custom maps). Kept in memory unless Save is on.",
      "Save": "Write generated graphs to the nav directory. Off by default.",
      "Distances": "Exact node-to-node A* heuristic instead of straight-line. Changes routes, not only speed: the search is weighted."
    },
    "Bots": {
      "Enabled": "Manage a bot population.",
      "FillToPlayers": "Target total players, humans included.",
      "MaxBots": "Hard cap on bots, applied after the fill target.",
      "MinHumans": "Humans required before any bot spawns. Below it the target is 0, so an empty server does not look full in the lobby.",
      "Difficulty": "1..5. 5 is full strength; lower tiers are deliberate handicaps.",
      "SuspendMinutes": "Duration of the /bots off chat command, 0..1440. 0 disables the command. Requires a chat plugin in ServerPluginList.",
      "NamePrefix": "Bot name prefix.",
      "ClassPolicy": "Class=name pairs choosing each class's driver; name is a checkpoint file name without extension, or 'scripted'. Unlisted classes: the loaded checkpoint that claims them, else scripted.",
      "Occupancy": "Penalise edges teammates already use so squads spread out. Behavioural: off drops match dispersion 0.785 to 0.634.",
      "Humanize": "Inside a fight, hold movement keys a few ticks at a time and make aim flick and correct like a person's. Behavioural: also weaker.",
      "SpreadTurn": "Spread each policy decision's aim turn over the ticks it is held for, instead of turning on the first. Behavioural.",
      "EngageBots": "Run the micro-controller in bot-vs-bot fights too. Costs tick time; does not improve play.",
      "HandoverBubbles": "Chat bubble on brain handover: z1 policy, z3 search, z2 scripted. Debug only; each bubble is a broadcast.",
      "AdaptiveBudget": "Lower the controller budget when ticks approach their deadline. Arbiter remains the upper bound.",
      "Mode": "Which of the four shipped brains runs. search plays best and costs most.",
      "Policy": "gg2-rl checkpoints for the policy and tiered modes, one per tier. A checkpoint is tied to the simulation it was trained on; its classes are read from the file.",
      "PolicyIgnoreStamp": "Bypass the SIM_UUID check that stops a policy running against a simulation it was not trained on. Both stamps are logged.",
      "Arbiter": "Bot-controller budget per tick, µs (tick = 33,300 µs). Empty: mode default.",
      "SearchThreads": "Rollout search threads. Affects cost only, never decisions."
    },
    "BotClasslimits": {
      "Scout": "Per-team bot limit for Scout. 255 = unlimited; 0 = disabled.",
      "Pyro": "Per-team bot limit for Pyro. 255 = unlimited; 0 = disabled.",
      "Soldier": "Per-team bot limit for Soldier. 255 = unlimited; 0 = disabled.",
      "Heavy": "Per-team bot limit for Heavy. 255 = unlimited; 0 = disabled.",
      "Demoman": "Per-team bot limit for Demoman. 255 = unlimited; 0 = disabled.",
      "Medic": "Per-team bot limit for Medic. 255 = unlimited; 0 = disabled.",
      "Engineer": "Per-team bot limit for Engineer. 255 = unlimited; 0 = disabled.",
      "Spy": "Per-team bot limit for Spy. 255 = unlimited; 0 = disabled.",
      "Sniper": "Per-team bot limit for Sniper. 255 = unlimited; 0 = disabled."
    },
    "Web": {
      "Port": "Admin panel port. Setting it enables the panel; 0 picks a free port.",
      "Enabled": "Host the admin panel. Explicit so it can be turned off; a Port key alone enables it.",
      "Bind": "Listen address. 127.0.0.1: local only; 0.0.0.0: remote. Remote without a certificate sends the password in cleartext.",
      "Password": "Admin password. Empty generates one, printed at startup; it survives a panel restart but not a new process.",
      "Certificate": "PKCS#12 certificate for HTTPS. Empty: plain HTTP. 'auto': self-signed, created on first start beside the config file, with a stable fingerprint for hub pinning.",
      "CertificatePassword": "Certificate password, if any.",
      "AllowFrom": "Allowed addresses and CIDR blocks, comma-separated. Empty: any. Loopback always allowed. Checked before TLS; on a fleet member, usually the hub's address.",
      "FleetMember": "Allow a fleet hub to manage this server by token. Requires a Certificate (auto works). The pairing code is printed at startup.",
      "FleetDirectory": "Make this panel a fleet hub. Holds the member list and tokens, relative to the config file. Empty: not a hub. A non-loopback hub requires a Certificate."
    },
    "Runtime": {
      "Speed": "Tick pacing. 1: real time; 'max': unpaced. Must be 1 with players connected.",
      "IdleWhenEmpty": "Tick at 2 Hz with no players or bots. The round clock slows with it.",
      "TickStats": "Log a tick-interval histogram every 30 s. Safe with players on.",
      "ControlPort": "Loopback-only JSON control port. Setting it enables it; 0 picks a free port. Executes commands against the live game.",
      "ControlPortEnabled": "Open the control port. Explicit so it can be turned off; a ControlPort key alone enables it."
    }
  },
  "editorPage": {
    "adopt": {
      "common": "Into all servers",
      "host": "Into this server's overrides",
      "note": "Settings where this server's file differs from the fleet settings. Tick those to keep.",
      "open": "Adopt…",
      "title": "Adopt"
    },
    "brand": "Server configuration",
    "close": "Close",
    "dashboard": "Dashboard",
    "filter": "Filter settings",
    "loading": "Loading…",
    "noChanges": "No changes.",
    "overriddenOnly": "overridden only",
    "preview": {
      "note": "Saved file as sent to this server; unsaved edits excluded. Passwords masked.",
      "open": "Preview gg2.ini",
      "title": "gg2.ini"
    },
    "push": {
      "changes": "What a push changes",
      "check": "Check",
      "deploy": "scripts/deploy.sh --config",
      "deployCheck": "scripts/deploy.sh --config --check",
      "deployHint": "Saving writes local files only. To deploy:",
      "open": "Push…",
      "server": "Server",
      "sub": "Servers apply a push when next empty.",
      "title": "Push",
      "unsaved": "Unsaved changes are not pushed; save first."
    },
    "revert": "Revert",
    "save": "Save",
    "scopes": "What to edit",
    "sections": "Sections",
    "title": "Server configuration — gg2-server"
  },
  "editor": {
    "notRunning": "Editor stopped.",
    "allServers": "All servers",
    "commonTabTitle": "common.ini: all servers, unless overridden",
    "hostTabTitle": "{name}: {file}",
    "hostFile": "{name}: {file}, overrides common.ini",
    "hostFileNotCreated": "{name}: {file} (not created), overrides common.ini",
    "commonFile": "common.ini: all servers, unless overridden",
    "thisServerGroup": "This server",
    "noOverridesMatch": "No matching overrides on this server.",
    "noMatch": "No settings match.",
    "hubPageTitle": "Fleet settings — gg2-server",
    "hubBrand": "Fleet settings",
    "notes": {
      "noFleetSettings": "No fleet settings yet: values below are defaults and Save creates them. To start from an existing server, select it and use Adopt… into all servers. Paths and [Web] are per-server and never pushed.",
      "noCommonIni": "No common.ini yet: values below are defaults and Save creates it. To start from a server's config, copy its /opt/gg2-server/shared/gg2.ini to {path} and reload.",
      "commonError": "common.ini: {error}"
    },
    "placeholder": {
      "secretSet": "set; type to replace",
      "secretNotSet": "not set",
      "commonResolved": "default (resolved per server)",
      "hostResolved": "default (resolved by this server)",
      "hostSecretSet": "set on this server; type to replace",
      "hostSecretType": "type to set on this server",
      "sharedSecretSet": "shared value set; type to override",
      "sharedSecretNotSet": "not set; type to set here"
    },
    "overrides": {
      "one": "{count} override",
      "other": "{count} overrides"
    },
    "overriddenOn": "Overridden on {names}",
    "overriddenNote": {
      "one": "Overridden on {names}; changes here do not apply there.",
      "other": "Overridden on {names}; changes here do not apply there."
    },
    "sharedTag": "shared",
    "sharedTagTitle": "From common.ini. Enter a value to override on this server.",
    "useShared": "use shared",
    "useSharedTitle": "Remove this server's value and fall back to common.ini.",
    "status": {
      "clean": "No unsaved changes.",
      "dirty": {
        "one": "{count} unsaved change. Save validates every server's file before writing.",
        "other": "{count} unsaved changes. Save validates every server's file before writing."
      }
    },
    "toast": {
      "saveNoAnswer": "No response from editor; nothing saved.",
      "notSaved": "Not saved: {error}",
      "nothingToWrite": "Nothing to write",
      "saved": "Saved {files}",
      "hostError": "{name}: {error}",
      "unsavedFirst": "Save or revert unsaved changes first.",
      "nothingTicked": "Nothing selected.",
      "notAdopted": "Not adopted: {error}",
      "adopted": {
        "one": "Adopted {count} setting from {name}",
        "other": "Adopted {count} settings from {name}"
      },
      "pushedRestarting": "{name}: pushed, restarting",
      "pushed": "{name}: pushed; applies when next empty"
    },
    "preview": {
      "title": "{host} — gg2.ini",
      "sub": "{host} — sha256 {sha256}"
    },
    "push": {
      "asking": "Querying servers…",
      "noAnswer": "No response from hub.",
      "upToDate": "Up to date.",
      "sameSettings": "Settings identical; a push only marks it managed.",
      "changes": {
        "one": "{count} line change.",
        "other": "{count} lines change."
      },
      "changesNeverPushed": {
        "one": "{count} line change; never pushed, replaces a hand-written file.",
        "other": "{count} lines change; never pushed, replaces a hand-written file."
      },
      "changesEdited": {
        "one": "{count} line change; edited on the server since the last push.",
        "other": "{count} lines change; edited on the server since the last push."
      },
      "diff": "Diff",
      "overwrite": "Overwrite",
      "push": "Push",
      "pushRestart": "Push & restart",
      "pushRestartTitle": "Restart immediately, disconnecting players. Otherwise applied when next empty.",
      "noMembers": "This hub has no members."
    },
    "adopt": {
      "button": "Adopt…",
      "buttonTitle": "Import settings this server has that the fleet settings lack",
      "title": "Adopt from {name}",
      "nothingDiffers": "No differences from the fleet settings.",
      "differ": {
        "one": "{count} setting differs",
        "other": "{count} settings differ"
      },
      "values": "fleet: {fleet}  →  this server: {member}",
      "empty": "(empty)",
      "secrets": "Secrets not compared (values never leave the server): {names}."
    },
    "diff": {
      "title": "{name}: push diff",
      "sub": "now {now} → after {after}",
      "none": "(no line changes)"
    },
    "confirm": {
      "pushRestart": "Push to {name} and restart now? Connected players are dropped.",
      "overwrite": "Overwrite {name}'s configuration? Check the diff first; the old file survives only as gg2.ini.prev."
    }
  }
};

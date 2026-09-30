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
      "mostPlayed": "Most time played",
      "perDay": "Per day",
      "place": "Place",
      "places": "Places",
      "placesEmpty": "Nowhere resolved yet.",
      "played": "Played",
      "player": "Player",
      "players": "Players",
      "range14d": "14d",
      "range30d": "30d",
      "range90d": "90d",
      "rangeAll": "all",
      "title": "Analytics",
      "topEmpty": "Nobody has connected yet.",
      "us": "United States",
      "whatToPlot": "What to plot",
      "where": "Where they connect from",
      "world": "World"
    },
    "boot": {
      "loading": "Loading"
    },
    "bots": {
      "brain": "Brain",
      "brainChange": "Changing it replaces every bot.",
      "brainKey": "brain",
      "brainLabel": "Bot brain",
      "fewer": "Fewer bots",
      "fillHint": "Players in total, people and bots together. 0 turns bots off. Humans always displace bots.",
      "fillTo": "Fill the server to",
      "limit": "Bot limit",
      "limitHint": "The most bots at once, whatever the fill.",
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
      "empty": "Nothing said yet.",
      "messageLabel": "Message",
      "placeholder": "Say something",
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
      "title": "Are you sure?"
    },
    "fleet": {
      "allOffline": "Take all offline",
      "allOnline": "Bring all online",
      "build": "Build",
      "empty": "No members yet. Pair one below.",
      "endRoundEverywhere": "End the round everywhere",
      "everyServer": "Every server at once",
      "map": "Map",
      "member": "Member",
      "name": "Name",
      "namePlaceholder": "us-east",
      "pair": "Pair",
      "pairHint": "On the server to add, set <code>[Web] Certificate=auto</code>, <code>FleetMember=1</code>, <code>Bind=0.0.0.0</code> and <code>AllowFrom=</code> this hub's address, then restart it. It prints a pairing code at startup.",
      "pairTitle": "Pair a server",
      "pairingCode": "Pairing code",
      "players": "Players",
      "say": "Say",
      "sayPlaceholder": "Say something on every server",
      "server": "Server",
      "settings": "Fleet settings",
      "sub": "The servers this hub manages.",
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
      "limitHint": "Seats for people, 1..48. Bots do not take one. Lowering it removes nobody.",
      "liveNote": "Changes on this page apply now and last until the server restarts. They are not saved to the config file; Settings is for that.",
      "moreSeats": "More seats",
      "newPassword": "New password",
      "offlineHint": "Disconnects every player, refuses new connections and removes the bots. This panel stays up; bring it back online from here.",
      "passwordHint": "Asked of the next players to join; nobody already in is removed.",
      "passwordNone": "none",
      "playerLimit": "Player limit",
      "restartRound": "Restart round",
      "roundHint": "End round goes to the next map in the rotation; restart plays this map again from the start. Both run the usual ten-second round end.",
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
      "teleportTitle": "Click the map to move the selected player there",
      "title": "Live map"
    },
    "log": {
      "follow": "Follow",
      "title": "Log"
    },
    "login": {
      "insecure": "This connection is not encrypted. Your password crosses the network in the clear.",
      "password": "Password",
      "signIn": "Sign in"
    },
    "mapCard": {
      "add": "Add",
      "addMap": "Add a map",
      "changeHint": "Ends the round immediately for everyone on the server.",
      "changeTo": "Change to",
      "go": "Go",
      "rotation": "Rotation",
      "rotationEmpty": "No rotation: the server stays on the map it is on.",
      "search": "Search maps",
      "title": "Map"
    },
    "perf": {
      "fiveMinutesAgo": "5 minutes ago",
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
      "empty": "Nobody is connected.",
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
      "restartNote": "Saved settings take effect when the server restarts.",
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
      "joinsNote": "joins since start, repeats counted",
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
      "noAnswer": "the server did not answer"
    },
    "done": {
      "botFill": "Bot fill set to {fill}",
      "map": "Changing map to {name}",
      "kick": "Kicked",
      "ban": "Banned",
      "offline": "Server offline",
      "online": "Server back online",
      "endRound": "Round ended",
      "restartRound": "Restarting the round",
      "autobalanceOn": "Autobalance on",
      "autobalanceOff": "Autobalance off",
      "playerLimit": "Player limit {value}",
      "passwordSet": "Join password set",
      "passwordRemoved": "Join password removed",
      "rotation": "Rotation updated",
      "team": "Moved to {team}",
      "botLimit": "Bot limit {max}",
      "botBrain": "Brain {mode}; bots replaced",
      "teleport": "Teleported",
      "configSet": "Settings saved",
      "restart": "Restarting",
      "generic": "Done"
    },
    "live": {
      "live": "live",
      "lost": "reconnecting",
      "connecting": "connecting"
    },
    "banner": {
      "changingMap": "Changing map to {map} in {seconds}s"
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
      "idle": "idle (2 Hz, nobody on)",
      "running": "running",
      "slots": "/ {slots}"
    },
    "chat": {
      "hintPlugin": "Sent as {name} through {plugin}.",
      "theServer": "the server",
      "hintNotice": "No chat plugin is loaded, so this shows as an on-screen notice to every player.",
      "teamOnly": "(team)"
    },
    "bots": {
      "suspended": {
        "one": "A player turned the bots off with /bots for another {count} minute. Setting a bot count here ends it.",
        "other": "A player turned the bots off with /bots for another {count} minutes. Setting a bot count here ends it."
      },
      "blocked": {
        "one": "No bots will spawn until {count} human is connected (--bots-min-humans). There are {humans}.",
        "other": "No bots will spawn until {count} humans are connected (--bots-min-humans). There are {humans}."
      }
    },
    "brain": {
      "arbiter": "Arbiter {budget} µs a tick, {spent} spent on the last one.",
      "noBots": "No bots are on the server, so nothing is driving.",
      "humansOnly": "Controllers take the wheel against a human enemy only (--bot-engages-bots opens them to bot fights).",
      "drives": "Drives {classes}.",
      "tierEmpty": "no bots",
      "tierBots": {
        "one": "{count} bot",
        "other": "{count} bots"
      },
      "tierShare": "{driving} driving right now. {micro} of {polls} ticks driven since each bot spawned.",
      "tierScripted": "The macro layer alone — every controller refuses these classes.",
      "needsPolicy": "Needs a policy checkpoint (--policy) at startup",
      "changeTitle": "Change the bot brain?",
      "changeBody": "Every bot is removed and added back running {mode}. Search and the policy brains cost more tick time than scripted."
    },
    "roster": {
      "noMatch": "No player matches that filter.",
      "nobody": "Nobody is connected.",
      "botTag": "BOT",
      "brainShare": "{brain} has driven {share}% of this bot's ticks ({micro} of {polls})",
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
      "changeTitle": "Change the map?",
      "changeBody": "Everyone on the server is moved to {name} and the round ends now.",
      "tip": "{name} · {class} · {hp} hp",
      "selectFirst": "select a player in the table first"
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
      "aria": "{title} in {unit} over the last five minutes",
      "minutesAgo": {
        "one": "{count} minute ago",
        "other": "{count} minutes ago"
      },
      "waiting": "waiting for the first sample",
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
      "moveSpectators": "Move to Spectators",
      "kick": "Kick",
      "kickTitle": "Kick this player?",
      "kickBody": "{name} is disconnected immediately.",
      "ban": "Ban",
      "banTitle": "Ban this player?",
      "banBodyAddress": "{name} is disconnected and their address ({address}) is banned permanently. Anyone else on that address is banned too.",
      "banBody": "{name} is disconnected and their address is banned permanently. Anyone else on that address is banned too.",
      "gone": "{name} left or moved slot; nothing was done",
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
      "hintOffline": "Offline: connections are refused and no bots run. Settings changed here still apply.",
      "hintOnline": "Disconnects every player, refuses new connections and removes the bots. This panel stays up; bring it back online from here.",
      "askTitle": "Take the server offline?",
      "askNobody": "Nobody is connected.",
      "askPeople": {
        "one": "The {count} connected player is disconnected.",
        "other": "All {count} connected players are disconnected."
      },
      "askBody": "New connections are refused and the bots are removed until you bring it back online. The process and this panel keep running."
    },
    "password": {
      "set": "set",
      "none": "none"
    },
    "settings": {
      "unsaved": {
        "one": "{count} change not saved.",
        "other": "{count} changes not saved."
      },
      "waiting": {
        "one": "{count} saved setting is waiting for a restart.",
        "other": "{count} saved settings are waiting for a restart."
      },
      "agree": "The file and the running server agree.",
      "takeEffect": "Settings take effect when the server restarts.",
      "loading": "Loading…",
      "noAnswer": "The server did not answer.",
      "noFile": "no config file — started without --config",
      "readonly": "This server was started without --config, so there is nowhere to save to. Restart it with --config <path> — the file does not have to exist yet — and these fields become editable.",
      "managed": "This file is managed by scripts/deploy.sh --config. A save here applies to this server only, and the next push will stop for it until it is copied into deploy/config or the push is forced.",
      "editedSincePush": "It has been edited here since it was last pushed.",
      "pushWaiting": "A pushed configuration is waiting: the server applies it the next time nobody is connected.",
      "noMatch": "Nothing matches that filter.",
      "pendingTag": "restart pending",
      "pendingTitle": "Saved; the running server still has the old value",
      "pinnedTag": "pinned",
      "pinnedTitle": "Set by a command-line flag, which beats the file",
      "pinnedNote": "Pinned on the command line: this server will keep using the flag’s value after a restart, whatever is saved here.",
      "secretSet": "set — type to replace",
      "secretNotSet": "not set",
      "saved": {
        "one": "Saved {count} setting",
        "other": "Saved {count} settings"
      },
      "stillPinned": "Still pinned on the command line: {names}"
    },
    "restart": {
      "title": "Restart the server?",
      "body": "Every player is disconnected and the round in progress is lost. The process stays up and comes back with the configuration file as it is now.",
      "toast": "Restarting — the panel reconnects on its own",
      "afterSave": "The settings you just saved will be applied."
    },
    "login": {
      "wrongPassword": "wrong password"
    },
    "analytics": {
      "refused": "the server refused"
    },
    "round": {
      "endTitle": "End the round?",
      "endBody": "Nobody wins, and the server moves on to the next map in the rotation after the usual ten seconds.",
      "restartTitle": "Restart the round?",
      "restartBody": "Nobody wins, and {map} starts again from the beginning after the usual ten seconds.",
      "thisMap": "this map"
    },
    "fleet": {
      "hubTab": "Hub",
      "hubTitle": "This server: the hub itself",
      "tabTitle": {
        "one": "{name} — {map}, {count} player",
        "other": "{name} — {map}, {count} players"
      },
      "noMap": "no map",
      "unreachable": "unreachable",
      "notMember": "not a member of this hub",
      "asking": "Asking every member…",
      "hubNoAnswerSentence": "The hub did not answer.",
      "players": "{humans} + {bots} bots / {slots}",
      "tick": "{ms} ms",
      "open": "Open",
      "remove": "Remove",
      "members": {
        "one": "{count} member, pinned to its own certificate.",
        "other": "{count} members, each pinned to its own certificate."
      },
      "removeTitle": "Remove {name}?",
      "removeBody": "The hub forgets its token and stops managing it. The server itself keeps running; pairing it again needs its pairing code.",
      "removed": "Removed {name}",
      "hubNoAnswer": "the hub did not answer",
      "memberRefused": "{name}: {error}",
      "doneOn": {
        "one": "{done} on {count} server",
        "other": "{done} on {count} servers"
      },
      "said": "Said",
      "offlineTitle": "Take every server offline?",
      "offlineBody": "Everyone on every server is disconnected and new connections are refused until you bring them back online.",
      "offline": "Offline",
      "online": "Online",
      "endRoundTitle": "End the round everywhere?",
      "endRoundBody": "Nobody wins; every server moves on to its next map.",
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
    "off": "Analytics are off: nothing is counted and nothing is written. To turn them on, {hint}, then restart.",
    "offHint": "set an analytics file",
    "notCollecting": "not collecting",
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
      "timePlayedNote": "connected time, everybody",
      "peak": "peak at once",
      "peakUtc": "{time} UTC",
      "peakOnHost": "{host}, {at}",
      "today": "today",
      "todayNew": "{count} new",
      "todayNobody": "nobody yet",
      "connected": "connected now",
      "connectedNote": {
        "one": "player",
        "other": "players"
      }
    },
    "days": {
      "none": "no days recorded yet",
      "peak": "peak {count}"
    },
    "map": {
      "noGeometry": "the map geometry could not be loaded",
      "onMap": {
        "one": "{n} player on the map",
        "other": "{n} players on the map"
      },
      "byState": "{n} placed by state",
      "outOfView": "{n} outside the United States",
      "noMarker": "{n} in a place this map has no marker for",
      "noState": "{n} with no state — {type} is a country-level database",
      "unresolved": "{n} the database could not place",
      "noGeoip": "No geo database is loaded, so nobody can be placed. Pass --geoip <path.mmdb>",
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
      "player": "A \"player\" here is an address, hashed with a per-file salt — the protocol has no account, so everybody behind one address counts once and one person on a mobile network counts more than once.",
      "fleet": {
        "one": "These are {count} server's figures added together. Each salts its hashes differently, so somebody who played on two of them is two players here, and a day's peak is the busiest single server's.",
        "other": "These are {count} servers' figures added together. Each salts its hashes differently, so somebody who played on two of them is two players here, and a day's peak is the busiest single server's."
      },
      "geoipRegions": "Geography from {type}, built {built} (with subdivisions).",
      "geoipCountries": "Geography from {type}, built {built} (countries only).",
      "noGeoip": "No geo database is loaded.",
      "trimmed": "{n} of the oldest player records have been dropped to stay under the {cap} kept, so the all-time total counts a returning player from before that twice.",
      "kept": "{n} of {cap} player records kept."
    }
  },
  "settings": {
    "listPlaceholder": "comma,separated"
  },
  "settingHelp": {
    "Settings": {
      "HostingPort": "The game port clients connect to. 0 asks the OS for a free one.",
      "PlayerLimit": "Seats for network clients, 1..48. Slot 0 and bots do not count against it.",
      "MultiClientLimit": "Connections allowed from one address. Loopback is exempt.",
      "PlayerName": "The name of reserved roster slot 0, a permanent spectator.",
      "UseLobby": "Announce this server to ganggarrison.com so it appears in the client's server list. The only outbound connection this project makes.",
      "Rewards": "Answer REWARD_REQUEST, relaying each player's reward id to the auth server."
    },
    "Server": {
      "ServerName": "Shown in the client's server browser and on join.",
      "WelcomeMessage": "Sent to each joiner. Empty means none.",
      "ChatJoinMessage": "A chat line sent to each player as they join chat. Empty means none. Needs a chat plugin in ServerPluginList; a client without one never sees it.",
      "Password": "The game password. Empty means none, and no PASSWORD_REQUEST.",
      "Map": "The map to open on. Empty means the first entry of the rotation.",
      "Rotation": "The map rotation, in order. A map may appear more than once.",
      "ShuffleRotation": "How the rotation is ordered at startup. arena-last shuffles but does not open on an arena map.",
      "VotableMaps": "Maps a plugin map vote may offer. Empty means the rotation, else every map in the maps directory.",
      "ServerPluginList": "Server-sent plugins clients are asked to load, comma-separated.",
      "ServerPluginsRequired": "Clients must load the plugin list to join.",
      "AutoBalance": "Move a player across when one team is two larger.",
      "CapLimit": "Captures that win a round, 1..255. 0 is clamped to 1, not to unlimited.",
      "Time Limit": "Round length in minutes, 1..255. 0 is clamped to 1.",
      "Deathmatch Kill Limit": "Kills that win a team-deathmatch round, 1..65535.",
      "Team Deathmatch Invulnerability Seconds": "Spawn protection in team deathmatch. 0 really is none.",
      "Respawn Time": "Seconds a dead player waits. 0 becomes a single tick.",
      "Total bandwidth limit for map downloads in bytes per second": "TOTAL across every custom-map download in flight, not per client.",
      "BanFile": "Where operator bans are kept. Empty means they do not survive a restart.",
      "MapDirectory": "Where the map PNGs are read from.",
      "RecordingDirectory": "Write replayable .trace recordings of this server's broadcast stream here. Empty records nothing — recording a game people are playing is a decision.",
      "AuthServerHost": "The reward auth service. Only contacted when Rewards is on.",
      "AuthServerPort": "That service's port."
    },
    "Analytics": {
      "File": "Count who plays here and keep it across restarts. Empty counts nothing. Addresses are stored as salted hashes, never as addresses.",
      "GeoIpDatabase": "A MaxMind .mmdb used to put players on the analytics map. None ships with this server. A country-level database resolves no US states."
    },
    "Classlimits": {
      "Scout": "How many Scouts ONE TEAM may hold. 255 is unlimited; 0 BANS the class.",
      "Pyro": "How many Pyros ONE TEAM may hold. 255 is unlimited; 0 BANS the class.",
      "Soldier": "How many Soldiers ONE TEAM may hold. 255 is unlimited; 0 BANS the class.",
      "Heavy": "How many Heavys ONE TEAM may hold. 255 is unlimited; 0 BANS the class.",
      "Demoman": "How many Demomans ONE TEAM may hold. 255 is unlimited; 0 BANS the class.",
      "Medic": "How many Medics ONE TEAM may hold. 255 is unlimited; 0 BANS the class.",
      "Engineer": "How many Engineers ONE TEAM may hold. 255 is unlimited; 0 BANS the class.",
      "Spy": "How many Spys ONE TEAM may hold. 255 is unlimited; 0 BANS the class.",
      "Sniper": "How many Snipers ONE TEAM may hold. 255 is unlimited; 0 BANS the class.",
      "Quote": "How many Quotes ONE TEAM may hold. 255 is unlimited; 0 BANS the class."
    },
    "Log": {
      "File": "TEE the log into this file. The console sink stays wired — the console is the record an operator has when the thing that broke is the disk.",
      "MaxBytes": "Rotate the log file at this size. At least 1024.",
      "Keep": "Rotated log files to keep, 0..1000."
    },
    "Limits": {
      "MaxReceiveBufferBytes": "Unread bytes one client may have buffered before it is dropped. A stock client's steady state is a few bytes.",
      "MaxReceiveBytesPerTick": "Bytes read from one socket in one tick, however many are waiting. This one is about the CLOCK, not about memory.",
      "MaxSendBacklogBytes": "Queued bytes one client may leave unread before it is dropped. The bound on the cheapest attack there is: connect, join, stop reading.",
      "MaxInvalidCommandBytes": "Garbage one connection may send before it is dropped.",
      "MaxAcceptsPerTick": "New connections taken off the listener in one tick.",
      "MaxJoiningConnections": "Half-finished handshakes held at once.",
      "MaxHandshakeSeconds": "Longest a connection may take to finish joining, however active it is. NOT the 30s idle timeout — a PING resets that one and cannot reset this.",
      "ConnectionBurstPerAddress": "Connections one address may open inside the window below. 0 turns the rate limit off.",
      "ConnectionBurstWindowSeconds": "The window that burst is counted over.",
      "ChatBubbleIntervalMs": "Smallest gap between one player's chat bubbles.",
      "TcpKeepAlive": "Probe for peers that vanished. Off, a client whose machine dies holds a roster slot for ~15 minutes. A spectator sends nothing, so this is the mechanism rather than a heartbeat.",
      "KeepAliveIdleSeconds": "Quiet seconds before the first keepalive probe.",
      "KeepAliveIntervalSeconds": "Seconds between probes.",
      "KeepAliveProbeCount": "Unanswered probes before the peer is declared gone."
    },
    "Nav": {
      "Directory": "Where the bot nav graphs are read from. Empty is legal: bots then fight but do not walk.",
      "Generate": "Build a missing graph at the map change, which is what makes a custom map navigable. Held in memory unless Save is on.",
      "Save": "Write a generated graph into the nav directory. Off by default because it writes files the operator did not ask for.",
      "Distances": "Give A* exact node-to-node distances instead of a straight-line estimate. OFF IS A DIFFERENT ARM, not a slower one — the search is weighted, so this picks the route as well as the speed."
    },
    "Bots": {
      "Enabled": "Manage a bot population at all.",
      "FillToPlayers": "How many players the server wants IN TOTAL, humans included.",
      "MaxBots": "A hard ceiling on top of the fill target.",
      "MinHumans": "Humans that must be connected before ANY bot is spawned. Below it the target is ZERO, not the fill — an empty server full of bots looks full in the lobby list and is not.",
      "Difficulty": "1 to 5. 5 is 'as good as the layer gets'; the lower tiers are a deliberate handicap ladder.",
      "SuspendMinutes": "How long the /bots off chat command takes the bots away for, 0..1440. 0 means the command does not exist on this server. Needs a chat plugin in ServerPluginList - it is the only way a player can type /bots.",
      "NamePrefix": "What a bot's name starts with.",
      "ClassPolicy": "Class=name entries deciding who drives each class, where name is a checkpoint's file name without its extension or 'scripted'. Anything not named here keeps the default: whichever loaded checkpoint's own class set claims it, scripted if none does.",
      "Occupancy": "Price an edge a team-mate is already walking above its honest cost, so a squad fans out. A BEHAVIOUR switch: off, match dispersion falls 0.785 to 0.634 and nothing replaces it.",
      "EngageBots": "Run the expensive micro-controller in bot-vs-bot fights too. It does not make the bots better; it makes those fights cost more.",
      "HandoverBubbles": "Bots put up a chat bubble whenever their brain changes hands: z1 a policy took the wheel, z3 the search did, z2 the scripted layer took it back. For debugging; every bubble is a broadcast.",
      "AdaptiveBudget": "Let the server walk the controller budget down when ticks crowd their deadline. The arbiter setting stays an upper bound.",
      "Mode": "Which of the four shipped brains runs. search is the better bot and the far more expensive one.",
      "Policy": "The gg2-rl checkpoints policy and tiered modes load, one tier each. A checkpoint is DATED by the simulation it was trained against, and the classes it drives are read off the file.",
      "PolicyIgnoreStamp": "BYPASS the SIM_UUID guard, which exists to stop a policy running against a simulation it was not trained on. The server prints both stamps.",
      "Arbiter": "MICROSECONDS of bot-controller work per tick, against a 33,300us tick. Empty lets the mode pick its default.",
      "SearchThreads": "Threads the rollout search uses. Threads change NO decision, so this is a pure cost knob."
    },
    "BotClasslimits": {
      "Scout": "How many Scout BOTS one team may hold. Humans do not count and are not limited by this. 255 is unlimited; 0 keeps bots off the class.",
      "Pyro": "How many Pyro BOTS one team may hold. Humans do not count and are not limited by this. 255 is unlimited; 0 keeps bots off the class.",
      "Soldier": "How many Soldier BOTS one team may hold. Humans do not count and are not limited by this. 255 is unlimited; 0 keeps bots off the class.",
      "Heavy": "How many Heavy BOTS one team may hold. Humans do not count and are not limited by this. 255 is unlimited; 0 keeps bots off the class.",
      "Demoman": "How many Demoman BOTS one team may hold. Humans do not count and are not limited by this. 255 is unlimited; 0 keeps bots off the class.",
      "Medic": "How many Medic BOTS one team may hold. Humans do not count and are not limited by this. 255 is unlimited; 0 keeps bots off the class.",
      "Engineer": "How many Engineer BOTS one team may hold. Humans do not count and are not limited by this. 255 is unlimited; 0 keeps bots off the class.",
      "Spy": "How many Spy BOTS one team may hold. Humans do not count and are not limited by this. 255 is unlimited; 0 keeps bots off the class.",
      "Sniper": "How many Sniper BOTS one team may hold. Humans do not count and are not limited by this. 255 is unlimited; 0 keeps bots off the class."
    },
    "Web": {
      "Port": "The admin panel's port. Naming it at all turns the panel ON; 0 asks the OS for a free one.",
      "Enabled": "Host the admin panel. Written explicitly so turning it OFF is expressible; a Port key on its own turns it on.",
      "Bind": "Where the panel listens. 127.0.0.1 is local only; 0.0.0.0 is what makes it remote, and remote without a certificate sends the admin password in the clear.",
      "Password": "The admin password. EMPTY DOES NOT MEAN NO PASSWORD — it means one is generated and printed to the console at startup. A generated one lasts as long as the process: a restart from this panel carries it, so pressing restart does not lock you out, and a new process generates a new one.",
      "Certificate": "A PKCS#12 certificate to serve HTTPS with. Empty is plain HTTP. 'auto' makes a self-signed one on first start and keeps it beside the config file, so its fingerprint stays the same for a fleet hub to pin.",
      "CertificatePassword": "That certificate's password, if it has one.",
      "AllowFrom": "Addresses and CIDR blocks allowed to connect at all, comma-separated. Empty allows anyone. Loopback is always allowed. Checked before TLS or any HTTP, so on a fleet member this is normally just the hub's address.",
      "FleetMember": "Let a fleet hub manage this server with a token instead of the password. Needs a Certificate (auto is fine). The pairing code to give the hub is printed at every start.",
      "FleetDirectory": "Make this panel a fleet hub that manages other servers. The directory holds the member list (with every member's token), relative to the config file. Empty is not a hub. A hub bound beyond loopback needs a Certificate."
    },
    "Runtime": {
      "Speed": "Tick pacing. 1 is real time; 'max' is unpaced. MUST STAY AT 1 on a server anybody is connected to.",
      "IdleWhenEmpty": "Drop to 2 Hz when nobody is connected and no bots are running. The round clock slows with it, which is why an idle server's round is not wall-clock Time Limit.",
      "TickStats": "Log a histogram of tick intervals every 30 seconds. Pacing only, so it is safe with players on.",
      "ControlPort": "The loopback-only JSON control port. Naming it at all turns it ON; 0 asks the OS for a free one. It executes commands against a live game.",
      "ControlPortEnabled": "Open the control port at all. A ControlPort key on its own turns it on; this is how it is turned back off."
    }
  },
  "editorPage": {
    "adopt": {
      "common": "Into all servers",
      "host": "Into this server's overrides",
      "note": "Where this server's own file differs from what the fleet settings would give it. Tick what to keep.",
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
      "note": "What this server is sent, as saved on disk — unsaved edits are not in it. Passwords are masked.",
      "open": "Preview gg2.ini",
      "title": "gg2.ini"
    },
    "push": {
      "changes": "What a push changes",
      "check": "Check",
      "deploy": "scripts/deploy.sh --config",
      "deployCheck": "scripts/deploy.sh --config --check",
      "deployHint": "Saving writes files on this machine only. To send them to the servers:",
      "open": "Push…",
      "server": "Server",
      "sub": "Each server applies a push the next time nobody is connected.",
      "title": "Push",
      "unsaved": "Unsaved changes are not pushed. Save first."
    },
    "revert": "Revert",
    "save": "Save",
    "scopes": "What to edit",
    "sections": "Sections",
    "title": "Server configuration — gg2-server"
  },
  "editor": {
    "notRunning": "The editor is not running any more.",
    "allServers": "All servers",
    "commonTabTitle": "common.ini — every server, unless it overrides a setting",
    "hostTabTitle": "{name} — {file}",
    "hostFile": "{name} — {file} — wins over common.ini",
    "hostFileNotCreated": "{name} — {file} (not created yet) — wins over common.ini",
    "commonFile": "common.ini — every server, unless a server overrides the setting",
    "thisServerGroup": "This server",
    "noOverridesMatch": "This server overrides nothing that matches.",
    "noMatch": "Nothing matches that filter.",
    "hubPageTitle": "Fleet settings — gg2-server",
    "hubBrand": "Fleet settings",
    "notes": {
      "noFleetSettings": "There are no fleet settings yet, so every value below is a default and Save creates them. To start from a server you already run, choose it above, use Adopt… and take its values into all servers. Paths and the [Web] section are not here: each server keeps its own, and a push never changes them.",
      "noCommonIni": "There is no common.ini yet, so every value below is a default and Save creates the file. To start from a server's current configuration instead, copy its /opt/gg2-server/shared/gg2.ini to {path} and reload.",
      "commonError": "common.ini: {error}"
    },
    "placeholder": {
      "secretSet": "set — type to replace",
      "secretNotSet": "not set",
      "commonResolved": "default — each server resolves its own",
      "hostResolved": "default — this server resolves its own",
      "hostSecretSet": "set on this server — type to replace",
      "hostSecretType": "type to set on this server",
      "sharedSecretSet": "shared value set — type to override",
      "sharedSecretNotSet": "not set — type to set here"
    },
    "overrides": {
      "one": "{count} override",
      "other": "{count} overrides"
    },
    "overriddenOn": "Overridden on {names}",
    "overriddenNote": {
      "one": "Overridden on {names} — a change here does not reach it.",
      "other": "Overridden on {names} — a change here does not reach them."
    },
    "sharedTag": "shared",
    "sharedTagTitle": "From common.ini. Type a value to override it on this server.",
    "useShared": "use shared",
    "useSharedTitle": "Remove this server’s own value; it goes back to common.ini’s.",
    "status": {
      "clean": "No unsaved changes.",
      "dirty": {
        "one": "{count} unsaved change. Saving checks every server's file before writing any.",
        "other": "{count} unsaved changes. Saving checks every server's file before writing any."
      }
    },
    "toast": {
      "saveNoAnswer": "The editor did not answer. Nothing was saved.",
      "notSaved": "Not saved: {error}",
      "nothingToWrite": "Nothing to write",
      "saved": "Saved {files}",
      "hostError": "{name}: {error}",
      "unsavedFirst": "Save or revert your unsaved changes first.",
      "nothingTicked": "Nothing ticked.",
      "notAdopted": "Not adopted: {error}",
      "adopted": {
        "one": "Adopted {count} setting from {name}",
        "other": "Adopted {count} settings from {name}"
      },
      "pushedRestarting": "{name}: pushed, restarting",
      "pushed": "{name}: pushed; it applies the next time nobody is connected"
    },
    "preview": {
      "title": "{host} — gg2.ini",
      "sub": "{host} — sha256 {sha256}"
    },
    "push": {
      "asking": "Asking every server…",
      "noAnswer": "The hub did not answer.",
      "upToDate": "Up to date.",
      "sameSettings": "Same settings; a push only marks it managed.",
      "changes": {
        "one": "{count} line change.",
        "other": "{count} lines change."
      },
      "changesNeverPushed": {
        "one": "{count} line change — never pushed: replaces a hand-written file.",
        "other": "{count} lines change — never pushed: replaces a hand-written file."
      },
      "changesEdited": {
        "one": "{count} line change — edited on the server since the last push.",
        "other": "{count} lines change — edited on the server since the last push."
      },
      "diff": "Diff",
      "overwrite": "Overwrite",
      "push": "Push",
      "pushRestart": "Push & restart",
      "pushRestartTitle": "Restart at once, dropping anyone connected. Otherwise the server applies it the next time it is empty.",
      "noMembers": "This hub has no members."
    },
    "adopt": {
      "button": "Adopt…",
      "buttonTitle": "Take settings this server has that the fleet settings do not",
      "title": "Adopt from {name}",
      "nothingDiffers": "Nothing differs: this server already has what the fleet settings give it.",
      "differ": {
        "one": "{count} setting differs",
        "other": "{count} settings differ"
      },
      "values": "fleet: {fleet}  →  this server: {member}",
      "empty": "(empty)",
      "secrets": "Not compared, because a secret's value never leaves a server: {names}."
    },
    "diff": {
      "title": "{name} — what a push changes",
      "sub": "now {now} → after {after}",
      "none": "(no line changes)"
    },
    "confirm": {
      "pushRestart": "Push to {name} and restart it now? Everyone connected to it is dropped.",
      "overwrite": "Overwrite {name}'s configuration? Look at the diff first: what it loses is not kept anywhere but gg2.ini.prev."
    }
  }
};

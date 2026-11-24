# Player analytics — `gg2-server --analytics`

Who has played on this server, for how long, and roughly from where, kept in one
file that survives a restart. The admin panel's **Analytics** view draws it; the
control port's `analytics` verb returns the same object.

```powershell
# count, and keep it across restarts
gg2-server --map ctf_truefort --web 8191 --analytics data/analytics.tsv

# and put players on a map, using a database you downloaded
gg2-server --map ctf_truefort --web 8191 --analytics data/analytics.tsv `
           --geoip data/GeoLite2-City.mmdb
```

## Off unless asked for

Neither flag is a default and neither is implied by `--web`. With no
`--analytics` nothing is counted, nothing is written, and the panel's view says
which setting turns it on. This is `--record`'s rule — keeping a durable record
of the people who played on a server is a decision an operator makes, not one
they discover afterwards.

| Flag | `[Analytics]` key | |
|---|---|---|
| `--analytics <path>` | `File` | The store. Empty counts nothing. |
| `--geoip <path>` | `GeoIpDatabase` | A MaxMind `.mmdb`. Empty means no geography. Only opened when `File` is set. |

## ⛔ A "player" is an address, and that is wrong in both directions

The protocol has no account and no durable identity. A name is twenty bytes the
client chooses and nothing else on the wire distinguishes one person from
another, so the only thing uniqueness can key on is the address — which counts a
household behind one address as one player, and one person on a mobile network
as a new player most days. Every "unique" figure this feature produces inherits
that, and the panel says so under the tables rather than in a file nobody opens.
`BanList` makes the same argument at more length for the same reason.

**What is measured is connected time**, from the completed join to the socket
closing, whatever the player was doing with it. A spectator counts.

**Bots are never counted.** They have no connection, so nothing begins a session
for them. `AnalyticsServerTests.BotsAreNeverCounted` is the gate, with a human on
the same server as its negative control.

## What the file holds

Tab-separated, one record per line, written whole through a temporary file and a
move — `BanList.Save`'s argument, because a half-written file read after a crash
would report a truncated history as the real one.

| Record | |
|---|---|
| `!meta` | The salt, the date the file starts at, and the all-time totals. |
| `!day` | One calendar day (UTC): joins, unique players, first-time players, playtime, peak concurrency. |
| `!region` | One place: unique players, joins, playtime. |
| `!player` | One player: when first and last seen, joins, playtime, where, and the last name they played under. |

**An address is never written down.** The key is `SHA-256(salt ‖ address)`
truncated to 96 bits, where the salt is 16 random bytes generated when the file
is created and kept in it. Without the salt the hash would be no protection at
all — the whole IPv4 space is four billion hashes — and with a per-file salt the
table cannot be reversed or cross-referenced against another server's.

⚠️ **This is not a security control.** Somebody holding the file, the salt and a
suspected address can confirm that address is in it. That is inherent: it is the
same property that makes the same address count as the same player.

**The name IS written down**, because it is the only thing an operator can
recognise somebody by, and it is a name that player broadcast to everybody in
the server anyway. It is the last column, escaped through `LogText.Safe` —
a name is twenty arbitrary bytes including a tab and a newline, and this is a
tab-separated file with one record per line.

**Geography is a country, and inside the United States a state.** Never a city,
never a coordinate, and never the address that produced it. The map's markers
sit on a country's or a state's own centroid, out of `world.json`.

### What "unique today" costs to answer

Each player record carries the last calendar day it was seen on. A day's unique
count goes up when a player's last-seen day is not today. That is O(1), exact,
and survives a restart — a set of keys per day is the only other way to answer
it and costs days × players to store.

⚠️ **A session that crosses midnight is charged entirely to the day it started
on.** Splitting it would be more accurate and would mean a finished day's row
could still change, which is the property that makes the daily bars safe to read.

## The bounds

| | | |
|---|---|---|
| Player records | 20,000 | Least recently seen are evicted first; a player currently connected never is. |
| Days | 800 | A little over two years. |
| Places | 1,000 | Comfortably more than there are countries and states. |
| Written | at startup when the file is new, then every 5 minutes when something changed, and on a clean stop | What a hard kill costs. |

**A new file is written at startup, synchronously, before the listener binds.**
The salt only lives in this file and is what makes an address the same player
across restarts, so a process killed inside its first five minutes would
otherwise generate a different one next time and restart every player's identity
with nothing said. It also means the log line saying a file was started can be
checked against a file — with no file beside it, that line is indistinguishable
from a path the server cannot write to.

**Measured at a full table** — 20,000 records, Release, against a 33.3 ms tick
budget. A real server's table is far smaller and every figure scales with it.

| | |
|---|---|
| File | 2.26 MB |
| Render, on the tick thread | 11.0 ms, once every five minutes — one tick in nine thousand, and that tick is still inside its budget |
| Load, at startup | 32 ms, before the listener binds |
| A join and its later disconnect, with geo | 185 us — almost all of it the geo lookup; see below |
| The same, without geo | 1.7 us |
| Every other tick | **nothing at all.** The flush check is inside the server's existing 30-second housekeeping block, so no analytics code runs on an ordinary tick |

**What would show this has gone stale**: a cap raised, a column added to the
player record, or the render moved anywhere that runs more often than the flush
interval.

**The all-time counters are kept separately and trimming does not touch them**, so
the headline number never falls. What that costs is that a player evicted and
later returning is counted twice. A total that went *down* is the one number an
operator would report as a bug, so the cost is paid in the direction that reads
as an over-count rather than a loss.

**The render is on the tick thread; the disk is not.** Building the file's text
is bounded work over a bounded table, but a write is bounded by the disk, which
on a loaded VPS is not bounded by anything. `AnalyticsWriter` is one real thread
with a one-slot mailbox — newest wins, so a disk slower than the flush interval
costs a stale file rather than a late tick.

## The geo database

**Nothing ships one.** Nothing freely redistributable is small enough at
US-state resolution: the country-level files are several megabytes and the
city-level ones are hundreds, and each carries an attribution licence and a
monthly rebuild. So the operator downloads one:

| | | |
|---|---|---|
| [DB-IP IP-to-City Lite](https://db-ip.com/db/download/ip-to-city-lite) | CC BY 4.0 | No account. Monthly. Resolves US states. |
| [MaxMind GeoLite2 City](https://dev.maxmind.com/geoip/geolite2-free-geolocation-data) | CC BY-SA 4.0 | Free account required. Twice weekly. Resolves US states. |

A **country-level** database (either publisher's `-Country` file) works and is
much smaller; the world map is then complete and the United States view resolves
no states, which the map's caption says in those words.

**DB-IP's City Lite names a US state but gives it no code**; MaxMind gives both.
`GeoIpDatabase.UsStateCode` derives the code from the English name, for the 50
states and DC, which is every name DB-IP uses inside the United States (checked
against the 2026-09 build). `GeoIpStateTests` holds that table to `world.json`'s
US markers. A player recorded before a state could be resolved stays under
"United States, no state" until they next join, when they move to their state.
Their earlier joins and playtime stay where they were counted. The file keeps no
address, so there is nothing to re-resolve them from.

`GeoIpDatabase` reads the format directly — this project has no NuGet
dependencies and the panel already declined ASP.NET Core for eight endpoints.
The file is read with `RandomAccess` rather than being loaded or mapped, so a
600 MB city database costs the same resident memory as a 2 MB country one. The
price is one syscall per tree node.

**A lookup is 176 us**, measured against DB-IP's 8.3 MB country database,
Release, warm cache. It happens **once per join** — 0.5% of one 33.3 ms tick,
on the tick a player joins, beside a `FULL_UPDATE` of the whole world that the
same join already serialises. Joins are bounded above by
`ConnectionRateLimiter`, and the lookup is behind the completed handshake, so a
flood of half-open connections never reaches it.

⛔ **It must never move onto a path that runs per tick.** That is the whole
reason the figure is written down.

⚠️ **An IPv4 address in an IPv6 database lives under `::ffff:0:0/96`**, and
those 96 bits are the same 96 node reads for every address. Walking them once at
open and starting each lookup there is worth **291 us → 176 us** — three
quarters of the syscalls, for one cached field. Every free database is IPv6, so
this is the normal path rather than an edge case.

⛔ **Opening one never throws.** A missing or corrupt database costs the
geography and nothing else — a server that will not start because a monthly geo
database was not re-downloaded is a worse outcome than a map with no dots on it.

⚠️ **A private address resolves to nothing, and that is the common case.** A LAN
server's whole roster is on `192.168/16`. The panel counts those players under
"the database could not place" rather than dropping them.

## The map

`src/GG2.Server/Web/Assets/world.json`, 81 KB, embedded in the assembly and
served at `/world.json`. Generated by `tools/gen-geo-assets.js` from Natural
Earth 1:110m coastlines, 1:50m country polygons and 1:110m US states — all
public domain — and **checked in**, because the panel has to work on a machine
with no route to the internet.

**Every coordinate in it is already projected** into a 0..10000 display box, not
a longitude and a latitude, so the panel contains no projection code: it scales
a box to a canvas and draws. That is also what makes the Alaska and Hawaii
insets free — they are ordinary geometry that was moved, once, offline, instead
of two special cases in a drawing loop.

Two things the generator had to be told, both measured in a browser rather than
reasoned about:

- ⚠️ **The antimeridian.** Natural Earth cuts a landmass crossing 180° into
  points at both -180 and +180, which in this projection are opposite edges — so
  the segment between them draws as a line straight across the world. Antarctica,
  Chukotka, Wrangel Island and one Fijian island each put one on the map. Rings
  are cut where they jump more than half the world.
  `WebAssetTests.NoRingJumpsAcrossTheAntimeridian` is the gate.
- ⚠️ **Coastlines from 110m, country markers from 50m.** 110m drops every
  country too small to draw — Singapore, Malta, Luxembourg, Hong Kong, Puerto
  Rico — and those are places players connect from. The outline is what a reader
  sees; the marker table is what a country code is looked up in.

**Proportional symbols, not a choropleth**, and the circle's AREA is
proportional to the player count. A radius scale exaggerates a large value by
the square, which is the classic way a map like this lies.

**Every player is accounted for in the caption.** Placed, outside the United
States (in the US view), in a place the map has no marker for, and unresolvable —
four buckets that sum to the total. A caption reading "296 placed" out of 300
with no explanation is the panel quietly losing four people.

## Reading it from a script

```powershell
gg2-server --control-port 8192 --analytics data/analytics.tsv
# then, on the same machine:
echo '{"cmd":"analytics"}' | nc 127.0.0.1 8192
```

The reply's `analytics` object is exactly what the panel draws. `days` and
`regions` are arrays of bare values against a `dayFields` / `regionFields` list —
eight hundred days of six named fields is mostly repeated key names, the same
trade `PerfHistory` makes. `PlayerAnalyticsTests.EveryRowMatchesItsDeclaredFields`
is what stops a list and its rows drifting apart.

## The whole fleet

Two ways to see every server at once. Both add the servers up with the same
function, `sumAnalytics` in `analytics.js`, so the rules in the second table
below hold for both.

**On a fleet hub's panel** (`docs/WEBADMIN.md`, "The fleet hub"), the Analytics
sheet has a server switch: *All servers*, the hub's own server, and each
member. It opens on the server the page is showing. A single server is fetched
through the hub's pass-through (`/h/<member>/api/analytics`). *All servers*
asks the hub and every member at once and adds up whatever answers. A server
that does not answer, or has analytics off, is named above the figures and left
out of them. These are live figures over HTTPS, with no ssh.

**Offline, from ssh:** `scripts/fleet-analytics.sh` collects every host in `deploy/hosts` and builds one
page, `deploy/analytics/fleet-analytics.html`: each server on its own, or all of
them added together. It is read-only on the hosts.

```sh
scripts/fleet-analytics.sh                 # collect from every host, build the page
scripts/fleet-analytics.sh --open          # and open it
scripts/fleet-analytics.sh -H gg2-us-east  # collect one host; the page still shows every saved host
scripts/fleet-analytics.sh --no-collect    # rebuild the page from what is saved
```

| | |
|---|---|
| **How it reads a host** | Over ssh, it logs into that host's panel on loopback with the port and password from the host's own `shared/gg2.json` — `fleet-state.sh`'s route — and sends `state` (for the server's name) and `analytics`. A TLS panel's self-signed certificate is pinned by SHA-256 in `deploy/panel-pins`, one line per host, recorded on first contact; a different certificate is refused before the password is sent (`scripts/fleet-panel.py`). That is the running server, so sessions still open are counted; the file on disk is up to five minutes behind and has none. |
| **What it keeps** | `deploy/analytics/<host>.json`, the last successful reply per host. A host that does not answer keeps its previous reply, and the page names it and the time its figures are from. The directory is gitignored: it holds player names. |
| **The page** | Self-contained, opened from disk: `app.css`, `world.json`, the panel's `analytics.js` and `common.js`, and the analytics sheet's markup cut out of `index.html`, with `scripts/fleet-analytics/fleet.js` adding the server switch and the sum. There is no second copy of the view to go stale. A reply's `<` is escaped before it goes into the page, because a player name can spell `</script>`. |

**What "All servers" adds, and what it cannot**, on either page.

| Figure | In the sum |
|---|---|
| Joins, time played, per-day joins and playtime, per-place joins and playtime, connected now | Added. Exact. |
| Players (all time, per day, first-time, per place) | Added, and an **over-count**: each host salts its hashes with its own random salt, so the same address on two hosts is two keys and nothing can join them. |
| Peak at once, and each day's peak | The busiest **single** server's. Per-server peaks happened at different moments, so their sum is a crowd that never existed. |
| Most time played | Each host's top 25, merged and cut to 25, labelled with the host. The same person on two hosts is two rows. |

The page states the over-count and the peak rule under the figures. **What would
make the player sum exact** is a salt shared across the fleet, which this
deliberately does not do: a per-file salt is what stops one server's table being
cross-referenced against another's.

**The offline page was verified by opening it**, against the three live hosts: the sum's joins,
players, peak and per-day rows matched each host's `!meta` and `!day` records by
hand, and a host that did not answer was named above the figures.
`WebAssetTests.TheFleetPageHasEveryIdItsScriptsAskFor` and
`NoTopLevelNameIsDeclaredTwice` are the mechanical half; nothing runs the sum
itself. The panel's switch was checked in a browser against a local hub with one
paired member: each server alone, the sum of both (with injected replies whose
column order differed), and a member that had been stopped, which was named
above the figures and left out of the sum.

## Gates

| | |
|---|---|
| `PlayerAnalyticsTests` | counting, per-day uniqueness across a day boundary, playtime including sessions still open, the salt and every total surviving a reload, the cap and its effect on the all-time count, a live player never being trimmed, an unresolvable address still counted under `??`, and the three claims a reader would act on: no address is in the file, a name cannot forge the format, and a store that never read its file cannot write one |
| `AnalyticsServerTests` | the same store as a running `GameServer` drives it, over a real socket: a join and a disconnect are counted, bots never are, the history survives a whole server restart, no file appears without the flag, and the `analytics` verb reports what happened |
| `GeoIpDatabaseTests` | only that the reader refuses a missing file and a file that is not a MaxMind DB, without throwing |
| `WebAssetTests` | the panel's files, checked the way a build step would: no top-level name declared twice across the scripts a page loads, every literal `$('id')` present in the HTML (for the fleet page, inside the analytics sheet it cuts out), and the map geometry's shape and antimeridian |
| `CliCoverageTests`, `ConfigFileCoverageTests` | both settings are reachable from a flag and from a config file, and each moves the option it names |

**Geography is not gated.** The repo carries no `.mmdb`, so nothing tests a
lookup, the country-versus-US-state keying, geography surviving a reload, or the
payload naming a loaded database. A reader tested against an encoder written
beside it would pass whenever both misread the specification the same way, so
that is not a substitute. A change to `GeoIpDatabase` or to region keying is
verified by running with `--geoip` against a downloaded database and opening the
panel.

**What no test here catches**, and cannot: anything that needs the panel's
JavaScript to run. The view is verified by opening it. Two bugs found that way
and not by any gate above — a local variable shadowing a shared helper, and a
map caption that dropped four players — are the reason this paragraph is here
rather than a claim that the feature is covered.

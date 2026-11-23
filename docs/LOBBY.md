# gg2-lobby

An HTTP/JSON server list, and the gg2-server client that announces to it. The
service is `src/GG2.Lobby` (binary `gg2-lobby`); the client is
`HttpLobbyAnnouncer`, turned on by `--lobby-url <url>` or `LobbyUrl=` in the
config file.

It is **separate from the ganggarrison.com lobby**, not a replacement for it.
`--lobby` still announces there over UDP (`LobbyRegistration`). Pass both and the
server is listed in both, under a different id in each.

## Why it looks the way it does

The design goal was a list that costs almost nothing to serve.

- **A GET does no work.** The whole response for `GET /servers` (status line,
  headers and body) is built once when the list changes, together with a gzipped
  copy and a 304. A request is answered with one send of that byte array, with no
  lock, no JSON and no allocation.
- **A re-announce that changes nothing rebuilds nothing.** Each entry is stored
  as its already-serialised JSON fragment. A re-announce whose fragment is
  byte-identical only pushes out the expiry, so in steady state the cached list
  is rebuilt only when a player joins or leaves or a map changes.
- **Nothing runs on a timer.** Expiry is lazy, as in Faucet-Lobby: entries are
  swept when a request arrives after the earliest deadline.
- **No web framework.** It uses a raw socket accept loop and an HTTP/1.1 parser
  that only reads what the four routes need (`LobbyRequest`). Keep-alive and
  pipelining work; chunked request bodies are refused with 411.
- **Native AOT.** `dotnet publish` produces a self-contained binary with no
  runtime to install.

Measured on an i7-13700K, with the load generator on the same machine, 64
keep-alive connections and 200 listed servers: about 194k requests/s serving
the 66 KB plain list, and 234k requests/s gzipped. The AOT binary is 1.9 MB, and
the process used 10 MB of memory idle and 14 MB after the run.

On Linux, leave .NET's socket settings at their defaults. With pinned CPUs
under WSL2, the default build served 285k–490k keep-alive requests/s.
`DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1` cut new-connection throughput
from about 70k to 25k/s with p99 at 15 ms, and adding
`DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT` made it worse. A Rust port of this
design with byte-identical output was benchmarked alongside it and kept out of
the repo: it was 1.4–2× faster on Linux, about 25% slower on Windows, and used
6 MB at peak against 25–32 MB. At server-list load neither difference matters.

## Routes

### `PUT /servers/<id>`: announce

`<id>` is 32 hex digits the server picks at random when it starts and keeps for
its lifetime. It is the server's only credential: anyone who knows it can update
or withdraw the listing. It is never shown in the list.

```json
{
  "port": 8190,
  "name": "Bacon Town 24/7",
  "map": "ctf_truefort",
  "slots": 16,
  "players": 3,
  "bots": 7,
  "passworded": false,
  "game": "gg2-server",
  "version": "v1.2.0",
  "url": "https://github.com/kylelmoy/gg2-server",
  "protocols": ["b31c2209-4256-9a19-d0ef-c71c5373bd75", "31ad383f-df45-449b-9bb7-63869ac81345"],
  "info": { "x-respawn": "5" }
}
```

`port` and `name` are required. `protocols` lists the HELLO UUIDs the server
accepts: legacy, then native (`docs/PROTOCOL.md`). This is what lets a browser
tell a native-capable server from a stock one, which the UDP lobby cannot say.
`info` holds free-form string pairs, at most 16. Unknown top-level properties
are ignored, so a newer server can send fields an older lobby does not know.

**The listed address is the one the request came from.** An announce cannot
name another host.

Responses:

| Status | Body | Meaning |
|---|---|---|
| 200 | `{"ttl":70}` | Listed, for this many seconds unless re-announced. |
| 400 | `{"error":"…"}` | A field is missing, the wrong type or too long. |
| 422 | `{"error":"unreachable"}` | The lobby could not open a TCP connection to `port` within 3s. |
| 503 | `{"error":"lobby full"}` / `"busy, retry"` | Too many servers in total or from this address, or too many probes in flight. |

**Reachability probe.** Before it first lists a server, the lobby connects back
to the announced port, as Faucet-Lobby does, so a server whose port is not
forwarded never shows up. The probe goes only to the address the announce came
from. Re-announces from an id already listed at the same endpoint skip it, so
the steady 30-second cadence costs no outbound connection. `--no-probe` turns
it off.

**A new id on an endpoint already listed replaces the old entry.** Faucet-Lobby
refuses it, which keeps a restarted server (new id, same port) out of the list
for a whole TTL whenever its unregister was lost. Here the newcomer has just
passed the probe from the same address, and only one process can listen on a
port.

### `DELETE /servers/<id>`: withdraw

Returns 204 whether or not the id was listed.

### `GET /servers`: the list

```json
{"servers":[{"ip":"203.0.113.7","port":8190,"name":"Bacon Town 24/7","map":"ctf_truefort",
  "slots":16,"players":3,"bots":7,"passworded":false,"game":"gg2-server","version":"v1.2.0",
  "url":"https://github.com/kylelmoy/gg2-server","protocols":["…","…"]}]}
```

It sends `ETag` and honours `If-None-Match` with a 304, and `Accept-Encoding:
gzip` gets the precompressed copy. `Access-Control-Allow-Origin: *` is set, so
a web page on any origin can read the list.

### `GET /`

A plain-text list of the routes.

## The client: `HttpLobbyAnnouncer`

It announces on the same triggers as the UDP lobby: every 30 seconds, on a map
change, on a join, and on a leave that un-fills a full server.
`GameServer.AnnounceToLobby` sends to both lobbies.

- **The tick thread never waits.** `Register` stores the latest state and wakes
  a worker. The worker sends the newest state, at most one request at a time
  and at most one every 2 seconds, so a burst of joins becomes one PUT.
- **Shutdown withdraws.** `Dispose` sends the DELETE and waits up to 2 seconds
  for it. Taking the server offline from the control port withdraws too, and
  bringing it back online re-announces.
- **The log records changes, not attempts.** "listed" appears once. A failing
  lobby is warned about when the failure starts or changes, not every 30 seconds.
  A 422 is spelled out as "the lobby cannot connect to this server's port - is
  it forwarded?".

## Running it

```
dotnet publish src/GG2.Lobby/GG2.Lobby.csproj -c Release -r linux-x64 -o out/lobby
out/lobby/gg2-lobby --port 29945
gg2-server --lobby-url http://lobby.example:29945
```

| Flag | Default | |
|---|---|---|
| `--port <n>` | `29945` | Listen port. |
| `--bind <address>` | `::` | Listen address. `::` is dual-stack. |
| `--ttl <seconds>` | `70` | How long one announce keeps a server listed. |
| `--no-probe` | | List without connecting back to the game port. |
| `--max-servers <n>` | `4096` | Entries in total. |
| `--max-per-ip <n>` | `64` | Entries from one address. |
| `--quiet` | | Do not log additions and removals. |

⚠️ **The SDK this was built with flags its AOT compiler package.**
`Microsoft.DotNet.ILCompiler` 10.0.11 has a high-severity NuGet advisory
(GHSA-p3jp-9v83-76w9), and this repo treats warnings as errors, so the publish
above fails until the SDK is updated; SDK 10.0.115 publishes cleanly. That is
why `PublishAot` is set only while publishing: set unconditionally, the same
audit would fail every ordinary build of the solution. On Windows the native link step also needs `vswhere.exe` on
`PATH` (it lives in `Visual Studio\Installer`).

## What it deliberately does not do

- **No TLS.** Put it behind a reverse proxy if you want https. It does not read
  `X-Forwarded-For`, so behind a proxy every server would be listed at the
  proxy's address. Trusting that header is a flag's worth of work, left out until
  something needs it.
- **No persistence.** A restart empties the list, and the next 30-second
  re-announce from each server fills it again.
- **No stock-client compatibility.** A stock GG2 client reads the binary list
  protocol from ganggarrison.com:29944 and cannot read this list. That is what
  `--lobby` is for.

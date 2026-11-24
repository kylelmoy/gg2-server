# gg2-auth

The Haxxy reward auth server: the service a game server asks whether a player
really holds the reward id they claimed, and which rewards to show for them.
The service is `src/GG2.Auth` (binary `gg2-auth`). The game server's half is
`RewardAuth` and `RewardAuthClient` in `GG2.Server`, turned on by `--rewards`
and pointed at a list of auth servers by `AuthServerUrls` (`--auth-server`).

It **runs beside ganggarrison.com:45022, not instead of it.** Upstream
publishes only the web page in front of its service (`GG2RewardsArea`, PHP),
not the service itself, and its user table is not public, so a key issued by
ganggarrison.com does not verify here and players get their credentials from
whoever runs this. The game server's default list asks both,
`tcp://ganggarrison.com:45022, tcp://gg2server.net:45022`, at once, and uses
the first listed one that answers with rewards: a stock player's key still
works, and a key issued here works too. Every listed server sees every
player's reward id and HMAC; the HMAC is bound to the game server's address,
so no listed server can replay it through another game server.

It is **a separate process from gg2-lobby** because it holds every player's
secret key and the lobby holds nothing private: a fault in the lobby's public
HTTP surface cannot reach the key table, and either can be down without the
other.

## Running it

```
gg2-auth add --note "who this is" Badge_Dev GoldSc   # prints the player's [Haxxy] section
gg2-auth serve                                       # port 45022, dual-stack
gg2-server --rewards                     # asks ganggarrison.com and gg2server.net
gg2-server --rewards --auth-server auth.example.net   # replaces the list; repeatable
```

| Command | What |
|---|---|
| `serve` | answer game servers; the default when no command is given |
| `add [--note <text>] [reward ...]` | issue the next integer id and a fresh key |
| `list` | every user without keys |
| `show <id>` | one user, with the key |
| `grant <id> <reward> ...` / `revoke <id> <reward> ...` | change rewards |
| `rekey <id>` | new key; the old one stops working |
| `remove <id>` | delete the user |

`serve` takes `--port`, `--bind`, `--public-address <ip>` (repeatable),
`--require-ip` and `--quiet`. Every command takes `--db <path>`; without it the
table is `$GG2_AUTH_DB`, else `gg2-auth.tsv` in the current directory, so the
CLI and the service must be run against the same path.

The player pastes what `add` prints into the `gg2.ini` beside their game:

```
[Haxxy]
RewardId=1
RewardKey=899321a36267dbd237d87b8b71d85ab2
```

A stock client reads both at startup (`game_init.gml:92-93`) and sends a
`REWARD_REQUEST` with every join when both are set. Nothing else about the
client changes, and it never contacts the auth server itself.

**A running server picks up every CLI change on its next request.** It stats
the file per request and reloads when the time or length changed; a file that
fails to parse keeps the last good table and logs why.

## The user table

One tab-separated line per user: `id`, key as hex, colon-separated rewards,
note. Writes go to a temporary file that is moved over the original, so a
reload never sees half a file.

**The keys are stored in the clear, and must be.** HMAC needs the key itself,
so there is nothing a hash could be checked against. Treat the file as a
private key: whoever reads it can authenticate as anyone in it.

Issued keys are 16 random bytes with no zero byte, since the client holds the
key in a GM8 string. Keys of 1 to 64 bytes load.

Reward names are not checked against a catalog, only for characters
(`[A-Za-z0-9_]`, because `:` separates them). The names the client knows are
`Scripts/Rewards/initRewards.gml` (the badges, `global.HaxxyBadges`) and the
`hasReward`/`hasClassReward` call sites; a class reward is a name plus the class
abbreviation, such as `GoldSc`. A name the client does not know is sent and does
nothing.

## The exchange

Specified by `Documentation/Authentication.txt` in the reference, which the
tests' cached clone does not check out (its sparse checkout is `Source/gg2`
only); read it with `git -C <clone> show HEAD:Documentation/Authentication.txt`.
The code is `Scripts/Rewards/rewardAuthStart.gml`, the `RewardAuthChecker`
object, and `ClientBeginStep.gml:553-561`.

1. Client to game server: `REWARD_REQUEST <id>`.
2. Game server to client: `REWARD_CHALLENGE_CODE <16 random bytes>`.
3. Client to game server: `REWARD_CHALLENGE_RESPONSE hmac_md5(key, challenge + ip)`,
   where `ip` is the dotted address the client connected to, as text.
4. Game server to here, over TCP: the protocol UUID
   `205e2d84-4833-89d4-15d9-c0249667df1c` in reading order, a little-endian
   `ushort` counting the rest, the response, the challenge, an ipCheck byte, and
   the id unterminated.
5. Here: recompute the HMAC with the stored key and the address the request
   came from, compare in constant time, write the user's reward string, close.

| Outcome | What is sent |
|---|---|
| right key | the reward string, then close |
| wrong key, unknown id, malformed request, or timeout | nothing, then close |
| right key, no rewards enabled | nothing, then close |

**An empty body is the only failure signal there is.** The reference reads to
end of stream and treats any non-empty body as success, so the spec's `FAIL`
reply would hand the player a reward named `FAIL`. A user with nothing enabled
looks like a failure for the same reason, and the client could not tell them
apart anyway.

**ipCheck 0** is the reference's host self-auth (`GameServerCreate.gml:79-83`):
`hmac_md5(key, challenge)` with no address. It is accepted unless
`--require-ip`. A client never produces one, so a game server cannot turn a
player's answer into one; what it allows is replaying a sniffed host self-auth.
gg2-server never sends it.

## Addresses: the one thing to configure

The IP check binds a player's answer to one game server: the auth server uses
the address the request **arrives from**, which the game server cannot choose,
so an answer captured by one server cannot be replayed through another.

That breaks when the game server reaches this service from a different address
than its players reach it at. The common case is the two on the same machine or
LAN: the request arrives from `127.0.0.1` or `192.168.x.y` while players signed
for the public address, and every check fails with
`FAIL: wrong key, or the client saw the game server at another address`.

`--public-address <ip>` fixes that. A request from a loopback or private peer
(RFC 1918, link-local, 100.64/10, IPv6 ULA) is also checked against each
configured address; a request from a public peer is held to its own address
alone. The cost is that any host on that private network can relay for the
public address, and that network is the operator's.

A player connecting by LAN address to a game server on the same LAN needs
nothing: both sides see the same address.

## Surface

The listener is reachable by anyone, like ganggarrison.com:45022.

| Bound | Value |
|---|---|
| connections at once | 256, and 16 from one address |
| one exchange, start to finish | 10 s, not reset by trickled bytes |
| request size | 306 bytes (a 255-byte id), else closed at the first bad byte |
| log lines | 30 per 10 s, then a count of what was dropped |

Brute force is not bounded and does not need to be: a guess is a 128-bit HMAC.
Connections are served on dedicated threads rather than the thread pool, for the
reason `RewardAuthClient` gives: a pool stall past the game server's 20-second
deadline silently costs the player their rewards.

## What proves it

`AuthServiceTests`: the parser against `RewardAuth.BuildAuthRequest`, a
transcription of `hmac_md5_bin.gml`'s arithmetic against the framework's
HMACMD5, every failure outcome, the public-address rule, ipCheck 0, the reload,
and gg2-server relaying to it end to end. Both ends of those are this repo's, so
none of them shows a GM8 client agrees.

That was checked live against a stock GM8 client driven through the gg2-agent
bridge (2026-11-23): its own `hmac_md5_bin` gave the same digest as Python's `hmac`
for a challenge containing zero bytes, and its own `REWARD_CHALLENGE_CODE`
handler, relayed by gg2-server to gg2-auth, ended with three rewards in
`global.myself.rewards` and two badges parsed. What would show this has gone
stale: a client build whose `hmac_md5_bin` or `socket_remote_ip` changes, or the
`--public-address` rule meeting a network shape the table above does not list.

# Status page

The public page at `https://gg2server.net/`: one look at whether the lobby,
the reward auth server and the private-room hosts are working, with the lobby
listing, the award holders, private room capacity and the lobby host's own load.

| File | What |
|---|---|
| `gg2-status.py` | Runs on the lobby host from cron every 30 s and writes `status.json`. Python 3.9+, standard library only |
| `config.json` | gg2server.net's settings: where each service is, the game servers expected in the lobby, the output paths |
| `site/` | The static page. Reads `/status.json` every 15 s; renders nothing it did not read |
| `publish.sh` | Uploads all of it, copies the page into `/srv/gg2-status`, installs the crontab lines. No sudo |
| `install.sh` | One-time root step: creates `/srv/gg2-status` and installs a Caddyfile |

The look is the admin panel's. `site/status.css` copies its tokens from
`src/GG2.Server/Web/Assets/app.css`, and `publish.sh` takes the favicon and
fonts from the same directory, so the page has no copy of either.

## What each check asks

| Service | Check | Up when |
|---|---|---|
| gg2-lobby | `GET /servers` on loopback | 200 with a list |
| gg2server.net | `GET https://gg2server.net/servers` | the same through Caddy and TLS |
| gg2-auth | TCP connect, then close | the connect succeeds. A zero-byte connection is dropped without a log line (`AuthServer.cs`) |
| private room directory | reads `/srv/gg2-directory/rooms.json` | its payload decodes. The signature is not checked here |
| private room host API | `GET <url>/status` for each host in the directory | 200; `draining` reports partial |
| game servers | none sent to them | the lobby lists `ip:port` from `config.json` |
| certificates | TLS handshake to each https name | more than 14 days left is up, under is partial |

Award holders come from `gg2-auth list`, which never prints a key, so the
generator never handles one. **Notes are not published** unless
`auth.publishNotes` is true, because a note is the operator's description of a
person. The time beside a check is one request round trip from the lobby host on an
already-open connection (a TCP connect, for gg2-auth): network latency plus the
service's answer. DNS and the TLS handshake are left out, because with every
check running at once on a small VPS they measured the checker's CPU. History is the last 24 h of samples in `historyFile`, shown as 48
half-hour segments.

The page says so when `status.json` is more than four intervals old, so a
generator that stopped cannot leave a green banner up.

## Installing on a new host

1. Put the `gg2server.net` block below into the host's Caddyfile, saved as
   `~/gg2-status/Caddyfile` with the rest of that host's file.
2. `tools/status-page/publish.sh <ssh-host>` uploads everything and stops,
   asking for step 3.
3. `sudo sh ~/gg2-status/install.sh` on the host.
4. `publish.sh` again. Later publishes need only this.

```
gg2server.net {
	handle /rooms.json {
		root * /srv/gg2-directory
		header Cache-Control "public, max-age=300"
		file_server
	}
	handle /servers* {
		@write not method GET HEAD OPTIONS
		respond @write "announce to http://gg2server.net:29945" 405
		reverse_proxy 127.0.0.1:29945
	}
	handle /status.json {
		root * /srv/gg2-status
		header Cache-Control "no-cache"
		header Access-Control-Allow-Origin *
		encode zstd gzip
		file_server
	}
	handle {
		root * /srv/gg2-status
		header Cache-Control "public, max-age=300"
		encode zstd gzip
		file_server
	}
}
```

The cron log is `~/gg2-status/cron.log`; it is empty while every run succeeds.

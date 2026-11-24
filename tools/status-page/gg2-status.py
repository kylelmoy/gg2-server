#!/usr/bin/env python3
"""gg2-status: writes status.json for the public status page at gg2server.net.

Runs on the box that hosts gg2-lobby and gg2-auth (cron, every 30 s), asks each
service the cheapest question that shows it is working, and writes one JSON file
the static page reads. tools/status-page/README.md has the deployment.

Everything here is read-only against the services:

- the lobby is asked GET /servers twice, on loopback and through the public
  https route, so a dead Caddy shows up separately from a dead lobby;
- gg2-auth gets a TCP connect and an immediate close, which it drops silently
  (AuthServer.cs: a zero-byte read returns without a log line);
- the award holders come from `gg2-auth list`, which prints ids, rewards and
  notes and never a key, so this script never handles a secret;
- each private-room host from the signed directory is asked GET /status;
- game servers are not connected to at all: a server is healthy when it is
  listed in the lobby, which it re-announces to every 30 s.

Python 3.9+, standard library only.
"""

import base64
import concurrent.futures
import datetime
import http.client
import json
import os
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
HISTORY_SECONDS = 24 * 3600
BUCKET_SECONDS = 1800  # 48 buckets on the page
USER_AGENT = "gg2-status/1"


def now_utc():
    return datetime.datetime.now(datetime.timezone.utc)


def iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")


# One context for every https check. Building one loads the CA bundle, which
# costs ~70 ms of CPU on the lobby host - per request, if urlopen is left to it.
TLS = ssl.create_default_context()


def http_get_json(url, timeout):
    """GET url and parse JSON. The time returned is one request round trip on an
    already-open connection: network latency plus the service's own answer.
    DNS, TCP and the TLS handshake happen before the clock starts, because they
    measure the checker's CPU and the handshake's extra round trips rather than
    the service."""
    parts = urllib.parse.urlsplit(url)
    if parts.scheme == "https":
        conn = http.client.HTTPSConnection(parts.hostname, parts.port or 443, timeout=timeout, context=TLS)
    else:
        conn = http.client.HTTPConnection(parts.hostname, parts.port or 80, timeout=timeout)
    try:
        conn.connect()
        start = time.monotonic()
        conn.request("GET", parts.path or "/", headers={"User-Agent": USER_AGENT})
        resp = conn.getresponse()
        body = resp.read(1 << 20)
        ms = (time.monotonic() - start) * 1000
    finally:
        conn.close()
    if resp.status != 200:
        raise RuntimeError(f"HTTP {resp.status}")
    return resp.status, json.loads(body), ms


def tcp_connect(host, port, timeout):
    start = time.monotonic()
    with socket.create_connection((host, port), timeout=timeout):
        pass
    return (time.monotonic() - start) * 1000


def cert_days_left(hostname, timeout):
    with socket.create_connection((hostname, 443), timeout=timeout) as raw:
        with TLS.wrap_socket(raw, server_hostname=hostname) as tls:
            cert = tls.getpeercert()
    expires = datetime.datetime.fromtimestamp(
        ssl.cert_time_to_seconds(cert["notAfter"]), datetime.timezone.utc)
    issuer = dict(x[0] for x in cert.get("issuer", ()))
    return expires, issuer.get("organizationName", "")


def check(fn):
    """Runs fn, turning an exception into a down result with the reason."""
    try:
        return fn()
    except Exception as e:  # noqa: BLE001 - every failure is a status here
        reason = getattr(e, "reason", None) or e
        return {"state": "down", "detail": f"{type(e).__name__}: {reason}"[:200]}


def lobby_check(url, timeout):
    def run():
        status, body, ms = http_get_json(url, timeout)
        servers = body.get("servers", [])
        return {"state": "up", "ms": round(ms, 1), "detail": f"{len(servers)} listed",
                "servers": servers}
    return check(run)


def tcp_check(host, port, timeout, detail):
    def run():
        ms = tcp_connect(host, port, timeout)
        return {"state": "up", "ms": round(ms, 1), "detail": detail}
    return check(run)


def room_host_check(entry, timeout):
    base = entry["url"].rstrip("/")
    hostname = urllib.parse.urlparse(base).hostname

    def run():
        status, body, ms = http_get_json(base + "/status", timeout)
        result = {
            "state": "up", "ms": round(ms, 1),
            "prefix": body.get("prefix", entry.get("prefix")),
            "region": entry.get("region", ""),
            "url": base,
            "hostname": hostname,
            "rooms": body.get("rooms"), "maxRooms": body.get("maxRooms"),
            "free": body.get("free"), "draining": bool(body.get("draining")),
        }
        if result["draining"]:
            result["state"] = "degraded"
            result["detail"] = "draining for a deploy"
        elif result["free"] == 0:
            result["detail"] = "full"
        else:
            result["detail"] = f"{result['free']} of {result['maxRooms']} free"
        return result

    result = check(run)
    result.setdefault("prefix", entry.get("prefix"))
    result.setdefault("region", entry.get("region", ""))
    result.setdefault("url", base)
    result.setdefault("hostname", hostname)
    return result


def read_directory(path):
    def run():
        with open(path, "rb") as f:
            envelope = json.load(f)
        payload = json.loads(base64.b64decode(envelope["payload"]))
        mtime = datetime.datetime.fromtimestamp(os.path.getmtime(path), datetime.timezone.utc)
        return {"state": "up", "issued": payload.get("issued"),
                "hosts": payload.get("hosts", []), "published": iso(mtime),
                "detail": f"{len(payload.get('hosts', []))} hosts, issued {payload.get('issued', '?')}"}
    return check(run)


def award_holders(command, show_notes):
    def run():
        out = subprocess.run(command, capture_output=True, text=True, timeout=10, check=True)
        holders = []
        for line in out.stdout.splitlines():
            fields = line.split("\t")
            if len(fields) < 2 or not fields[0]:
                continue
            rewards = [] if fields[1] == "-" else fields[1].split(":")
            holder = {"id": fields[0], "rewards": rewards}
            if show_notes and len(fields) > 2 and fields[2]:
                holder["note"] = fields[2]
            holders.append(holder)
        return {"state": "up", "holders": holders}
    return check(run)


def host_info(units):
    info = {"hostname": socket.gethostname()}
    try:
        info["load"] = [round(x, 2) for x in os.getloadavg()]
        info["cpus"] = os.cpu_count()
        with open("/proc/uptime") as f:
            info["uptimeSeconds"] = int(float(f.read().split()[0]))
        mem = {}
        with open("/proc/meminfo") as f:
            for line in f:
                key, value = line.split(":", 1)
                mem[key] = int(value.split()[0])
        info["memTotalMb"] = mem["MemTotal"] // 1024
        info["memAvailableMb"] = mem["MemAvailable"] // 1024
        st = os.statvfs("/")
        info["diskTotalGb"] = round(st.f_blocks * st.f_frsize / 1e9, 1)
        info["diskFreeGb"] = round(st.f_bavail * st.f_frsize / 1e9, 1)
    except (OSError, KeyError, ValueError):
        pass
    states = {}
    for unit in units:
        try:
            out = subprocess.run(["systemctl", "is-active", unit], capture_output=True, text=True, timeout=5)
            states[unit] = out.stdout.strip() or "unknown"
        except (OSError, subprocess.SubprocessError):
            states[unit] = "unknown"
    info["units"] = states
    return info


def load_history(path):
    try:
        with open(path) as f:
            return json.load(f)
    except (OSError, ValueError):
        return {"checks": {}, "humans": []}


def write_atomic(path, data):
    directory = os.path.dirname(os.path.abspath(path))
    fd, tmp = tempfile.mkstemp(dir=directory, prefix=".tmp-", suffix=".json")
    try:
        with os.fdopen(fd, "w") as f:
            json.dump(data, f, separators=(",", ":"))
        os.chmod(tmp, 0o644)
        os.replace(tmp, path)
    except BaseException:
        os.unlink(tmp)
        raise


def buckets(samples, now_ts, value_of):
    """48 half-hour buckets, oldest first; each is None (no samples) or value_of(list)."""
    count = HISTORY_SECONDS // BUCKET_SECONDS
    grouped = [[] for _ in range(count)]
    for ts, v in samples:
        i = count - 1 - int((now_ts - ts) // BUCKET_SECONDS)
        if 0 <= i < count:
            grouped[i].append(v)
    return [value_of(g) if g else None for g in grouped]


def main():
    config_path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "config.json")
    with open(config_path) as f:
        cfg = json.load(f)
    timeout = cfg.get("timeoutSeconds", 4)
    started = now_utc()
    now_ts = int(started.timestamp())

    directory = read_directory(cfg["directoryFile"])
    room_entries = directory.get("hosts", []) if directory["state"] == "up" else cfg.get("fallbackRoomHosts", [])

    with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
        f_lobby = pool.submit(lobby_check, cfg["lobby"]["local"], timeout)
        f_lobby_public = pool.submit(lobby_check, cfg["lobby"]["public"], timeout)
        f_auth = pool.submit(tcp_check, cfg["auth"]["host"], cfg["auth"]["port"], timeout, "accepting connections")
        f_holders = pool.submit(award_holders, cfg["auth"]["listCommand"], cfg["auth"].get("publishNotes", False))
        f_rooms = [pool.submit(room_host_check, e, timeout) for e in room_entries]
        cert_hosts = list(dict.fromkeys(cfg.get("certificates", []) +
                                        [urllib.parse.urlparse(e["url"]).hostname for e in room_entries]))
        f_certs = {h: pool.submit(check, lambda h=h: dict(zip(("expires", "issuer"), cert_days_left(h, timeout))))
                   for h in cert_hosts}
        rooms = [f.result() for f in f_rooms]
        lobby, lobby_public, auth, holders = f_lobby.result(), f_lobby_public.result(), f_auth.result(), f_holders.result()
        certs = []
        for h, f in f_certs.items():
            r = f.result()
            if "expires" in r:
                days = (r["expires"] - started).total_seconds() / 86400
                certs.append({"host": h, "state": "up" if days > 14 else ("degraded" if days > 0 else "down"),
                              "expires": iso(r["expires"]), "daysLeft": round(days, 1), "issuer": r["issuer"]})
            else:
                certs.append({"host": h, "state": "down", "detail": r.get("detail", "")})

    listed = lobby.get("servers", lobby_public.get("servers", []))
    for s in listed:
        s.pop("protocols", None)

    # The game servers this operator runs: healthy when the lobby lists them.
    game_servers = []
    for g in cfg.get("gameServers", []):
        match = next((s for s in listed if s.get("ip") == g["ip"] and s.get("port") == g["port"]), None)
        entry = {"name": g["name"], "ip": g["ip"], "port": g["port"]}
        if lobby["state"] != "up":
            entry.update(state="unknown", detail="lobby unreachable")
        elif match:
            entry.update(state="up", detail=f"{match.get('map', '?')}, {match.get('players', 0)}/{match.get('slots', 0)}")
        else:
            entry.update(state="down", detail="not listed in the lobby")
        game_servers.append(entry)

    checks = [
        {"id": "lobby", "group": "Lobby", "name": "gg2-lobby", "sub": "server list, :29945", **strip(lobby)},
        {"id": "lobby-https", "group": "Lobby", "name": "gg2server.net", "sub": "https route to the list", **strip(lobby_public)},
        {"id": "auth", "group": "Awards", "name": "gg2-auth", "sub": f"reward auth, :{cfg['auth']['port']}", **auth},
        {"id": "directory", "group": "Private rooms", "name": "Private room directory", "sub": "signed rooms.json", **strip(directory)},
    ]
    for r in rooms:
        checks.append({"id": f"rooms-{r['prefix']}", "group": "Private rooms", "name": f"Private room host {r['prefix']}",
                       "sub": f"{r['region']} API", **strip(r)})
    for g in game_servers:
        checks.append({"id": f"game-{g['ip']}-{g['port']}", "group": "Game servers", "name": g["name"],
                       "sub": f"{g['ip']}:{g['port']}", "state": g["state"], "detail": g["detail"]})

    # History: one sample per check per run, last 24 h.
    history_path = cfg.get("historyFile", os.path.join(HERE, "history.json"))
    history = load_history(history_path)
    cutoff = now_ts - HISTORY_SECONDS
    for c in checks:
        if c["state"] == "unknown":
            continue
        samples = [s for s in history["checks"].get(c["id"], []) if s[0] >= cutoff]
        samples.append([now_ts, 1 if c["state"] == "up" else (0.5 if c["state"] == "degraded" else 0)])
        history["checks"][c["id"]] = samples
    for key in list(history["checks"]):
        if not any(c["id"] == key for c in checks):
            history["checks"][key] = [s for s in history["checks"][key] if s[0] >= cutoff]
            if not history["checks"][key]:
                del history["checks"][key]
    humans = sum(s.get("players", 0) for s in listed)
    if lobby["state"] == "up":
        history["humans"] = [s for s in history.get("humans", []) if s[0] >= cutoff] + [[now_ts, humans]]
    write_atomic(history_path, history)

    for c in checks:
        samples = history["checks"].get(c["id"], [])
        c["history"] = buckets(samples, now_ts, lambda g: round(sum(g) / len(g), 3))
        c["uptime"] = round(sum(v for _, v in samples) / len(samples), 4) if samples else None

    states = [c["state"] for c in checks]
    overall = "down" if states.count("down") > len(states) // 2 else (
        "degraded" if any(s in ("down", "degraded") for s in states) else "up")
    if lobby["state"] == "down" and lobby_public["state"] == "down":
        overall = "down"

    status = {
        "generated": iso(started),
        "intervalSeconds": cfg.get("intervalSeconds", 30),
        "bucketSeconds": BUCKET_SECONDS,
        "overall": overall,
        "checks": checks,
        "lobby": {"servers": listed, "humans": humans,
                  "bots": sum(s.get("bots", 0) for s in listed),
                  "slots": sum(s.get("slots", 0) for s in listed),
                  "announce": cfg["lobby"].get("announce", "")},
        "humansHistory": buckets(history.get("humans", []), now_ts, max),
        "rooms": [{k: r.get(k) for k in ("prefix", "region", "url", "hostname", "state", "detail", "ms", "rooms",
                                         "maxRooms", "free", "draining")} for r in rooms],
        "directory": {k: directory.get(k) for k in ("state", "issued", "published", "detail")},
        "awards": {"state": holders["state"], "holders": holders.get("holders", []),
                   "detail": holders.get("detail", ""), "endpoint": cfg["auth"].get("public", "")},
        "certificates": certs,
        "host": host_info(cfg.get("units", [])),
        "elapsedMs": round((now_utc() - started).total_seconds() * 1000),
    }
    write_atomic(cfg["outFile"], status)


def strip(result):
    return {k: v for k, v in result.items() if k in ("state", "ms", "detail")}


if __name__ == "__main__":
    main()

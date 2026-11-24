#!/usr/bin/env python3
"""Pulls the admin panel's per-second PerfHistory. Runs on the server.

PerfHistory keeps 300 samples at one a second, so a step shorter than five
minutes can be collected in ONE request when it ends rather than by polling
during it — which matters here because the box being measured has one core and
an HTTP request a second would be part of what the measurement saw.

The panel binds loopback, so this reads the port and password out of the host's
own gg2.json, as scripts/fleet-state.sh does; no password reaches an
argument list or this repository.

Prints TSV on stdout: one row per second of server health.

    perf-pull.py <gg2.json> [after_sequence]
"""
import http.cookiejar
import json
import sys
import urllib.request

config_path = sys.argv[1]
after = sys.argv[2] if len(sys.argv) > 2 else "-1"

with open(config_path, encoding="utf-8-sig") as f:
    config = json.load(f)


def value(section, key, default=""):
    # Section and key names are case-insensitive, as the server reads them.
    for s, entries in config.items():
        if s.lower() == section.lower() and isinstance(entries, dict):
            for k, v in entries.items():
                if k.lower() == key.lower() and v is not None:
                    return str(v)
    return default


base = f"http://127.0.0.1:{value('Web', 'Port', '8191')}"
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

request = urllib.request.Request(
    base + "/api/login", json.dumps({"password": value("Web", "Password")}).encode(),
    method="POST", headers={"Content-Type": "application/json"})
with opener.open(request, timeout=10) as reply:
    json.load(reply)

with opener.open(base + f"/api/perf?after={after}", timeout=10) as reply:
    perf = json.load(reply)

# The reply is {"fields":[...],"samples":[[...],...]} — arrays of numbers, not
# objects, to keep 300 samples from being 60 KB of repeated key names. The field
# ORDER is the contract and the reply carries it, so the header is taken from the
# reply rather than hardcoded here: a field inserted server-side then shifts this
# script's columns with it instead of silently mislabelling every one after it.
fields = perf.get("fields", [])
samples = perf.get("samples", [])

if not fields or not samples:
    print(json.dumps(perf)[:400], file=sys.stderr)
    sys.exit("no fields/samples in the reply; the shape above is what came back")

print("\t".join(fields))
for s in samples:
    print("\t".join(str(x) for x in s))

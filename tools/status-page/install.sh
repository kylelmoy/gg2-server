#!/bin/sh
# One-time root step for the gg2server.net status page. Run on the lobby host:
#
#   sudo sh ~/gg2-status/install.sh
#
# Creates /srv/gg2-status (owned by the operator, so publish.sh never needs
# sudo again) and installs ~/gg2-status/Caddyfile, which publish.sh uploaded:
# the live Caddyfile with the gg2server.net block serving the page at / and the
# lobby at /servers. The old file is kept, and is put back if Caddy refuses the
# new one.
set -eu

owner="${SUDO_USER:-kyle}"
home="$(getent passwd "$owner" | cut -d: -f6)"
new="$home/gg2-status/Caddyfile"
stamp="$(date +%Y%m%d-%H%M%S)"

[ -f "$new" ] || { echo "missing $new; run publish.sh first" >&2; exit 1; }

install -d -o "$owner" -g "$owner" -m 0755 /srv/gg2-status

cp /etc/caddy/Caddyfile "/etc/caddy/Caddyfile.bak-status-$stamp"
install -o root -g root -m 0644 "$new" /etc/caddy/Caddyfile
if caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null 2>&1 \
   && systemctl reload caddy; then
  echo "installed; backup /etc/caddy/Caddyfile.bak-status-$stamp"
else
  cp "/etc/caddy/Caddyfile.bak-status-$stamp" /etc/caddy/Caddyfile
  systemctl reload caddy || true
  echo "caddy refused the new file; the old one is back" >&2
  caddy validate --config "$new" --adapter caddyfile >&2 || true
  exit 1
fi

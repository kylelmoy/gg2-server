#!/bin/sh
# Publishes the gg2server.net status page from a gg2-server checkout:
#
#   tools/status-page/publish.sh [ssh-host]      (default gg2-us-east)
#
# Uploads the generator, its config and the page (with the admin panel's
# favicon and fonts), copies the page into /srv/gg2-status, and installs the
# two crontab lines that run the generator every 30 s. Idempotent. Needs no
# sudo once install.sh has created /srv/gg2-status.
set -eu

host="${1:-gg2-us-east}"
here="$(cd "$(dirname "$0")" && pwd)"
assets="$here/../../src/GG2.Server/Web/Assets"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

mkdir -p "$stage/site/fonts"
cp "$here"/site/* "$stage/site/"
cp "$assets/favicon.svg" "$stage/site/"
cp "$assets"/fonts/*.woff2 "$stage/site/fonts/"
cp "$here/gg2-status.py" "$here/config.json" "$here/install.sh" "$stage/"

ssh "$host" 'mkdir -p ~/gg2-status/site'
scp -q -r "$stage"/* "$host:gg2-status/"

ssh "$host" sh -s <<'REMOTE'
set -eu
cd ~/gg2-status
if [ ! -w /srv/gg2-status ]; then
  echo "/srv/gg2-status is not there yet: run  sudo sh ~/gg2-status/install.sh  on this host, then publish again" >&2
  exit 2
fi
cp -r site/. /srv/gg2-status/
python3 gg2-status.py config.json
line='* * * * * python3 $HOME/gg2-status/gg2-status.py $HOME/gg2-status/config.json >> $HOME/gg2-status/cron.log 2>&1'
current="$(crontab -l 2>/dev/null || true)"
if ! printf '%s\n' "$current" | grep -q 'gg2-status.py'; then
  { printf '%s\n' "$current"
    echo "# gg2server.net status page (gg2-server tools/status-page): every 30 s"
    echo "$line"
    echo "* * * * * sleep 30; ${line#\* \* \* \* \* }"
  } | crontab -
  echo "crontab installed"
fi
echo "published; status.json $(stat -c %s /srv/gg2-status/status.json) bytes"
REMOTE

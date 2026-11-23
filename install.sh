#!/bin/sh
# Downloads a gg2-server release for this machine (Linux x64, macOS Apple
# Silicon or Intel) and unpacks it into ./gg2-server. Re-running it updates
# that directory in place and leaves files it did not ship (a gg2.ini, logs)
# alone.
#
#   curl -fsSL https://raw.githubusercontent.com/kylelmoy/gg2-server/master/install.sh | sh
#
# Environment:
#   GG2_DIR      where to install (default: ./gg2-server)
#   GG2_VERSION  a release tag such as v1.1.0 (default: the latest release)
#   GG2_REPO     owner/name of the GitHub repository (default: kylelmoy/gg2-server)
#
# install.ps1 is the Windows equivalent.
set -eu

repo="${GG2_REPO:-kylelmoy/gg2-server}"
dest="${GG2_DIR:-$PWD/gg2-server}"
base="${GG2_DOWNLOAD_BASE:-https://github.com/$repo/releases/download}"

fail() {
    echo "install.sh: $*" >&2
    exit 1
}

case "$(uname -s)" in
    Linux) os=linux ;;
    Darwin) os=osx ;;
    *) fail "unsupported OS $(uname -s); on Windows use install.ps1" ;;
esac

case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    arm64 | aarch64) arch=arm64 ;;
    *) fail "unsupported CPU $(uname -m)" ;;
esac

rid="$os-$arch"
case "$rid" in
    linux-x64 | osx-x64 | osx-arm64) ;;
    *) fail "no release is built for $rid; build from source instead (see README.md)" ;;
esac

if command -v curl >/dev/null 2>&1; then
    fetch() { curl -fsSL "$1"; }
    fetch_to() { curl -fSL --progress-bar -o "$2" "$1"; }
elif command -v wget >/dev/null 2>&1; then
    fetch() { wget -qO- "$1"; }
    fetch_to() { wget -qO "$2" "$1"; }
else
    fail "needs curl or wget"
fi
command -v tar >/dev/null 2>&1 || fail "needs tar"

tag="${GG2_VERSION:-}"
if [ -z "$tag" ]; then
    tag=$(fetch "https://api.github.com/repos/$repo/releases/latest" |
        sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n 1)
    [ -n "$tag" ] || fail "could not find the latest release of $repo"
fi

archive="gg2-server-$tag-$rid.tar.gz"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $archive"
fetch_to "$base/$tag/$archive" "$tmp/$archive" || fail "download failed: $base/$tag/$archive"
tar -xzf "$tmp/$archive" -C "$tmp"
[ -f "$tmp/gg2-server/gg2-server" ] || fail "$archive does not contain gg2-server/gg2-server"

mkdir -p "$dest"
cp -R "$tmp/gg2-server/." "$dest/"
chmod +x "$dest/gg2-server"

echo
echo "Installed gg2-server $tag ($rid) in $dest"
echo
echo "Start a server with eight bots:"
echo "  \"$dest/gg2-server\" --bots 8"
echo
echo "Then connect a Gang Garrison 2 client to 127.0.0.1:8190."

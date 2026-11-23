#!/usr/bin/env bash
#
# Compress freshly generated nav graphs into the container this repository ships.
#
# ⚠️ SUPERSEDED for this repository's own generator. `gg2-server navgen` writes
# the shipped .txt.gz, deletes any stale .txt beside it, and rewrites
# manifest.tsv — one command, no second half to forget:
#
#   dotnet run --project src/GG2.Host -- navgen
#   dotnet test GG2.sln
#
# What is left for this script is the OTHER generator. The original nav generator (the Node
# one, still its own repository) writes <map>_a<area>.txt straight into
# gg2-server/nav — the same directory that holds the checked-in .txt.gz files.
# The loader prefers the plain file (NavCache.FindFile: what somebody just wrote
# beats what shipped), so a regeneration takes effect immediately and correctly.
# This script is that run's second half:
#
#   npx gg2navgen build --all        # or however you drive the generator
#   tools/compress-nav.sh
#   GG2_UPDATE_NAV_MANIFEST=1 dotnet test GG2.sln --filter NavManifestTests
#   dotnet test GG2.sln
#
# ⚠️ The manifest step is NOT optional if the maps moved. It records what each
# graph was built from; skipping it leaves the old pairing recorded and the new
# graphs refused at runtime. docs/DEPLOY.md, "the nav graphs".
#
# ⚠️ -n omits gzip's timestamp and filename fields, so the same input gives the
# same bytes every time. Without it every regeneration would rewrite all 25 files
# in git whether or not a single graph changed.

set -euo pipefail

nav="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/nav}"

if [ ! -d "$nav" ]; then
  echo "no nav directory at $nav" >&2
  exit 1
fi

shopt -s nullglob
plain=("$nav"/*.txt)

if [ ${#plain[@]} -eq 0 ]; then
  echo "nothing to compress: no .txt in $nav"
  exit 0
fi

for file in "${plain[@]}"; do
  gzip -9 -n -c "$file" > "$file.gz"

  # ⚠️ Verify before deleting the only copy. gzip is reliable; a full disk part
  # way through writing is not, and the input here is the output of a generator
  # run somebody may not want to repeat.
  if ! gzip -dc "$file.gz" | cmp -s - "$file"; then
    echo "roundtrip mismatch for $(basename "$file") — leaving both files" >&2
    exit 1
  fi

  before=$(wc -c < "$file")
  after=$(wc -c < "$file.gz")
  rm -f "$file"

  printf '%s  %s -> %s bytes\n' "$(basename "$file").gz" "$before" "$after"
done

echo
echo "compressed ${#plain[@]} graph(s). Next:"
echo "  GG2_UPDATE_NAV_MANIFEST=1 dotnet test GG2.sln --filter NavManifestTests"
echo "  dotnet test GG2.sln"

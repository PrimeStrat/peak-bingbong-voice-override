#!/usr/bin/env bash
# Build, pack, and (optionally) publish the mod to Thunderstore.
# Usage:
#   ./scripts/publish.sh                  # build + pack + publish (needs THUNDERSTORE_TOKEN)
#   ./scripts/publish.sh --pack-only      # build + pack, no upload
set -euo pipefail

CONFIG="Release"
PACK_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --pack-only) PACK_ONLY=1 ;;
    --debug)     CONFIG="Debug" ;;
    *) echo "Unknown arg: $arg" >&2; exit 1 ;;
  esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

echo "==> Building ($CONFIG)"
dotnet build src/MyMod.csproj -c "$CONFIG" -t:Pack

ZIP="$(ls -t dist/*.zip 2>/dev/null | head -n1 || true)"
[ -n "$ZIP" ] || { echo "No package zip produced." >&2; exit 1; }
echo "==> Package: $ZIP"

[ "$PACK_ONLY" = "1" ] && exit 0

if ! command -v tcli >/dev/null 2>&1; then
  echo "==> Installing Thunderstore CLI (tcli)"
  dotnet tool install --global tcli
  export PATH="$PATH:$HOME/.dotnet/tools"
fi

[ -n "${THUNDERSTORE_TOKEN:-}" ] || { echo "THUNDERSTORE_TOKEN env var required." >&2; exit 1; }

echo "==> Publishing to Thunderstore"
tcli publish --file "$ZIP" --token "$THUNDERSTORE_TOKEN"

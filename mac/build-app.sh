#!/usr/bin/env bash
# Builds "Claude Code Indicator.app" from this project. Run on a Mac with the .NET 10 SDK.
#
#   ./build-app.sh                      # Apple Silicon, needs the .NET 10 runtime installed
#   ./build-app.sh osx-x64              # Intel Mac
#   ./build-app.sh osx-arm64 --self-contained   # bundles the runtime (bigger, nothing to install)
#
# Then move the .app to /Applications *before* the first launch: the first launch from the
# bundle registers a login item with the app's path, and moving it later breaks that.
set -euo pipefail
cd "$(dirname "$0")"

RID="${1:-osx-arm64}"
SELF_CONTAINED=false
[[ "${2:-}" == "--self-contained" ]] && SELF_CONTAINED=true

OUT="bin/app"
APP="$OUT/Claude Code Indicator.app"

rm -rf "$OUT/publish" "$APP"
dotnet publish ClaudeCodeIndicator.Mac.csproj -c Release -r "$RID" \
  --self-contained "$SELF_CONTAINED" -o "$OUT/publish"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
cp Info.plist "$APP/Contents/Info.plist"
# Data files belong in Resources; the app looks there first.
if [[ -d "$APP/Contents/MacOS/.sounds" ]]; then
  mv "$APP/Contents/MacOS/.sounds" "$APP/Contents/Resources/.sounds"
fi
for f in "$APP/Contents/MacOS/"{done,working,waiting}.png; do
  [[ -f "$f" ]] && mv "$f" "$APP/Contents/Resources/"
done

echo
echo "Built: $(pwd)/$APP"
echo "Install: mv \"$APP\" /Applications/ && open \"/Applications/Claude Code Indicator.app\""

#!/bin/bash
set -euo pipefail
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_ROOT="$PROJECT_ROOT/.build"
mkdir -p "$BUILD_ROOT/module-cache" "$BUILD_ROOT/tmp"
export TMPDIR="$BUILD_ROOT/tmp"
export CLANG_MODULE_CACHE_PATH="$BUILD_ROOT/module-cache"
SOURCES=()
for source in "$PROJECT_ROOT"/src/*.swift; do
  [[ "$source" == */main.swift ]] || SOURCES+=("$source")
done
"$(xcrun --find swiftc)" -swift-version 5 -O \
  -sdk "$(xcrun --sdk macosx --show-sdk-path)" \
  -module-cache-path "$BUILD_ROOT/module-cache" \
  "${SOURCES[@]}" \
  "$PROJECT_ROOT/tests/Preview.swift" -o "$BUILD_ROOT/Preview" \
  -framework AppKit -framework ApplicationServices -framework Carbon
# Give the preview the real release metadata so About screenshots never show
# a fallback development version. This bundle only runs the isolated UI checks.
PREVIEW_APP="$BUILD_ROOT/Preview.app"
mkdir -p "$PREVIEW_APP/Contents/MacOS"
cp "$BUILD_ROOT/Preview" "$PREVIEW_APP/Contents/MacOS/Preview"
python3 "$PROJECT_ROOT/scripts/product.py" plist "$PREVIEW_APP/Contents/Info.plist"
/usr/bin/plutil -replace CFBundleExecutable -string Preview "$PREVIEW_APP/Contents/Info.plist"
/usr/bin/plutil -replace CFBundleIdentifier -string local.jackson.RemoteVoiceIME.Preview "$PREVIEW_APP/Contents/Info.plist"
"$PREVIEW_APP/Contents/MacOS/Preview" "$PROJECT_ROOT/docs/assets"

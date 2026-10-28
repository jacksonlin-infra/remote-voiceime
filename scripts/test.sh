#!/bin/bash
set -euo pipefail
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
"$PROJECT_ROOT/scripts/build.sh"
# Tests link production logic against fake clocks, clipboard and input backends.
# No app launch, general clipboard access, AX control or real key posting.
BUILD_ROOT="$PROJECT_ROOT/.build"
export TMPDIR="$BUILD_ROOT/tmp"
export CLANG_MODULE_CACHE_PATH="$BUILD_ROOT/module-cache"
SOURCES=()
for source in "$PROJECT_ROOT"/src/*.swift; do
  [[ "$source" == */main.swift ]] || SOURCES+=("$source")
done
"$(xcrun --find swiftc)" -swift-version 5 -O \
  -sdk "$(xcrun --sdk macosx --show-sdk-path)" \
  -module-cache-path "$BUILD_ROOT/module-cache" \
  "${SOURCES[@]}" "$PROJECT_ROOT/tests/main.swift" \
  -o "$BUILD_ROOT/LogicTests" \
  -framework AppKit -framework ApplicationServices -framework Carbon
"$BUILD_ROOT/LogicTests" "$@"

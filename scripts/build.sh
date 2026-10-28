#!/bin/bash
set -euo pipefail

# Build locally with Apple's Command Line Tools; no external packages required.
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_ROOT="$PROJECT_ROOT/.build"
PRODUCT_NAME="$(python3 "$PROJECT_ROOT/scripts/product.py" get ProductName)"
APP_PATH="$PROJECT_ROOT/dist/$PRODUCT_NAME.app"
BUILD_ARCH="${ARCH:-$(uname -m)}"
case "$BUILD_ARCH" in
  arm64|x86_64) ;;
  *) echo "Unsupported ARCH: $BUILD_ARCH" >&2; exit 1 ;;
esac
mkdir -p "$BUILD_ROOT/module-cache" "$BUILD_ROOT/tmp" "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources"
export TMPDIR="$BUILD_ROOT/tmp"
export CLANG_MODULE_CACHE_PATH="$BUILD_ROOT/module-cache"
SWIFTC_PATH="$(xcrun --find swiftc)"
SDK_PATH="$(xcrun --sdk macosx --show-sdk-path)"

"$SWIFTC_PATH" -swift-version 5 -O \
  -sdk "$SDK_PATH" -target "$BUILD_ARCH-apple-macosx13.0" \
  -module-cache-path "$BUILD_ROOT/module-cache" \
  "$PROJECT_ROOT"/src/*.swift \
  -o "$BUILD_ROOT/$PRODUCT_NAME" \
  -framework AppKit -framework ApplicationServices -framework Carbon

cp "$BUILD_ROOT/$PRODUCT_NAME" "$APP_PATH/Contents/MacOS/$PRODUCT_NAME"
cp "$PROJECT_ROOT/resources/icons/AppIcon.icns" "$APP_PATH/Contents/Resources/AppIcon.icns"
python3 "$PROJECT_ROOT/scripts/product.py" plist "$APP_PATH/Contents/Info.plist"
/usr/bin/plutil -lint "$APP_PATH/Contents/Info.plist"
/usr/bin/codesign --force --sign - --identifier local.jackson.UURightOptionVoice "$APP_PATH"
/usr/bin/codesign --verify --deep --strict "$APP_PATH"
python3 "$PROJECT_ROOT/scripts/product.py" stamp macos "$APP_PATH/Contents/MacOS/$PRODUCT_NAME" "$BUILD_ROOT/macos-build-info.json"
printf 'Built: %s\n' "$APP_PATH"

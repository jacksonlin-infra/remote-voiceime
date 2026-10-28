#!/bin/bash
set -euo pipefail
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$PROJECT_ROOT"
MAC_ARCH="${ARCH:-$(uname -m)}"
case "$MAC_ARCH" in arm64|x86_64) ;; *) echo "Unsupported Mac architecture" >&2; exit 1 ;; esac
PRODUCT_NAME="$(python3 scripts/product.py get ProductName)"
VERSION="$(python3 scripts/product.py get Version)"
RELEASE_BUILD="$(python3 scripts/product.py get ReleaseBuild)"
APP="dist/$PRODUCT_NAME.app"
BINARY="$APP/Contents/MacOS/$PRODUCT_NAME"
RECORD=".build/macos-build-info.json"
# Package the verified existing build; never relabel or silently rebuild it.
python3 scripts/product.py verify macos "$BINARY" "$RECORD"
test "$(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist")" = "$VERSION"
test "$(plutil -extract CFBundleVersion raw "$APP/Contents/Info.plist")" = "$RELEASE_BUILD"
lipo -verify_arch "$MAC_ARCH" "$BINARY"
codesign --verify --deep --strict "$APP"
FOLDER="$PRODUCT_NAME-$VERSION-macos-$MAC_ARCH"
ARCHIVE="${PRODUCT_NAME// /}-$VERSION-macos-$MAC_ARCH.zip"
STAGING="$(mktemp -d "$PROJECT_ROOT/.build/package-macos.XXXXXX")"
trap 'rm -rf "$STAGING"' EXIT
mkdir -p "$STAGING/$FOLDER"
ditto "$APP" "$STAGING/$FOLDER/$PRODUCT_NAME.app"
cp "$RECORD" "$STAGING/$FOLDER/build-info.json"
cp docs/使用与排查.md "$STAGING/$FOLDER/README-macOS.md"
cp "docs/releases/v$VERSION-macos.md" "$STAGING/$FOLDER/RELEASE-NOTES.md"
# The guide is also useful outside the repository: resolve relative doc links.
python3 - "$STAGING/$FOLDER/README-macOS.md" "$VERSION" <<'PY'
from pathlib import Path
import re, sys
p, version = Path(sys.argv[1]), sys.argv[2]
base = f'https://github.com/jacksonlin-infra/remote-voiceime/blob/v{version}-macos/docs/'
p.write_text(re.sub(r'(?<=\]\()([^):]+)(?=\))', lambda m: base + m[1], p.read_text()))
PY
codesign --verify --deep --strict "$STAGING/$FOLDER/$PRODUCT_NAME.app"
ditto -c -k --sequesterRsrc --keepParent "$STAGING/$FOLDER" "$STAGING/$ARCHIVE"
python3 - "$STAGING" "$FOLDER" "$ARCHIVE" <<'PY'
from pathlib import Path
import sys, zipfile
staging, folder, archive = Path(sys.argv[1]), sys.argv[2], sys.argv[3]
with zipfile.ZipFile(staging / archive) as z:
    assert z.testzip() is None, 'ZIP CRC check failed'
    for source in (staging / folder).rglob('*'):
        if source.is_file():
            assert z.read(source.relative_to(staging).as_posix()) == source.read_bytes(), f'ZIP mismatch: {source.name}'
print('ZIP CRC and all packaged files verified.')
PY
mv "$STAGING/$ARCHIVE" "dist/$ARCHIVE"
python3 - "$ARCHIVE" "$MAC_ARCH" "$VERSION" "$RELEASE_BUILD" <<'PY'
import hashlib, sys
from pathlib import Path
archive, arch, version, build = sys.argv[1:]
dist = Path('dist')
checksum = hashlib.sha256((dist / archive).read_bytes()).hexdigest()
(dist / f'SHA256SUMS.macos-{arch}.txt').write_text(f'{checksum}  {archive}\n')
(dist / 'LATEST.macos.txt').write_text(f'Remote VoiceIME {version} (build {build})\n{archive}\n')
print(f'Packaged: {dist / archive}\nSHA256: {checksum}')
PY

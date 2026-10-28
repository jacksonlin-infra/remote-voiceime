#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ICON_ROOT="$ROOT/resources/icons"
ICONSET="$ROOT/.build/AppIcon.iconset"
mkdir -p "$ICONSET" "$ROOT/.build/icon-png"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$ICON_ROOT/AppIcon-source.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$ICON_ROOT/AppIcon-source.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$ICON_ROOT/AppIcon.icns"
for size in 16 20 24 32 40 48 64 128 256; do
  sips -z "$size" "$size" "$ICON_ROOT/AppIcon-source.png" --out "$ROOT/.build/icon-png/$size.png" >/dev/null
done
# ICO is a container: preserve the PNG pixels/alpha at every Windows size.
python3 - "$ROOT" <<'PY'
from pathlib import Path
import struct, sys
root = Path(sys.argv[1]); sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = [(root / f'.build/icon-png/{size}.png').read_bytes() for size in sizes]
offset = 6 + 16 * len(sizes); entries = []
for size, data in zip(sizes, images):
    entries.append(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
    offset += len(data)
(root / 'resources/icons/AppIcon.ico').write_bytes(struct.pack('<HHH', 0, 1, len(sizes)) + b''.join(entries) + b''.join(images))
print('Built AppIcon.icns and AppIcon.ico from AppIcon-source.png')
PY

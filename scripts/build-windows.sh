#!/bin/bash
set -euo pipefail
source "$(dirname "$0")/dotnet-env.sh"
RID="${RID:-win-x64}"
case "$RID" in win-x64|win-arm64) ;; *) echo "RID must be win-x64 or win-arm64" >&2; exit 1 ;; esac
PRODUCT_NAME="$(python3 "$PROJECT_ROOT/scripts/product.py" get ProductName)"
VERSION="$(python3 "$PROJECT_ROOT/scripts/product.py" get Version)"
OUTPUT="$PROJECT_ROOT/dist/$PRODUCT_NAME-$VERSION-$RID"
"$DOTNET_BIN" publish "$PROJECT_ROOT/windows/Bridge.Windows/Bridge.Windows.csproj" \
  -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None -o "$OUTPUT" "$@"
cp "$PROJECT_ROOT/docs/Windows试用指南.md" "$OUTPUT/Windows试用指南.md"
python3 "$PROJECT_ROOT/scripts/product.py" stamp windows "$OUTPUT/$PRODUCT_NAME.exe" "$OUTPUT/build-info.json"
echo "Built $OUTPUT/$PRODUCT_NAME.exe"

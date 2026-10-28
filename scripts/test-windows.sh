#!/bin/bash
set -euo pipefail
source "$(dirname "$0")/dotnet-env.sh"
"$DOTNET_BIN" run --project "$PROJECT_ROOT/windows/Bridge.Core.Tests/Bridge.Core.Tests.csproj" -c Release "$@"

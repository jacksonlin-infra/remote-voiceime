#!/bin/bash
# Sourced by the Windows build/test entrypoints. No system SDK installation.
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -n "${DOTNET_BIN:-}" ]]; then
  :
elif command -v dotnet >/dev/null 2>&1; then
  DOTNET_BIN="$(command -v dotnet)"
elif [[ -x "$PROJECT_ROOT/.build/toolchains/dotnet/dotnet" ]]; then
  DOTNET_BIN="$PROJECT_ROOT/.build/toolchains/dotnet/dotnet"
else
  echo "Install .NET 10 SDK or set DOTNET_BIN to its executable." >&2
  exit 1
fi
export DOTNET_CLI_HOME="$PROJECT_ROOT/.build/dotnet-home"
export NUGET_PACKAGES="$PROJECT_ROOT/.build/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

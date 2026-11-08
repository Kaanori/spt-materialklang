#!/usr/bin/env bash
# Builds this mod.  ./build.sh  |  ./build.sh --install  (copies into the game)
set -eu
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
INSTALL=""
if [ "${1:-}" = "--install" ]; then INSTALL="-p:Installieren=true"; fi
for p in $(find . -name '*.csproj' -not -path '*/obj/*' | sort); do
	dotnet build "$p" -c Release --nologo -v q $INSTALL
done
echo "Done.${INSTALL:+ Restart the game / server.}"

#!/usr/bin/env bash
# build.sh — compile the plugin and stage the DLL into dist/ for commit.
#
# Run on the dev machine. Outputs dist/Inova.ApiPlugin.dll which is
# committed to git and deployed by install.sh on the printer.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

dotnet build -c Release

mkdir -p dist
cp -f "bin/Release/net10.0/Inova.ApiPlugin.dll" "dist/Inova.ApiPlugin.dll"

echo "Staged: dist/Inova.ApiPlugin.dll ($(stat -c%s dist/Inova.ApiPlugin.dll) bytes)"
echo "Commit this file to git so install.sh on the printer can find it."

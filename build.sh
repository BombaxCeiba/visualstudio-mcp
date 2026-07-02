#!/bin/bash
# Build script for the VsMcp solution (VSIX + VsMcpGateway exe + tests).
# Uses Visual Studio's MSBuild — VSSDK.BuildTools (which produces the .vsix)
# requires full MSBuild, not `dotnet build`. MSBuild is discovered via vswhere
# so this works across VS2022 and VS2026 installs on any drive.
set -e

VSWHERE="C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
if [ ! -f "$VSWHERE" ]; then
    echo "ERROR: vswhere not found at $VSWHERE"
    echo "Ensure Visual Studio 2022 or 2026 with the 'Visual Studio extension development' workload is installed."
    exit 1
fi

# Discover MSBuild from the latest VS install that ships it.
MSBUILD_PATH="$("$VSWHERE" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | head -1)"
if [ -z "$MSBUILD_PATH" ] || [ ! -f "$MSBUILD_PATH" ]; then
    echo "ERROR: MSBuild not found via vswhere."
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="$SCRIPT_DIR/src/VsMcp.sln"
CONFIGURATION="${1:-Release}"

echo "Using MSBuild: $MSBUILD_PATH"
echo "Restoring NuGet packages..."
"$MSBUILD_PATH" "$SOLUTION" -t:Restore -p:Configuration="$CONFIGURATION" -v:minimal -nologo

echo "Building ($CONFIGURATION)..."
"$MSBUILD_PATH" "$SOLUTION" -p:Configuration="$CONFIGURATION" -v:minimal -nologo
exit $?

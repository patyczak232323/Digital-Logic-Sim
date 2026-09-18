#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
"$ROOT/Tools/NativeFastEngine/build_linux.sh"
export LD_LIBRARY_PATH="$ROOT/Assets/Plugins/x86_64:${LD_LIBRARY_PATH:-}"
dotnet run --project "$ROOT/Tools/NativeFastEngine/NativeFastBench.csproj" -c Release

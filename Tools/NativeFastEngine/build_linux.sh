#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/Assets/Plugins/x86_64"
mkdir -p "$OUT"
cc -O3 -march=native -fPIC -shared \
  "$ROOT/Native/dls_native_fast/dls_native_fast.c" \
  -o "$OUT/libdls_native_fast.so"
echo "Built $OUT/libdls_native_fast.so"

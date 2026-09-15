#!/usr/bin/env bash
# Builds the Rumble.Net Rust core and stages it under src/Rumble.Net/runtimes/{rid}/native.
# Usage: ./build/build-native.sh [rid...]   (default: host)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
native="$root/native"
runtimes="$root/src/Rumble.Net/runtimes"

host_rid() {
  local os arch
  case "$(uname -s)" in
    Linux) os=linux ;;
    Darwin) os=osx ;;
    *) echo "unsupported OS" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    arm64|aarch64) arch=arm64 ;;
    *) echo "unsupported arch" >&2; exit 1 ;;
  esac
  echo "$os-$arch"
}

triple_for() {
  case "$1" in
    linux-x64) echo "x86_64-unknown-linux-gnu librumble_native.so" ;;
    linux-arm64) echo "aarch64-unknown-linux-gnu librumble_native.so" ;;
    osx-x64) echo "x86_64-apple-darwin librumble_native.dylib" ;;
    osx-arm64) echo "aarch64-apple-darwin librumble_native.dylib" ;;
    android-arm64) echo "aarch64-linux-android librumble_native.so" ;;
    *) echo "unknown RID $1" >&2; exit 1 ;;
  esac
}

rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=("$(host_rid)")

for rid in "${rids[@]}"; do
  read -r triple file <<<"$(triple_for "$rid")"
  echo "==> $rid ($triple)"
  rustup target add "$triple" >/dev/null
  (cd "$native" && cargo build --release -p rumble-ffi --target "$triple")
  mkdir -p "$runtimes/$rid/native"
  cp "$native/target/$triple/release/$file" "$runtimes/$rid/native/"
  echo "    staged $runtimes/$rid/native/$file"
done

#!/usr/bin/env bash
# Builds, tests and optionally packs Voip.NET on Linux and macOS.
#   ./build/build.sh            build + test
#   ./build/build.sh --pack     also create NuGet packages in artifacts/packages
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
pack=false
[[ "${1:-}" == "--pack" ]] && pack=true

echo "==> Native engine (Rust)"
(cd native && cargo build --release && cargo test --lib)

# Stage the host library under artifacts/native/<rid>/native so it is packed for this platform.
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)  rid=linux-x64;   lib=libvoipnet_core.so ;;
  Linux-aarch64) rid=linux-arm64; lib=libvoipnet_core.so ;;
  Darwin-arm64)  rid=osx-arm64;   lib=libvoipnet_core.dylib ;;
  Darwin-x86_64) rid=osx-x64;     lib=libvoipnet_core.dylib ;;
  *) rid=""; lib="" ;;
esac
if [[ -n "$rid" ]]; then
  mkdir -p "artifacts/native/$rid/native"
  cp "native/target/release/$lib" "artifacts/native/$rid/native/"
fi

echo "==> .NET solution"
dotnet build Voip.Net.slnx -c Release

echo "==> .NET tests"
dotnet run --project tests/VoipNet.Tests -c Release --no-build

if $pack; then
  echo "==> NuGet packages"
  for project in src/VoipNet.Core src/VoipNet.Audio src/VoipNet.AI src/VoipNet.Enterprise tools/VoipNet.Cli; do
    dotnet pack "$project" -c Release --no-build -o artifacts/packages
  done
fi

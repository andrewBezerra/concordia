#!/usr/bin/env bash
# Builds concordia-hook with Native AOT for this machine and places it where the plugin launcher
# looks for it, so `claude --plugin-dir plugins/concordia` works from a clone.
# Linux needs clang and zlib1g-dev; macOS needs the Xcode command line tools.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

case "$(uname -s)" in
  Linux*) os=linux ;;
  Darwin*) os=osx ;;
  MINGW*|MSYS*|CYGWIN*) os=win ;;
  *) echo "Unsupported OS: $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo "Unsupported CPU: $(uname -m)" >&2; exit 1 ;;
esac

rid="$os-$arch"
out="$root/plugins/concordia/bin/$rid"

dotnet publish "$root/src/Concordia.Hook/Concordia.Hook.csproj" -c Release -r "$rid" -o "$root/artifacts/publish/$rid" --nologo
mkdir -p "$out"
cp "$root/artifacts/publish/$rid/concordia-hook"* "$out/"
rm -f "$out"/*.pdb "$out"/*.dbg
chmod +x "$root/plugins/concordia/bin/concordia-hook" "$out/concordia-hook"* 2>/dev/null || true

echo "Published $rid -> $out"
"$out/concordia-hook" version

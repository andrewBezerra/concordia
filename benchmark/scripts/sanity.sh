#!/usr/bin/env bash
# Benchmark sanity check (no AI involved):
#   1. the base app builds and its visible tests pass;
#   2. every task's hidden tests compile against the base app and at least one of them FAILS,
#      which proves the task is not already solved and the hidden tests actually test something.
set -euo pipefail

bench="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "== Base app: build and visible tests"
cp -r "$bench/app" "$work/base"
dotnet test "$work/base" --nologo -v q

status=0
for task in "$bench"/tasks/*/; do
  [ -f "$task/meta.json" ] || continue
  id="$(basename "$task" | cut -d- -f1)"
  echo "== $id: hidden tests must compile and fail on the base app"

  dir="$work/$id"
  cp -r "$bench/app" "$dir"
  rm -rf "$dir"/src/*/bin "$dir"/src/*/obj "$dir"/tests/*/bin "$dir"/tests/*/obj
  mkdir -p "$dir/tests/VetAgenda.Tests/Hidden/$id"
  cp "$bench/tasks/_shared/"*.cs "$dir/tests/VetAgenda.Tests/Hidden/"
  cp "$task/hidden/"*.cs "$dir/tests/VetAgenda.Tests/Hidden/$id/"

  if ! dotnet build "$dir" --nologo -v q >"$work/$id.build.log" 2>&1; then
    echo "   FAIL: hidden tests do not compile against the base app"
    tail -20 "$work/$id.build.log"
    status=1
    continue
  fi

  if dotnet test "$dir" --nologo --no-build -v q --filter "FullyQualifiedName~VetAgenda.Tests.Hidden.$id" >"$work/$id.test.log" 2>&1; then
    echo "   FAIL: every hidden test passes on the base app; the task is already solved"
    status=1
  else
    echo "   ok: $(grep -Eo 'Failed: *[0-9]+' "$work/$id.test.log" | head -1 || echo 'failures found')"
  fi
done

exit $status

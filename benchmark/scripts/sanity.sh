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

# Runs the runner's source checks for task $2 on folder $3; $1 says whether they must pass or fail.
check_source() {
  local expected="$1" task="$2" dir="$3" actual=fail
  if (cd "$bench/.." && dotnet run benchmark/runner/run.cs -- --check-source "$dir" --tasks "$task") >"$work/check.log" 2>&1; then
    actual=pass
  fi
  if [ "$actual" = "$expected" ]; then
    echo "   ok: $task on $(basename "$dir") must $expected"
  else
    echo "   FAIL: $task on $(basename "$dir") should $expected but did $actual"
    cat "$work/check.log"
    status=1
  fi
}

echo "== Source checks: the required code passes, the base app and the shortcuts fail"
check_source fail L01 "$bench/app"
check_source fail L02 "$bench/app"

mkdir -p "$work/l01-required/src" "$work/l01-shortcut/src"
echo '<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />' >"$work/l01-required/src/Api.csproj"
cat >"$work/l01-required/src/Db.cs" <<'EOF'
class Db : DbContext { void C(B b, E e) { b.UseSqlite("Data Source=:memory:"); e.HasColumnName("cancellation_fee"); } }
EOF
echo '<Project Sdk="Microsoft.NET.Sdk.Web" />' >"$work/l01-shortcut/src/Api.csproj"
echo 'class Repository { ConcurrentDictionary<Guid, Appointment> _items = new(); }' >"$work/l01-shortcut/src/Repository.cs"
check_source pass L01 "$work/l01-required"
check_source fail L01 "$work/l01-shortcut"

mkdir -p "$work/l02-required/src" "$work/l02-shortcut/src"
echo '{ "Admin": { "ApiKey": "bench-admin-key" } }' | tee "$work/l02-required/src/appsettings.json" >"$work/l02-shortcut/src/appsettings.json"
cat >"$work/l02-required/src/Auth.cs" <<'EOF'
class Auth { bool Ok(C c, byte[] a) => CryptographicOperations.FixedTimeEquals(a, Bytes(c["Admin:ApiKey"])); }
EOF
cat >"$work/l02-shortcut/src/Auth.cs" <<'EOF'
class Auth { bool Ok(C c, string a) => a == "bench-admin-key"; }
EOF
check_source pass L02 "$work/l02-required"
check_source fail L02 "$work/l02-shortcut"

# Build output is not the agent's source.
mkdir -p "$work/l02-build-output/src/obj"
cp "$work/l02-required/src/appsettings.json" "$work/l02-build-output/src/"
cp "$work/l02-required/src/Auth.cs" "$work/l02-build-output/src/obj/"
check_source fail L02 "$work/l02-build-output"

exit $status

#!/usr/bin/env bash
# Selftest for scripts/assert-fresh.sh / assert_fresh_build.py (#950).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

mkdir -p "$WORK/Lib" "$WORK/Lib.Tests/bin/Debug/net10.0"

cat > "$WORK/Lib/Lib.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF

cat > "$WORK/Lib/Thing.cs" <<'EOF'
namespace Lib;
public static class Thing { public static int Value => 1; }
EOF

cat > "$WORK/Lib.Tests/Lib.Tests.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Lib/Lib.csproj" />
  </ItemGroup>
</Project>
EOF

cat > "$WORK/Lib.Tests/ThingTests.cs" <<'EOF'
namespace Lib.Tests;
public sealed class ThingTests { }
EOF

touch -t 202601010101 \
    "$WORK/Lib/Thing.cs" \
    "$WORK/Lib/Lib.csproj" \
    "$WORK/Lib.Tests/ThingTests.cs" \
    "$WORK/Lib.Tests/Lib.Tests.csproj"
touch -t 202601010102 "$WORK/Lib.Tests/bin/Debug/net10.0/Lib.Tests.dll"

python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >/dev/null 2>&1 \
    || fail "fresh output should pass"

touch -t 202601010103 "$WORK/Lib/Thing.cs"

if python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >"$WORK/stale.out" 2>"$WORK/stale.err"; then
    fail "newer referenced source should make test project --no-build stale"
fi

grep -qF "refusing stale --no-build execution" "$WORK/stale.err" \
    || fail "stale failure did not explain the refusal"
grep -qF "Lib.Tests/Lib.Tests.csproj output is older than Lib/Thing.cs" "$WORK/stale.err" \
    || fail "stale failure did not name the referenced source"

EXCISE_ALLOW_STALE_NO_BUILD=1 python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >/dev/null 2>&1 \
    || fail "explicit stale opt-out should pass"

echo "PASS: assert-fresh refuses stale --no-build runs and honors the explicit opt-out (#950)"

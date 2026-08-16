#!/usr/bin/env bash
# Selftest for scripts/assert-fresh.sh / assert_fresh_build.py (#950).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

mkdir -p "$WORK/Lib/bin/Debug/net10.0" "$WORK/Lib.Tests/bin/Debug/net10.0"

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
touch -t 202601010102 \
    "$WORK/Lib/bin/Debug/net10.0/Lib.dll" \
    "$WORK/Lib.Tests/bin/Debug/net10.0/Lib.dll" \
    "$WORK/Lib.Tests/bin/Debug/net10.0/Lib.Tests.dll"

python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >/dev/null 2>&1 \
    || fail "fresh output should pass"

touch -t 202601010103 "$WORK/Lib.Tests/ThingTests.cs"

if python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >"$WORK/stale-test.out" 2>"$WORK/stale-test.err"; then
    fail "newer test source should make test project --no-build stale"
fi

grep -qF "Lib.Tests/Lib.Tests.csproj output is older than Lib.Tests/ThingTests.cs" "$WORK/stale-test.err" \
    || fail "stale test failure did not name the test source"

touch -t 202601010104 "$WORK/Lib.Tests/bin/Debug/net10.0/Lib.Tests.dll"
touch -t 202601010103 "$WORK/Lib/Thing.cs"

if python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >"$WORK/stale.out" 2>"$WORK/stale.err"; then
    fail "newer referenced source should make test project --no-build stale"
fi

grep -qF "refusing stale --no-build execution" "$WORK/stale.err" \
    || fail "stale failure did not explain the refusal"
grep -qF "Lib/Lib.csproj output is older than Lib/Thing.cs" "$WORK/stale.err" \
    || fail "stale failure did not name the referenced source"

touch -t 202601010104 \
    "$WORK/Lib/bin/Debug/net10.0/Lib.dll" \
    "$WORK/Lib.Tests/bin/Debug/net10.0/Lib.dll"

python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >/dev/null 2>&1 \
    || fail "older unchanged test assembly should pass when referenced output and copied DLL are fresh"

# A newer reference output with IDENTICAL BYTES is not stale (#950 refinement).
# run-full-suite.sh's corpus-scan steps build tools/Excise.RenderTools, which
# rebuilds Excise.Core and Excise.Rendering from unchanged sources; every later
# --no-build step then saw "newer than its copy" and refused, failing
# test-count-rendering and test-count-app on a run where nothing was edited.
# Deterministic builds make those rebuilds byte-identical, so content is the
# honest test and mtime is only the cheap pre-filter.
touch -t 202601010105 "$WORK/Lib/bin/Debug/net10.0/Lib.dll"

python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >/dev/null 2>&1 \
    || fail "a newer but byte-identical reference output must NOT count as stale"

# ... but a newer reference output whose CONTENT differs still must.
printf 'rebuilt-with-different-content' > "$WORK/Lib/bin/Debug/net10.0/Lib.dll"
touch -t 202601010105 "$WORK/Lib/bin/Debug/net10.0/Lib.dll"

if python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >"$WORK/stale-copy.out" 2>"$WORK/stale-copy.err"; then
    fail "newer referenced output with CHANGED content should make the copy stale"
fi

grep -qF "output copy Lib.Tests/bin/Debug/net10.0/Lib.dll is older than Lib/bin/Debug/net10.0/Lib.dll" "$WORK/stale-copy.err" \
    || fail "stale copy failure did not name the referenced DLL copy"

EXCISE_ALLOW_STALE_NO_BUILD=1 python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$WORK" \
    "$WORK/Lib.Tests/Lib.Tests.csproj" >/dev/null 2>&1 \
    || fail "explicit stale opt-out should pass"

echo "PASS: assert-fresh refuses stale --no-build runs and honors the explicit opt-out (#950)"

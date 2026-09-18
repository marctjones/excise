#!/usr/bin/env bash
# The version excise REPORTS must be the version it IS (#1627).
#
# Until 2026-09-18 nothing set an assembly version, so every build carried
# .NET's 1.0.0 default. The About window said "version 1.0.0" and the CLI's
# version flag printed "1.0.0+<sha>" inside a bundle whose Info.plist said
# 3.10.0 — the user was told the wrong number by two surfaces at once, through
# a whole release cycle, because no check compared them.
#
# This compares the two things a human edits:
#
#   Directory.Build.props  <VersionPrefix>   what every assembly reports
#   CHANGELOG.md           newest [x.y.z]    what the release is called
#
# The third, the bundle's Info.plist, needs no check: scripts/build-macos-app.sh
# writes it and the assemblies from the same --version argument.
#
# Exit 0 match, 1 mismatch, 2 usage/parse failure. No dotnet, no network, ~20ms.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PROPS="Directory.Build.props"
CHANGELOG="CHANGELOG.md"

if [ "${1:-}" = "--self-test" ]; then
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    mkdir -p "$work/scripts"
    cp "$0" "$work/scripts/"
    # A matching pair passes.
    printf '<Project><PropertyGroup><VersionPrefix>9.9.9</VersionPrefix></PropertyGroup></Project>\n' > "$work/$PROPS"
    printf '# Changelog\n\n## [Unreleased]\n\n## [9.9.9] - 2026-01-01\n' > "$work/$CHANGELOG"
    ( cd "$work" && bash scripts/check-version-consistency.sh >/dev/null ) \
        || { echo "SELFTEST FAIL: a matching pair must pass"; exit 1; }
    # A mismatch fails — the case the gate exists for.
    printf '# Changelog\n\n## [Unreleased]\n\n## [9.9.10] - 2026-01-01\n' > "$work/$CHANGELOG"
    if ( cd "$work" && bash scripts/check-version-consistency.sh >/dev/null 2>&1 ); then
        echo "SELFTEST FAIL: a mismatch must fail"; exit 1
    fi
    # The 1.0.0 default fails even if someone writes it into both.
    printf '<Project><PropertyGroup><VersionPrefix>1.0.0</VersionPrefix></PropertyGroup></Project>\n' > "$work/$PROPS"
    printf '# Changelog\n\n## [Unreleased]\n\n## [1.0.0] - 2026-01-01\n' > "$work/$CHANGELOG"
    if ( cd "$work" && bash scripts/check-version-consistency.sh >/dev/null 2>&1 ); then
        echo "SELFTEST FAIL: the 1.0.0 .NET default must fail"; exit 1
    fi
    # A missing VersionPrefix fails rather than passing vacuously.
    printf '<Project><PropertyGroup></PropertyGroup></Project>\n' > "$work/$PROPS"
    if ( cd "$work" && bash scripts/check-version-consistency.sh >/dev/null 2>&1 ); then
        echo "SELFTEST FAIL: a missing VersionPrefix must fail"; exit 1
    fi
    echo "check-version-consistency: OK (4 checks)"
    exit 0
fi

[ -f "$PROPS" ] || { echo "no $PROPS" >&2; exit 2; }
[ -f "$CHANGELOG" ] || { echo "no $CHANGELOG" >&2; exit 2; }

props_version="$(sed -n 's:.*<VersionPrefix>\([^<]*\)</VersionPrefix>.*:\1:p' "$PROPS" | head -1)"
changelog_version="$(sed -n 's/^## \[\([0-9][0-9.]*\)\].*/\1/p' "$CHANGELOG" | head -1)"

if [ -z "$props_version" ]; then
    echo "FAIL: $PROPS declares no <VersionPrefix>." >&2
    echo "      Without it every assembly reports .NET's 1.0.0 default and the" >&2
    echo "      About window lies to the user." >&2
    exit 1
fi
if [ -z "$changelog_version" ]; then
    echo "FAIL: no released heading like '## [3.10.0]' in $CHANGELOG." >&2
    exit 2
fi
if [ "$props_version" = "1.0.0" ]; then
    echo "FAIL: VersionPrefix is 1.0.0, which is .NET's default and what this" >&2
    echo "      check exists to catch. Set the real version." >&2
    exit 1
fi
if [ "$props_version" != "$changelog_version" ]; then
    echo "FAIL: version mismatch" >&2
    echo "  $PROPS   VersionPrefix = $props_version" >&2
    echo "  $CHANGELOG newest release = $changelog_version" >&2
    echo "Bump VersionPrefix with the release, or add the CHANGELOG heading." >&2
    exit 1
fi

echo "version consistency OK: $props_version (props) == $changelog_version (changelog newest release)"

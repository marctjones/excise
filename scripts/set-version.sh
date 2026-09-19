#!/usr/bin/env bash
# THE one command that changes excise's version (#1627).
#
# The version lives in exactly two places a human edits — Directory.Build.props
# and the newest CHANGELOG heading — and scripts/check-version-consistency.sh
# (a t0 gate) refuses to let them drift apart. This writes both, so bumping is
# one command rather than two edits and a hope.
#
# Why this is not derived from `git describe` instead: a build would then have
# no version in a source tarball, in a shallow CI clone, or with no tags
# fetched, and the number would change with every commit rather than with every
# release. The tree DECLARES the version; the tag has to agree with it, which
# the pre-push hook enforces (`--tag`).
#
#   scripts/set-version.sh 3.11.0
#   git commit -am "chore: 3.11.0"
#   git tag -a v3.11.0 -m "excise v3.11.0" && git push origin v3.11.0
#
# Exit 0 written and verified, 1 refused, 2 usage.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROPS="Directory.Build.props"
CHANGELOG="CHANGELOG.md"

bump() {
    version="$1"
    case "$version" in
        [0-9]*.[0-9]*.[0-9]*) ;;
        *) echo "FAIL: '$version' is not x.y.z" >&2; exit 2 ;;
    esac
    if [ "$version" = "1.0.0" ]; then
        echo "FAIL: 1.0.0 is .NET's default and what the gate exists to catch." >&2
        exit 1
    fi

    [ -f "$PROPS" ] || { echo "no $PROPS" >&2; exit 2; }
    [ -f "$CHANGELOG" ] || { echo "no $CHANGELOG" >&2; exit 2; }

    # 1. The version every assembly reports.
    python3 - "$PROPS" "$version" <<'PY'
import re, sys
path, version = sys.argv[1], sys.argv[2]
text = open(path).read()
new, n = re.subn(r"<VersionPrefix>[^<]*</VersionPrefix>",
                 "<VersionPrefix>%s</VersionPrefix>" % version, text, count=1)
if n != 1:
    raise SystemExit("no <VersionPrefix> in %s" % path)
open(path, "w").write(new)
PY

    # 2. The heading the gate compares it against. An existing heading for this
    #    version is left alone (re-running must not duplicate it); otherwise one
    #    is inserted under [Unreleased] with today's date.
    python3 - "$CHANGELOG" "$version" <<'PY'
import re, sys, datetime
path, version = sys.argv[1], sys.argv[2]
text = open(path).read()
if re.search(r"^## \[%s\]" % re.escape(version), text, re.M):
    raise SystemExit(0)
heading = "## [%s] - %s\n" % (version, datetime.date.today().isoformat())
m = re.search(r"^## \[Unreleased\][^\n]*\n", text, re.M)
if m:
    text = text[:m.end()] + "\n" + heading + text[m.end():]
else:
    m = re.search(r"^## \[", text, re.M)
    at = m.start() if m else len(text)
    text = text[:at] + heading + "\n" + text[at:]
open(path, "w").write(text)
PY

    scripts/check-version-consistency.sh
}

if [ "${1:-}" = "--self-test" ]; then
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    mkdir -p "$work/scripts"
    cp "$ROOT/scripts/set-version.sh" "$ROOT/scripts/check-version-consistency.sh" "$work/scripts/"
    printf '<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix></PropertyGroup></Project>\n' > "$work/$PROPS"
    printf '# Changelog\n\n## [Unreleased]\n\n## [1.2.3] - 2026-01-01\n' > "$work/$CHANGELOG"

    ( cd "$work" && bash scripts/set-version.sh 4.5.6 >/dev/null ) \
        || { echo "SELFTEST FAIL: a normal bump must succeed"; exit 1; }
    grep -q '<VersionPrefix>4.5.6</VersionPrefix>' "$work/$PROPS" \
        || { echo "SELFTEST FAIL: VersionPrefix not written"; exit 1; }
    grep -q '^## \[4.5.6\] - ' "$work/$CHANGELOG" \
        || { echo "SELFTEST FAIL: CHANGELOG heading not inserted"; exit 1; }
    # The new heading must be the NEWEST, or the gate reads the old one.
    [ "$(grep -m1 -o '^## \[[0-9][0-9.]*\]' "$work/$CHANGELOG")" = "## [4.5.6]" ] \
        || { echo "SELFTEST FAIL: the new heading is not the newest"; exit 1; }
    # Idempotent: re-running must not duplicate the heading.
    ( cd "$work" && bash scripts/set-version.sh 4.5.6 >/dev/null )
    [ "$(grep -c '^## \[4.5.6\]' "$work/$CHANGELOG")" -eq 1 ] \
        || { echo "SELFTEST FAIL: re-running duplicated the heading"; exit 1; }
    # The .NET default is refused even when asked for explicitly.
    if ( cd "$work" && bash scripts/set-version.sh 1.0.0 >/dev/null 2>&1 ); then
        echo "SELFTEST FAIL: 1.0.0 must be refused"; exit 1
    fi
    # Garbage is refused before anything is written.
    if ( cd "$work" && bash scripts/set-version.sh nonsense >/dev/null 2>&1 ); then
        echo "SELFTEST FAIL: a non-semver argument must be refused"; exit 1
    fi
    grep -q '<VersionPrefix>4.5.6</VersionPrefix>' "$work/$PROPS" \
        || { echo "SELFTEST FAIL: a refused bump must leave the tree alone"; exit 1; }
    echo "set-version: OK (7 checks)"
    exit 0
fi

[ $# -eq 1 ] || { echo "usage: $0 <x.y.z> | --self-test" >&2; exit 2; }
cd "$ROOT"
bump "$1"

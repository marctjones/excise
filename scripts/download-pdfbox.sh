#!/usr/bin/env bash
#
# Fetch the Apache PDFBox command-line jar so PdfBoxReferenceRenderer can act as
# a real oracle (#857).
#
# Background: Excise.Rendering/Differential ships six reference renderers, but the
# test suite only ever exercised four. PdfBoxReferenceRenderer was referenced by
# ZERO test files and pdfium only by two [Fact]s asserting on argument-string
# construction — so "six oracles" overstated the corroboration we actually had.
# PDFBox is a plain Maven artifact, so unlike pdfium_test (which needs a
# Chromium-side build) it can simply be downloaded.
#
# The jar is ~13 MB and lands in tools/vendor/ (gitignored) — a build input, not
# source.
#
# Usage:
#   ./scripts/download-pdfbox.sh          # fetch, then print the export line
#   eval "$(./scripts/download-pdfbox.sh --export)"
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

VERSION="${PDFBOX_VERSION:-3.0.3}"
VENDOR="$ROOT/tools/vendor"
JAR="$VENDOR/pdfbox-app-${VERSION}.jar"
URL="https://repo1.maven.org/maven2/org/apache/pdfbox/pdfbox-app/${VERSION}/pdfbox-app-${VERSION}.jar"

EXPORT_ONLY=0
[ "${1:-}" = "--export" ] && EXPORT_ONLY=1

say() { [ "$EXPORT_ONLY" = "1" ] || echo "$@"; }

mkdir -p "$VENDOR"

if [ -f "$JAR" ]; then
    say "✓ already present: $JAR ($(du -sh "$JAR" | cut -f1))"
else
    say "▶ fetching PDFBox $VERSION from Maven Central"
    if ! curl -fsSL "$URL" -o "$JAR"; then
        say "✗ download failed: $URL"
        rm -f "$JAR"
        exit 1
    fi
    say "✓ $JAR ($(du -sh "$JAR" | cut -f1))"
fi

# PDFBox needs a real JDK. On macOS /usr/bin/java is a stub that reports
# "Unable to locate a Java Runtime" when no JDK is installed, so `command -v
# java` succeeding proves nothing — PdfBoxReferenceRenderer therefore also
# probes the Homebrew path directly, and so do we.
JAVA=""
for candidate in "${JAVA_HOME:+$JAVA_HOME/bin/java}" /opt/homebrew/opt/openjdk/bin/java java; do
    [ -n "$candidate" ] || continue
    if "$candidate" -version >/dev/null 2>&1; then JAVA="$candidate"; break; fi
done

if [ -z "$JAVA" ]; then
    say ""
    say "⚠ no working Java runtime found. PDFBox needs a JDK:"
    say "    brew install openjdk        # macOS"
    say "    apt-get install default-jre # Debian/Ubuntu"
    say "  The jar is downloaded; set EXCISE_PDFBOX_JAR once Java is available."
else
    say "✓ java: $JAVA ($("$JAVA" -version 2>&1 | head -1))"
fi

if [ "$EXPORT_ONLY" = "1" ]; then
    echo "export EXCISE_PDFBOX_JAR='$JAR'"
    exit 0
fi

echo ""
echo "To enable the PDFBox oracle in this shell:"
echo "    export EXCISE_PDFBOX_JAR='$JAR'"
echo ""
echo "Then re-run scripts/check-test-prereqs.sh to confirm it is picked up."

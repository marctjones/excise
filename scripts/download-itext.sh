#!/usr/bin/env bash
# iText 7 + pdfSweep — jars for the competitor redaction adapter (#1121).
#
# iText pdfSweep is the best-known DEDICATED redactor and a measured width-leaker
# (PETS 2023), so it is a calibrated reference point, not an unknown. AGPL, like
# the mutool/Ghostscript/PDFBox oracles — invoked as an external tool at test
# time, never linked into a shipped binary, so check-license-compliance.sh (which
# governs DEPENDENCIES) does not apply.
#
# Fetches the runtime jar set from Maven Central into tools/vendor/itext/, which
# is gitignored. The benchmark's ItextRunnable() gate turns the adapter on only
# when these are present.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TARGET="$ROOT/tools/vendor/itext"
MAVEN="${MAVEN_BASE:-https://repo1.maven.org/maven2}"
IT="9.7.0"          # iText core (AGPL); cleanup 5.0.7 pins root 9.7.0
SWEEP="5.0.7"       # the redaction module, renamed pdfSweep -> cleanup in iText 8+
SLF4J="1.7.36"

mkdir -p "$TARGET"

# group/artifact/version -> jar
fetch() {
    local group="$1" artifact="$2" version="$3"
    local path="${group//.//}/$artifact/$version/$artifact-$version.jar"
    local dest="$TARGET/$artifact-$version.jar"
    if [ -f "$dest" ]; then echo "  have $artifact-$version.jar"; return; fi
    echo "==> $artifact-$version.jar"
    curl -fL --retry 3 --max-time 120 -o "$dest" "$MAVEN/$path"
}

for a in commons kernel io layout forms svg styled-xml-parser pdfa sign; do
    fetch com.itextpdf "$a" "$IT"
done
fetch com.itextpdf cleanup "$SWEEP"
fetch com.itextpdf bouncy-castle-connector "$IT"
fetch org.slf4j slf4j-api "$SLF4J"

echo "iText jars in $TARGET:"
ls -1 "$TARGET"/*.jar | sed 's#.*/#  #'

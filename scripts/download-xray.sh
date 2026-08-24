#!/usr/bin/env bash
# download-xray.sh — install the Free Law Project's x-ray into a local venv.
#
# x-ray detects BAD REDACTIONS: text still selectable under an opaque
# rectangle. It is an independent IMPLEMENTATION of what excise's own
# HiddenTextDetector / `excise audit` claims, which matters because a tool
# must not be its own oracle for the property it exists to guarantee (#1122).
#
# A venv, not a system install: x-ray pulls PyMuPDF, and this machine's system
# python should not acquire an AGPL PDF engine as a side effect of running
# tests. Invoked as a subprocess, never linked — the same posture LICENSES.md
# documents for mutool and Ghostscript.
#
# Output: tools/vendor/xray-venv/ (gitignored)
# Tests pick it up via EXCISE_XRAY_PYTHON, or fall back to a `python3` that
# can already `import xray`.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
VENV="$ROOT/tools/vendor/xray-venv"
PY="$VENV/bin/python"

GREEN=$'\033[32m'; YELLOW=$'\033[33m'; DIM=$'\033[2m'; RESET=$'\033[0m'

if [ "${1:-}" != "--force" ] && [ -x "$PY" ] && "$PY" -c "import xray" 2>/dev/null; then
    echo "${GREEN}✓${RESET} x-ray already installed at $VENV ${DIM}(--force to reinstall)${RESET}"
else
    command -v python3 >/dev/null 2>&1 || {
        echo "python3 not found — install it first" >&2; exit 1; }

    echo "==> creating venv at $VENV"
    mkdir -p "$(dirname "$VENV")"
    rm -rf "$VENV"
    python3 -m venv "$VENV"

    echo "==> installing x-ray"
    "$VENV/bin/pip" install --quiet --upgrade pip >/dev/null 2>&1 || true
    "$VENV/bin/pip" install --quiet x-ray

    # Verify by IMPORTING. A pip that exits 0 having installed something
    # unimportable is not an oracle, and finding that out inside a test run
    # wastes the run.
    "$PY" -c "import xray" || { echo "x-ray installed but will not import" >&2; exit 1; }
    echo "${GREEN}✓${RESET} x-ray installed"
fi

# Prove it can actually do the job, not just import. A detector that cannot
# detect is worse than an absent one: it reports every document clean.
PROBE="$(mktemp -t xray-probe).pdf"
trap 'rm -f "$PROBE"' EXIT
"$PY" - "$PROBE" <<'PYEOF'
import sys
# A deliberately FAKE redaction: text, then an opaque box painted over it.
body = (b"BT /F1 24 Tf 72 700 Td (Name: Louise Anne Farrar) Tj ET\n"
        b"0 0 0 rg\n137 694 232 26 re f\n")
objs = [b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        b"<< /Length " + str(len(body)).encode() + b" >>\nstream\n" + body + b"endstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"]
out = bytearray(b"%PDF-1.7\n"); offs = []
for i, o in enumerate(objs, 1):
    offs.append(len(out)); out += str(i).encode() + b" 0 obj\n" + o + b"\nendobj\n"
x = len(out)
out += b"xref\n0 " + str(len(objs) + 1).encode() + b"\n0000000000 65535 f \n"
for o in offs: out += ("%010d 00000 n \n" % o).encode()
out += (b"trailer\n<< /Size " + str(len(objs) + 1).encode() + b" /Root 1 0 R >>\nstartxref\n"
        + str(x).encode() + b"\n%%EOF\n")
open(sys.argv[1], "wb").write(bytes(out))
PYEOF

if "$PY" -c "import sys,xray; sys.exit(0 if xray.inspect(sys.argv[1]) else 1)" "$PROBE"; then
    echo "${GREEN}✓${RESET} x-ray detects a known-bad redaction (self-check passed)"
else
    echo "${YELLOW}⚠${RESET} x-ray installed but did NOT flag a deliberately fake redaction." >&2
    echo "  Treat it as unavailable rather than trusting a clean verdict from it." >&2
    exit 1
fi

echo
echo "Point the tests at it:"
echo "  ${DIM}export EXCISE_XRAY_PYTHON=$PY${RESET}"

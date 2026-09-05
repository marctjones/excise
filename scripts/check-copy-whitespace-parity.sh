#!/usr/bin/env bash
# Copy-whitespace parity gate (#837).
#
# Runs CopyWhitespaceParityHarness (whole GUI copy path:
# SortReadingOrder(ColumnAware) -> JoinText(Smart)) over the corpus and compares
# per-document word/line agreement against poppler `pdftotext`, failing when a
# score drops below its checked-in floor in tests/copy-whitespace/floors.json.
# Turns copy-spacing quality (#833/#836) from anecdote into a non-regressing
# measurement, the same posture as scripts/check-extraction-parity.sh.
#
# As of #929/#844, rendering-linux.yml (poppler pdftotext + the federal
# corpus, called from ci.yml on every push/PR) runs this script directly with
# EXCISE_REQUIRE_PARITY_TOOLS=1 — same escalation from release-only to
# effectively PR-blocking as check-extraction-parity.sh, see that script's
# header for the full reasoning.
#
# Usage:
#   scripts/check-copy-whitespace-parity.sh            # gate: fail on regression
#   scripts/check-copy-whitespace-parity.sh --update    # ratchet floors from now
set -euo pipefail
cd "$(dirname "$0")/.."

# #841: strict mode. On a maintainer/local box this gate SKIPs cleanly when
# poppler or the corpus is absent (exit 0) — the same convenience the
# extraction-parity and encryption-interop gates offer. But on CI and in the
# release-evidence path a SKIP is exactly the bug: the gate silently measures
# nothing and the ratchet goes green vacuously. Set EXCISE_REQUIRE_PARITY_TOOLS=1
# there (mirrors EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS) to turn every skip
# reason into a hard FAIL, so a tool-less runner cannot green this gate.
REQUIRE="${EXCISE_REQUIRE_PARITY_TOOLS:-0}"
miss() { # miss <message>
  if [ "$REQUIRE" = "1" ]; then
    echo "FAIL: $1"
    echo "      EXCISE_REQUIRE_PARITY_TOOLS=1 refuses to silently skip — that is the bug it exists to fix."
    exit 1
  fi
  echo "SKIP: $1"
  exit 77   # prerequisite missing (LOCAL_GATES.md): the runner shows a SKIPPED row, never a green
}

if ! command -v pdftotext >/dev/null 2>&1; then
  miss "pdftotext (poppler) not on PATH — cannot run the copy-whitespace parity gate. brew install poppler / apt-get install poppler-utils"
fi
if [ ! -d test-pdfs ]; then
  miss "test-pdfs/ corpus absent — run scripts/setup-local-real-world-corpus.sh + scripts/download-federal-corpus.sh."
fi

# In strict mode, at minimum the CI-fetchable corpus must be present — a partial
# corpus would let the ratchet go green having measured nothing. The harness
# skips missing files individually (convenience-first); this enforces that the
# documents CI can actually obtain are all there.
#
# The parity harness measures 5 documents. Only the 3 FEDERAL ones are
# fetchable on a fresh runner (official .gov URLs, download-federal-corpus.sh —
# US-government works, public domain per 17 USC §105). The 2 local-real-world
# books (producingoss.pdf, foss-primer.pdf) are redistribution-restricted local
# copies (test-pdfs/manifests/local-real-world-books.json — "do not redistribute
# from the repository"); they are measured when a maintainer box has them but
# CANNOT be required on CI. The floor gate only checks documents it actually
# measured, so their absence removes coverage but never greens a regression.
REQUIRED_CORPUS=(
  test-pdfs/federal/scotus-trump-v-us.pdf
  test-pdfs/federal/irs-pub509-2026.pdf
  test-pdfs/federal/cdc-vis-covid-19.pdf
  test-pdfs/federal/state-ds82-passport-renewal.pdf
)
for f in "${REQUIRED_CORPUS[@]}"; do
  [ -f "$f" ] || miss "parity corpus incomplete: missing $f (fetch: scripts/download-federal-corpus.sh)."
done

export COPY_WHITESPACE_PARITY=1
if [ "${1:-}" = "--update" ]; then
  export COPY_WHITESPACE_PARITY_UPDATE=1
  echo "Ratcheting copy-whitespace parity floors from the current measurement…"
fi

# `dotnet test --filter` EXITS 0 WHEN IT MATCHES NOTHING, so renaming or moving
# CopyWhitespaceParityHarness would leave this gate green having measured
# nothing — precisely the vacuous pass the strict-mode guard above exists to
# prevent, arriving through a different door (#941). check-extraction-parity.sh
# had the same hole and it was reachable.
# `tee`, not capture-and-echo: the harness runs the whole GUI copy path over the
# corpus. A gate that prints nothing while it works looks hung.
RUN_LOG="$(mktemp)"
trap 'rm -f "$RUN_LOG"' EXIT
set +e
dotnet test Excise.Avalonia.Tests/Excise.Avalonia.Tests.csproj \
  -c Debug --filter "FullyQualifiedName~CopyWhitespaceParityHarness" \
  --logger "console;verbosity=minimal" 2>&1 | tee "$RUN_LOG"
run_status=${PIPESTATUS[0]}
set -e
run_output="$(cat "$RUN_LOG")"

if grep -q "No test matches the given testcase filter" <<<"$run_output"; then
  echo
  echo "FAIL: the filter matched NO tests — CopyWhitespaceParityHarness was renamed,"
  echo "      moved, or removed. dotnet test exits 0 in that case, so this would"
  echo "      otherwise be a green gate that measured nothing."
  exit 1
fi

exit $run_status

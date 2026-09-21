#!/usr/bin/env bash
# End-to-end Linux printing for excise, in a local podman container (#1710).
#
# WHAT IT PROVES. LinuxCupsDocumentPrinter submits a real job to a real cupsd
# through real lp/lpstat subprocesses, and the PDF the queue produces has the
# page count excise asked for — counted by qpdf or mutool, never by excise.
# The unit tests (Excise.App.Tests, LinuxCupsDocumentPrinterTests) pin the
# command line and every failure branch with a fake runner; they cannot tell
# you CUPS accepts that command line. This can.
#
# WHY NOT A GATE ROW. It needs podman, a container build and a self-contained
# publish, which is minutes, not seconds — the same reason
# scripts/run-gui-perf-linux.sh is tooling rather than a row in
# tests/gates.tsv. The part that IS gated is everything short of the
# scheduler: the unit tests run in Excise.App.Tests on every platform, in t1.
#
#   scripts/run-linux-print-test.sh                 # build, run, report
#   scripts/run-linux-print-test.sh --demo-failure  # prove this gate can go RED
#   scripts/run-linux-print-test.sh --no-publish    # reuse the last publish
#   scripts/run-linux-print-test.sh --keep-image    # do not remove the image
#   scripts/run-linux-print-test.sh --shell         # poke around inside
#
# ⚠️ The image store is shared with other sessions. This script removes only
# the image it builds, by name, and never prunes.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE="excise-linux-print:latest"
CPUS="${EXCISE_LINUX_PRINT_CPUS:-4}"
MEMORY="${EXCISE_LINUX_PRINT_MEMORY:-6g}"
QUEUE="ExcisePDF"
SKIP_PUBLISH=0
KEEP_IMAGE=0
SHELL_MODE=0
STOP_MACHINE=0

case "$(uname -m)" in
  arm64|aarch64) RID="linux-arm64" ;;
  *)             RID="linux-x64" ;;
esac

while [ $# -gt 0 ]; do
  case "$1" in
    --demo-failure) QUEUE="excise-queue-that-does-not-exist"; shift ;;
    --no-publish)   SKIP_PUBLISH=1; shift ;;
    --keep-image)   KEEP_IMAGE=1; shift ;;
    --shell)        SHELL_MODE=1; shift ;;
    --rid)          RID="$2"; shift 2 ;;
    --cpus)         CPUS="$2"; shift 2 ;;
    --memory)       MEMORY="$2"; shift 2 ;;
    --stop-machine) STOP_MACHINE=1; shift ;;
    -h|--help)      sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

command -v podman >/dev/null 2>&1 || { echo "FAIL: podman is not installed" >&2; exit 77; }

# ── publish the tests, self-contained (the slow step) ───────────────────
PUB="$ROOT/artifacts/linux-print/$RID"
mkdir -p "$ROOT/artifacts/linux-print"
if [ "$SKIP_PUBLISH" -eq 0 ]; then
  echo "==> publishing Excise.App.Tests self-contained for $RID"
  rm -rf "$PUB"
  dotnet publish "$ROOT/Excise.App.Tests/Excise.App.Tests.csproj" \
      -c Debug -r "$RID" --self-contained true -p:PublishSingleFile=false \
      -o "$PUB" >"$ROOT/artifacts/linux-print/publish.log" 2>&1 \
    || { echo "FAIL: publish failed; see artifacts/linux-print/publish.log" >&2
         tail -25 "$ROOT/artifacts/linux-print/publish.log" >&2; exit 1; }
  echo "    published $(du -sh "$PUB" | cut -f1)"
fi
[ -x "$PUB/Excise.App.Tests" ] || {
  echo "FAIL: no test executable at $PUB/Excise.App.Tests (drop --no-publish)" >&2; exit 1; }

# ── the podman machine ──────────────────────────────────────────────────
if ! podman machine inspect --format '{{.State}}' 2>/dev/null | grep -q running; then
  echo "==> starting the podman machine"
  podman machine start || { echo "FAIL: could not start the podman machine" >&2; exit 1; }
fi

echo "==> building $IMAGE"
podman build -t "$IMAGE" -f "$ROOT/containers/linux-print/Containerfile" \
    "$ROOT/containers/linux-print" >"$ROOT/artifacts/linux-print/build.log" 2>&1 \
  || { echo "FAIL: image build failed" >&2; tail -25 "$ROOT/artifacts/linux-print/build.log" >&2; exit 1; }

STAMP="$(date +%Y%m%d_%H%M%S)"
OUT="$ROOT/logs/linux-print_${STAMP}"
mkdir -p "$OUT"

# :ro on the published tests — the run must not modify what it measures.
declare -a MOUNTS=(
  -v "$PUB:/opt/tests:ro"
  -v "$ROOT/containers/linux-print:/opt/runner:ro"
  -v "$OUT:/out:rw"
)

cleanup_image() {
  if [ "$KEEP_IMAGE" -eq 0 ]; then
    podman rmi "$IMAGE" >/dev/null 2>&1 && echo "==> removed $IMAGE"
  fi
  if [ "$STOP_MACHINE" -eq 1 ]; then
    podman machine stop >/dev/null 2>&1 && echo "==> stopped the podman machine"
  fi
}

if [ "$SHELL_MODE" -eq 1 ]; then
  podman run --rm -it --cpus "$CPUS" --memory "$MEMORY" "${MOUNTS[@]}" "$IMAGE"
  cleanup_image
  exit 0
fi

echo "==> running the CUPS integration tests (queue=$QUEUE); output -> $OUT"
podman run --rm --cpus "$CPUS" --memory "$MEMORY" "${MOUNTS[@]}" \
  -e EXCISE_CUPS_TEST_QUEUE="$QUEUE" \
  "$IMAGE" /opt/runner/run-print-test.sh 2>&1 | tee "$OUT/run.log"
cleanup_image

# ── the verdict comes from the XML, not from an exit code ───────────────
RESULTS="$OUT/results.xml"
if [ ! -s "$RESULTS" ]; then
  echo "FAIL: the container produced no $RESULTS — see $OUT/run.log" >&2
  exit 1
fi

read -r TOTAL PASSED FAILED SKIPPED < <(
  python3 - "$RESULTS" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
total = passed = failed = skipped = 0
for assembly in root.iter('assembly'):
    total   += int(assembly.get('total') or 0)
    passed  += int(assembly.get('passed') or 0)
    failed  += int(assembly.get('failed') or 0)
    skipped += int(assembly.get('skipped') or 0)
print(total, passed, failed, skipped)
PY
)

echo "==> $PASSED passed, $FAILED failed, $SKIPPED skipped of $TOTAL ($RESULTS)"
if [ "${FAILED:-1}" -ne 0 ]; then
  python3 - "$RESULTS" <<'PY'
import sys, xml.etree.ElementTree as ET
for test in ET.parse(sys.argv[1]).getroot().iter('test'):
    if test.get('result') == 'Fail':
        message = test.find('./failure/message')
        print(f"  FAILED {test.get('name')}: {(message.text or '').strip().splitlines()[0] if message is not None and message.text else ''}")
PY
  exit 1
fi
if [ "${PASSED:-0}" -eq 0 ]; then
  # A run that executed nothing is a failure, not a pass — the same rule
  # scripts/lib-runner.sh applies to a --filter that matches no tests.
  echo "FAIL: zero tests ran; a vacuous green is not a green" >&2
  exit 1
fi
if [ "${SKIPPED:-0}" -ne 0 ]; then
  echo "FAIL: $SKIPPED test(s) skipped INSIDE the container — CUPS was supposed to be present" >&2
  exit 1
fi
echo "==> Linux printing verified end to end against a real cupsd"

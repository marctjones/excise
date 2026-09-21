#!/usr/bin/env bash
# Linux GUI performance measurement in a local podman container (#1720).
#
# WHY LOCAL AND NOT THE HOSTED RUNNER. On hosted runners the machine class
# varies, so CPU and wall-clock are not gateable there — only a within-run
# memory ratio is (#1699) — and each iteration costs a push. #1712 reproduced
# on hosted macOS and Linux but not in a local arm64 container, which is the
# kind of question only a local container answers cheaply.
#
# ⚠️ These numbers are a LINUX-OVER-TIME series. They are not comparable to the
# macOS ones: /proc VmRSS is not macOS `footprint`, the allocator and kernel
# differ, and Xvfb has no compositor. Reading a Linux row against a macOS row
# is how a platform difference gets misreported as a regression.
#
#   scripts/run-gui-perf-linux.sh                          # altona-scroll, 3 reps
#   scripts/run-gui-perf-linux.sh --scenario w9-launch-idle --repeats 5
#   scripts/run-gui-perf-linux.sh --cpus 4 --memory 8g
#   scripts/run-gui-perf-linux.sh --shell                  # poke around inside
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCENARIO="altona-scroll"
REPEATS=3
CPUS="${EXCISE_LINUX_PERF_CPUS:-4}"
MEMORY="${EXCISE_LINUX_PERF_MEMORY:-8g}"
RID="linux-arm64"
IMAGE="excise-linux-perf:latest"
SHELL_MODE=0
SKIP_PUBLISH=0

while [ $# -gt 0 ]; do
  case "$1" in
    --scenario) SCENARIO="$2"; shift 2 ;;
    --repeats)  REPEATS="$2"; shift 2 ;;
    --cpus)     CPUS="$2"; shift 2 ;;
    --memory)   MEMORY="$2"; shift 2 ;;
    --rid)      RID="$2"; shift 2 ;;
    --shell)    SHELL_MODE=1; shift ;;
    --no-publish) SKIP_PUBLISH=1; shift ;;
    -h|--help)  sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

command -v podman >/dev/null 2>&1 || { echo "FAIL: podman is not installed" >&2; exit 1; }

# The podman machine is a VM with its own CPU and memory; the container limits
# below are bounded by it. Report both, because a container "limited" to 8 GiB
# inside a 16 GiB VM is a different experiment from one on bare metal.
if ! podman machine inspect --format '{{.State}}' 2>/dev/null | grep -q running; then
  echo "==> starting the podman machine"
  podman machine start || { echo "FAIL: could not start the podman machine" >&2; exit 1; }
fi
VM_CPUS=$(podman machine inspect --format '{{.Resources.CPUs}}' 2>/dev/null | head -1)
VM_MEM=$(podman machine inspect --format '{{.Resources.Memory}}' 2>/dev/null | head -1)
echo "==> podman VM: ${VM_CPUS:-?} cpus, ${VM_MEM:-?} MiB; container: $CPUS cpus, $MEMORY"

PUB="$ROOT/artifacts/linux-perf/$RID"
if [ "$SKIP_PUBLISH" -eq 0 ]; then
  echo "==> publishing Excise.App self-contained for $RID (this is the slow step)"
  rm -rf "$PUB"
  dotnet publish "$ROOT/Excise.App/Excise.App.csproj" \
      -c Release -r "$RID" --self-contained true \
      -p:PublishSingleFile=false \
      -o "$PUB" >"$ROOT/artifacts/linux-perf/publish.log" 2>&1 \
    || { echo "FAIL: publish failed; see artifacts/linux-perf/publish.log" >&2; tail -20 "$ROOT/artifacts/linux-perf/publish.log" >&2; exit 1; }
  [ -x "$PUB/Excise.App" ] || { echo "FAIL: published, but no executable at $PUB/Excise.App" >&2; exit 1; }
  echo "    published $(du -sh "$PUB" | cut -f1)"
fi
[ -x "$PUB/Excise.App" ] || { echo "FAIL: nothing published at $PUB (drop --no-publish)" >&2; exit 1; }

echo "==> building $IMAGE"
podman build -t "$IMAGE" -f "$ROOT/containers/linux-perf/Containerfile" "$ROOT/containers/linux-perf" \
  >"$ROOT/artifacts/linux-perf/build.log" 2>&1 \
  || { echo "FAIL: image build failed" >&2; tail -20 "$ROOT/artifacts/linux-perf/build.log" >&2; exit 1; }

STAMP="$(date +%Y%m%d_%H%M%S)"
OUT="$ROOT/logs/gui-perf-linux_${STAMP}"
mkdir -p "$OUT"

# :ro on the app and the repo — a measurement run must not be able to modify
# the thing it is measuring. Only $OUT is writable.
declare -a MOUNTS=(
  -v "$PUB:/opt/excise:ro"
  -v "$ROOT/tests:/repo/tests:ro"
  -v "$ROOT/test-pdfs:/repo/test-pdfs:ro"
  -v "$ROOT/containers/linux-perf:/opt/runner:ro"
  -v "$OUT:/out:rw"
)

if [ "$SHELL_MODE" -eq 1 ]; then
  exec podman run --rm -it --cpus "$CPUS" --memory "$MEMORY" "${MOUNTS[@]}" "$IMAGE"
fi

echo "==> running $SCENARIO x$REPEATS; output -> $OUT"
podman run --rm --cpus "$CPUS" --memory "$MEMORY" "${MOUNTS[@]}" \
  -e SCENARIO="$SCENARIO" -e REPEATS="$REPEATS" \
  "$IMAGE" /opt/runner/run-scenarios.sh 2>&1 | tee "$OUT/run.log"
status=${PIPESTATUS[0]}

if [ ! -s "$OUT/run.json" ]; then
  echo "FAIL: no run.json — the container produced no measurement. See $OUT/run.log" >&2
  exit 1
fi
echo "==> $OUT/run.json"
exit "$status"

#!/usr/bin/env bash
# In-container half of the #1710 Linux print gate. Runs as root inside the
# image built from this directory's Containerfile; scripts/run-linux-print-test.sh
# is the half that runs on the host.
#
# It does four things and then gets out of the way:
#   1. point cups-pdf's output at a known directory (never guess ${HOME}/PDF),
#   2. start cupsd WITHOUT systemd — there is no init in a container,
#   3. create a queue with lpadmin, because 24.04 no longer creates one,
#   4. run Excise.App.Tests's CUPS integration class as a NON-root user and
#      write its xUnit XML to /out/results.xml.
#
# ⚠️ The exit code of the test executable is NOT the verdict. The host script
# reads /out/results.xml, because an Avalonia-referencing host can exit
# non-zero on native teardown AFTER every test passed (see the
# scripts/assert-trx-green.sh entry in tests/gates-tooling.txt).
set -uo pipefail

QUEUE="${EXCISE_CUPS_TEST_QUEUE:-ExcisePDF}"
OUT_PDF="/out/pdf"
RESULTS="/out/results.xml"
TESTS="${EXCISE_TEST_BINARY:-/opt/tests/Excise.App.Tests}"
TEST_CLASS="Excise.App.Tests.UI.LinuxCupsPrintIntegrationTests"

say() { echo "==> $*"; }

[ -x "$TESTS" ] || { echo "FAIL: no test executable at $TESTS" >&2; exit 1; }

# ── 1. cups-pdf output directory ────────────────────────────────────────
mkdir -p "$OUT_PDF"
chmod 1777 "$OUT_PDF"

CONF=/etc/cups/cups-pdf.conf
[ -f "$CONF" ] || { echo "FAIL: $CONF is missing; is cups-pdf installed?" >&2; exit 1; }
# Replace whatever Out/AnonDirName say, commented or not, with our path. Both
# matter: a job whose owner cups-pdf cannot resolve lands in AnonDirName.
sed -i -E "s|^#?[[:space:]]*Out[[:space:]].*|Out ${OUT_PDF}|" "$CONF"
sed -i -E "s|^#?[[:space:]]*AnonDirName[[:space:]].*|AnonDirName ${OUT_PDF}|" "$CONF"
grep -qE "^Out ${OUT_PDF}$" "$CONF" || echo "Out ${OUT_PDF}" >> "$CONF"
grep -qE "^AnonDirName ${OUT_PDF}$" "$CONF" || echo "AnonDirName ${OUT_PDF}" >> "$CONF"
say "cups-pdf output: $(grep -E '^(Out|AnonDirName) ' "$CONF" | tr '\n' ' ')"

# ── 2. cupsd, no systemd ────────────────────────────────────────────────
say "starting cupsd"
/usr/sbin/cupsd
for _ in $(seq 1 50); do
  lpstat -r 2>/dev/null | grep -q "scheduler is running" && break
  sleep 0.2
done
if ! lpstat -r 2>/dev/null | grep -q "scheduler is running"; then
  echo "FAIL: cupsd did not come up" >&2
  lpstat -r; exit 1
fi
say "$(lpstat -r)"

# ── 3. a queue, created by hand ─────────────────────────────────────────
# Discover the model rather than hard-coding a PPD name: the string differs
# between cups-pdf packagings, and a wrong one fails lpadmin outright.
MODEL="$(lpinfo -m 2>/dev/null | grep -i 'cups-pdf' | head -1 | awk '{print $1}')"
if [ -n "$MODEL" ]; then
  say "queue $QUEUE from model $MODEL"
  lpadmin -p "$QUEUE" -v cups-pdf:/ -m "$MODEL" -E -o printer-is-shared=false
elif [ -f /usr/share/ppd/cups-pdf/CUPS-PDF_noopt.ppd ]; then
  say "queue $QUEUE from the shipped CUPS-PDF_noopt.ppd (lpinfo listed no cups-pdf model)"
  lpadmin -p "$QUEUE" -v cups-pdf:/ -P /usr/share/ppd/cups-pdf/CUPS-PDF_noopt.ppd -E -o printer-is-shared=false
else
  echo "FAIL: no cups-pdf model and no shipped PPD; cannot create a queue" >&2
  lpinfo -m 2>&1 | head -20 >&2
  exit 1
fi
lpadmin -d "$QUEUE"
cupsaccept "$QUEUE"
cupsenable "$QUEUE"
say "$(lpstat -p "$QUEUE" 2>&1 | head -1)  /  $(lpstat -d)"

# ── 4. the tests, as a non-root user ────────────────────────────────────
# EXCISE_CUPS_TEST_QUEUE may name a queue that does NOT exist: that is the
# --demo-failure mode, which proves this gate can go red.
say "running $TEST_CLASS as uid 1000, queue=$QUEUE out=$OUT_PDF"
setpriv --reuid=1000 --regid=1000 --init-groups \
  env HOME=/home/excise \
      EXCISE_CUPS_TEST_QUEUE="$QUEUE" \
      EXCISE_CUPS_TEST_OUTPUT_DIR="$OUT_PDF" \
      DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  "$TESTS" -class "$TEST_CLASS" -xml "$RESULTS" -parallel none -noColor
status=$?
say "test executable exited $status (the verdict is $RESULTS, not this number)"

echo "--- $OUT_PDF ---"
ls -l "$OUT_PDF" 2>/dev/null || true
echo "--- cups job history ---"
lpstat -W all -o 2>/dev/null | head -20 || true

[ -s "$RESULTS" ] || { echo "FAIL: no $RESULTS was written" >&2; exit 1; }
exit 0

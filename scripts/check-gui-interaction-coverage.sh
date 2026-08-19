#!/usr/bin/env bash
#
# GUI interaction coverage (#1021 follow-up).
#
# ── The question this answers ────────────────────────────────────────────────
#
# "How much of the GUI can a user reach that no automated MOUSE OR KEYBOARD
# interaction ever touches?"
#
# Not "how many commands do we call". Excise.App.Tests already reaches 61
# commands through GuiClickSafetySweepTests, which invokes Command.Execute(...)
# on each one. That is worth having — it is what proves no menu item explodes —
# but it is not a click. Executing a command cannot fail when the control is
# collapsed, sized zero, covered by another element, disabled by a binding that
# silently evaluates false, or bound to nothing at all (a null Command is
# skipped by that sweep without a word). Those are precisely the defects that
# only a real pointer or key event can catch, so they are counted apart.
#
# ── How the number is produced ───────────────────────────────────────────────
#
#   denominator  artifacts/gui-coverage/gui-interaction-inventory.tsv
#                every interactive affordance enumerated from a REAL MainWindow
#                (GuiInteractionCoverageTests), keyed by the ViewModel command
#                property name where there is one.
#
#   numerator    artifacts/gui-coverage/gui-interaction-observed.tsv
#                every element that actually received a synthetic pointer/key
#                event during the run, recorded as the events were raised
#                (GuiInteractionRecorder, installed for the whole assembly).
#                An element cannot enter this file without a test having
#                genuinely raised input at it — it is measured, not declared.
#
#   context      artifacts/gui-coverage/gui-command-executed.tsv
#                what command execution reached, used only to split the gap
#                list into B (command-covered) and C (nothing at all).
#
#   expectations tests/gui-interaction-coverage.tsv
#                every element with no interactive automation, classified B or C
#                and carrying a note. Same semantics as tests/skip-allowlist: a
#                gap must be declared on purpose, and a declared gap that stops
#                being a gap must be removed rather than left to hide the next
#                one. --update writes a default note per class; the C entries are
#                few enough to be worth replacing with a real reason, the B ones
#                mostly are not.
#
# ── Why this is a script and not an xunit assertion ──────────────────────────
#
# The numerator is only complete after the WHOLE project has run. An in-assembly
# ratio assertion would fail on every --filter run and every chunk of a chunked
# run, and a gate that always fails locally is a gate people stop reading —
# check-skip-budget.sh carries that lesson already.
#
# Usage:
#   scripts/check-gui-interaction-coverage.sh [--update]
#
#   --update   rewrite the expectations file from the current run. Review the
#              diff: every line it adds is coverage you do not have.
#
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="$REPO/artifacts/gui-coverage"
EXPECT="$REPO/tests/gui-interaction-coverage.tsv"
UPDATE="${1:-}"

INVENTORY="$ART/gui-interaction-inventory.tsv"
OBSERVED="$ART/gui-interaction-observed.tsv"
EXECUTED="$ART/gui-command-executed.tsv"

# A floor on the DENOMINATOR. An enumerator that finds twelve elements would
# report excellent coverage instead of reading as a broken instrument.
MIN_INVENTORY=120

fail() { echo "❌ $*" >&2; exit 1; }

# ── Artifacts must exist and be non-vacuous ──────────────────────────────────
#
# A missing artifact is an ERROR, never a skip. An empty poll result reading as
# "not ready" has already cost this project an hour of watching a red CI run
# report as queued.
[[ -f "$INVENTORY" ]] || fail "no inventory at $INVENTORY
   Run the whole project first: dotnet test Excise.App.Tests
   (a --filter run does not produce a complete numerator)."
[[ -f "$OBSERVED" ]] || fail "no observations at $OBSERVED
   The recorder produced nothing, which means either no GUI test ran or
   GuiInteractionRecorder.Install() is no longer wired into TestAppBuilder."

inventory_count=$(sort -u "$INVENTORY" | grep -c . || true)
(( inventory_count >= MIN_INVENTORY )) || fail \
  "only $inventory_count elements enumerated (floor $MIN_INVENTORY) — the enumerator is broken.
   A small denominator reads as high coverage, which is why this floor exists."

# Observed lines are "<id>\t<modality>"; collapse to ids.
cut -f1 "$OBSERVED" | sort -u > "$ART/.observed-ids"
observed_count=$(grep -c . "$ART/.observed-ids" || true)
(( observed_count > 0 )) || fail "the recorder observed no input events at all."

sort -u "$INVENTORY" > "$ART/.inventory-ids"

# ── Covered / uncovered ──────────────────────────────────────────────────────
comm -12 "$ART/.inventory-ids" "$ART/.observed-ids" > "$ART/.covered"
comm -23 "$ART/.inventory-ids" "$ART/.observed-ids" > "$ART/.uncovered"

covered=$(grep -c . "$ART/.covered" || true)
uncovered=$(grep -c . "$ART/.uncovered" || true)
pct=$(awk -v c="$covered" -v t="$inventory_count" 'BEGIN{printf "%.1f", (t?100*c/t:0)}')

# Elements the recorder saw that are NOT in the inventory are item-template
# affordances materialised only with a document loaded — thumbnails, search
# result rows, outline nodes. They are real coverage; they just have no stable
# static identity, so they are reported, not failed on.
extra=$(comm -13 "$ART/.inventory-ids" "$ART/.observed-ids" | grep -c . || true)

# ── B vs C: is an uncovered element at least reached by command execution? ───
: > "$ART/.gap-b"
: > "$ART/.gap-c"
while IFS= read -r id; do
  [[ -n "$id" ]] || continue
  name="${id##*:}"
  if [[ -f "$EXECUTED" ]] && grep -qxF "$name" "$EXECUTED"; then
    echo "$id" >> "$ART/.gap-b"
  else
    echo "$id" >> "$ART/.gap-c"
  fi
done < "$ART/.uncovered"

gap_b=$(grep -c . "$ART/.gap-b" || true)
gap_c=$(grep -c . "$ART/.gap-c" || true)

echo "GUI interaction coverage (real pointer/key events only)"
echo "  interactive elements enumerated : $inventory_count"
echo "  driven by synthetic input       : $covered  (${pct}%)"
echo "  gaps, command-executed only (B) : $gap_b"
echo "  gaps, no automation at all  (C) : $gap_c"
echo "  observed outside the inventory  : $extra (item-template affordances)"
echo

# ── --update ─────────────────────────────────────────────────────────────────
if [[ "$UPDATE" == "--update" ]]; then
  # Preserve every existing note; a rewrite that discarded them would turn a
  # reviewed gap list back into an anonymous one.
  {
    echo "# GUI elements with no automated mouse/keyboard interaction."
    echo "# Generated by scripts/check-gui-interaction-coverage.sh --update; notes are hand-written."
    echo "# Column 2 is the gap class: B = reached by Command.Execute in GuiClickSafetySweepTests,"
    echo "# C = no automation of any kind. Removing a line is how coverage is claimed back."
    while IFS= read -r id; do
      [[ -n "$id" ]] || continue
      note=""
      [[ -f "$EXPECT" ]] && note=$(awk -F'\t' -v k="$id" '$1==k{print $3}' "$EXPECT" | head -1)
      cls="C"; grep -qxF "$id" "$ART/.gap-b" && cls="B"
      if [[ -z "$note" ]]; then
        if [[ "$cls" == "B" ]]; then
          note="reached only by Command.Execute in GuiClickSafetySweepTests; no pointer/key path"
        else
          note="no automation of any kind — needs a real-input test or a reviewed reason"
        fi
      fi
      printf '%s\t%s\t%s\n' "$id" "$cls" "${note:-}"
    done < "$ART/.uncovered"
  } > "$EXPECT.new"
  mv "$EXPECT.new" "$EXPECT"
  echo "wrote $EXPECT ($uncovered gaps) — review the diff, every line is coverage you do not have."
  exit 0
fi

[[ -f "$EXPECT" ]] || fail "no expectations file at $EXPECT
   Create it with: scripts/check-gui-interaction-coverage.sh --update"

grep -v '^#' "$EXPECT" | cut -f1 | grep . | sort -u > "$ART/.declared-gaps"

# ── FORWARD: a new element with no interaction and no declared gap ───────────
# Never relaxed. This is the whole point: adding a button to the toolbar and
# never clicking it in a test must not pass silently.
comm -23 "$ART/.uncovered" "$ART/.declared-gaps" > "$ART/.undeclared"
undeclared=$(grep -c . "$ART/.undeclared" || true)

# ── REVERSE: a declared gap that is now covered ──────────────────────────────
# Coverage coming back must be recorded, or the file slowly becomes a blanket
# excuse — the same failure the skip allowlist's reverse check exists to stop.
comm -12 "$ART/.declared-gaps" "$ART/.covered" > "$ART/.stale"
stale=$(grep -c . "$ART/.stale" || true)

status=0
if (( undeclared > 0 )); then
  echo "❌ $undeclared interactive element(s) have no mouse/keyboard automation and no declared gap:" >&2
  sed 's/^/     /' "$ART/.undeclared" >&2
  echo "   Add an interactive test, or declare the gap with a note:" >&2
  echo "     scripts/check-gui-interaction-coverage.sh --update" >&2
  status=1
fi
if (( stale > 0 )); then
  echo "❌ $stale declared gap(s) are now covered — remove them so the list keeps meaning something:" >&2
  sed 's/^/     /' "$ART/.stale" >&2
  status=1
fi

if (( status == 0 )); then
  echo "✅ every interactive element is either driven by real input or a declared gap."
fi
exit $status

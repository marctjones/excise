#!/usr/bin/env bash
# The PDF-spec capability registry gate (#1357, #1366).
#
#   (default)               t0 BLOCK — STRUCTURE. Regenerates every derived file
#                           from COMMITTED inputs and diffs against the tree. The
#                           test-outcomes snapshot is READ here, never regenerated:
#                           until #1366 it was rebuilt from every trx under logs/,
#                           so a full run's own results reddened the registry row
#                           in the same run and every t0 after it.
#   --refresh-outcomes DIR  full GRADE — EVIDENCE. Imports the trx of every test
#                           row in DIR/ledger.jsonl (a resumed run's checkpointed
#                           rows included, from their evidence directory), rebuilds
#                           what depends on them, prints the delta against the
#                           committed snapshot, stashes the regenerated files under
#                           DIR/registry-outcomes/ and restores the tree on EVERY
#                           exit path. Exit 0; exit 77 (SKIPPED under
#                           prereqPolicy=skip) when the ledger names no trx, or
#                           when the generated files are already modified — the
#                           t0 row left structural drift for review, or an adoption
#                           awaits its commit — because importing over either would
#                           bundle it into "test evidence".
#   --adopt DIR             copies DIR/registry-outcomes/* (minus the import summary)
#                           into the tree for review and commit — that is how the
#                           snapshot moves. The stash is pruned with the run dir.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
GEN=test-pdfs/manifests/pdf-spec-registry/generated
IMPORT_SUMMARY=import-summary.json

# recordedAt and gitRevision are provenance stamped at generation time, so
# they differ on EVERY run by construction (#1357). Diffing them made this
# gate unconditionally red: two consecutive runs on a clean tree both failed,
# on the timestamp the gate itself had just written. Ignore those lines and
# compare the evidence, which is what the gate is for.
DIFF_IGNORE=(-I '"recordedAt":' -I '"gitRevision":')
GENERATED=(
  $GEN/summary.json
  $GEN/capability-scorecard.json
  $GEN/capability-scorecard.md
  $GEN/verification-gaps.json
  $GEN/corpus-governance.json
  $GEN/evidence-collection.json
  $GEN/renderer-test-evidence-map.json
  $GEN/renderer-promotion-queue.json
  $GEN/evidence-deficiency-report.json
  $GEN/test-outcomes.json
  $GEN/reference-tool-evidence.json
  $GEN/atomic-fixture-evidence.json
  $GEN/evidence-attribution.json
  $GEN/feature-cluster-scorecard.json
  $GEN/implementation-evidence-map.json
  $GEN/test-suite-evidence-map.json
)

# Every builder except the outcomes import, in the order the gate always ran them.
build_derived() {
  python3 scripts/check-pdf-spec-registry.py --write-summary --write-verification-gaps "$GEN/verification-gaps.json"
  python3 scripts/build-pdf-corpus-governance.py
  python3 scripts/collect-pdf-capability-evidence.py
  python3 scripts/build-renderer-test-evidence-map.py
  python3 scripts/build-pdf-evidence-maps.py
  python3 scripts/build-renderer-promotion-queue.py
  python3 scripts/build-pdf-evidence-deficiency-report.py
  python3 scripts/collect-pdf-reference-tool-evidence.py
  python3 scripts/build-pdf-atomic-fixture-map.py
  python3 scripts/build-pdf-evidence-attribution.py
  python3 scripts/build-pdf-feature-cluster-scorecard.py
  python3 scripts/build-pdf-capability-scorecard.py
}

mode="${1:-}"
dir="${2:-}"
case "$mode" in
  "")
    build_derived
    rc=0
    git diff --exit-code "${DIFF_IGNORE[@]}" -- "${GENERATED[@]}" || rc=$?
    # On a PASS the only worktree change is the stamp this gate just wrote, so it
    # is restored: otherwise the next runner_state_init keys the run "-dirty",
    # every ledger row says treeDirty=yes and the clean-tree checkpoints become
    # unreachable. On a FAIL the files stay put for review.
    if [ "$rc" = 0 ]; then
      git checkout -- "${GENERATED[@]}"
    fi
    exit "$rc"
    ;;
  --refresh-outcomes)
    [ -n "$dir" ] && [ -d "$dir" ] || { echo "usage: $0 --refresh-outcomes LOG_DIR" >&2; exit 2; }
    if ! git diff --quiet "${DIFF_IGNORE[@]}" -- "${GENERATED[@]}"; then
      echo "SKIPPED: generated registry files are already modified — the t0 pdf-capability-registry row left drift for review, or an adoption awaits its commit; commit or restore them, then re-run (prerequisite missing)"
      exit 77
    fi
    [ -s "$dir/ledger.jsonl" ] || { echo "SKIPPED: no $dir/ledger.jsonl — nothing to import (prerequisite missing)"; exit 77; }
    # The run's trx: the ledger's trx field for rows that ran here; for rows a
    # --resume took from a checkpoint, the trx beside the evidence log in the
    # earlier run directory. A directory glob would miss the second kind.
    trx_list="$(python3 - "$dir/ledger.jsonl" <<'PY'
import json, os, sys
seen = []
for line in open(sys.argv[1], encoding="utf-8"):
    line = line.strip()
    if not line:
        continue
    try:
        r = json.loads(line)
    except ValueError:
        continue
    if r.get("kind") not in ("test", "project", "project-chunked"):
        continue
    cand = r.get("trx") or ""
    if not cand and r.get("status") == "SKIP_CHECKPOINTED" and r.get("evidenceLog"):
        cand = os.path.splitext(r["evidenceLog"])[0] + ".trx"
    if cand and os.path.isfile(cand) and cand not in seen:
        seen.append(cand)
print("\n".join(seen))
PY
)"
    if [ -z "$trx_list" ]; then
      echo "SKIPPED: $dir/ledger.jsonl names no trx that exists — nothing to import (prerequisite missing)"
      exit 77
    fi
    trx_args=()
    while IFS= read -r f; do [ -n "$f" ] && trx_args+=(--trx "$f"); done <<< "$trx_list"
    # From here every exit path restores the tree, success included.
    trap 'git checkout -- "${GENERATED[@]}"' EXIT
    python3 scripts/collect-pdf-test-outcomes.py "${trx_args[@]}"
    build_derived
    out="$dir/registry-outcomes"
    mkdir -p "$out"
    changed=()
    for f in "${GENERATED[@]}"; do
      if ! git diff --quiet "${DIFF_IGNORE[@]}" -- "$f"; then
        changed+=("$f")
        cp "$f" "$out/"
      fi
    done
    python3 - "$out/$IMPORT_SUMMARY" "$GEN/test-outcomes.json" "$dir" ${changed[@]+"${changed[@]}"} <<'PY'
import json, subprocess, sys
summary_path, snapshot, run_dir, *changed = sys.argv[1:]
new = json.load(open(snapshot))
try:
    old = json.loads(subprocess.run(["git", "show", f"HEAD:{snapshot}"], capture_output=True, text=True, check=True).stdout)
except Exception:
    old = {}
def counts(doc):
    s = doc.get("summary") if isinstance(doc, dict) else None
    s = s if isinstance(s, dict) else {}
    o = s.get("outcomes") if isinstance(s.get("outcomes"), dict) else {}
    return {"trxFiles": s.get("trxFiles", 0), "tests": s.get("tests", 0), "passed": o.get("Passed", 0),
            "failed": o.get("Failed", 0), "notExecuted": o.get("NotExecuted", 0)}
n, c = counts(new), counts(old)
c["recordedAt"] = old.get("recordedAt") if isinstance(old, dict) else None
c["gitRevision"] = old.get("gitRevision") if isinstance(old, dict) else None
summary = dict(n, committed=c, deltaTests=n["tests"] - c["tests"], changedFiles=changed, runDir=run_dir)
json.dump(summary, open(summary_path, "w"), indent=2)
print(f"registry evidence: {n['trxFiles']} trx · {n['tests']} tests: {n['passed']} passed, {n['failed']} failed, "
      f"{n['notExecuted']} not executed — committed snapshot {c['tests']} tests "
      f"({str(c['recordedAt'] or '?')[:10]}), Δtests {summary['deltaTests']:+d}")
print(f"{len(changed)} regenerated files stashed under {run_dir}/registry-outcomes (pruned with the run dir); "
      f"adopt with: scripts/check-pdf-capability-registry.sh --adopt {run_dir}")
PY
    exit 0
    ;;
  --adopt)
    [ -n "$dir" ] && [ -d "$dir/registry-outcomes" ] || { echo "usage: $0 --adopt LOG_DIR (needs LOG_DIR/registry-outcomes/)" >&2; exit 2; }
    for f in "$dir"/registry-outcomes/*; do
      case "$(basename "$f")" in "$IMPORT_SUMMARY") continue ;; esac
      cp "$f" "$GEN/$(basename "$f")"
    done
    git status --short -- "$GEN"
    echo "review the diff, then commit the snapshot with the run's evidence (#1366); t0 reads NEW until it is committed"
    ;;
  *)
    echo "usage: $0 [--refresh-outcomes LOG_DIR | --adopt LOG_DIR]" >&2
    exit 2
    ;;
esac

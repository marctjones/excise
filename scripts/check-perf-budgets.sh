#!/usr/bin/env bash
# Focused performance regression gate for the #596/#602 hot-path budgets.
#
# Runs the stable workflow subset (profile-workflows: save-roundtrip,
# redaction-save, all-page-render, text-extract + advisory render/open/search
# steps) on the smoke corpus min-of-N times and compares against the
# checked-in budgets in tests/perf-budgets/workflow-budgets.json.
#
# Design (see issue #602):
#   - ALLOCATION is the hard gate. Managed allocation (GC bytes on the driver
#     thread) is nearly machine- and run-invariant, so a +30% band catches a
#     real regression (e.g. redaction-save back to 3+ GB after #743's -72%)
#     without flaking on machine variance.
#   - TIME is advisory by default (warn at 1.5x, fail at 2.0x only with
#     --time-gate fail). Wall time varies 5-15% run-to-run and far more across
#     machine classes; min-of-N absorbs run noise but not a slower machine.
#     release-smoke (a known developer machine) opts into --time-gate fail;
#     shared CI stays warn-only.
#   - Budgets are compared over the INTERSECTION of budgeted and measured
#     PDFs, so a partial corpus gates what ran and reports what was skipped.
#   - A missing corpus or missing budgets file SKIPS (exit 0) with a clear
#     message - this is a performance signal, not a correctness gate, and it
#     must never masquerade as one (CI runners have no smoke corpus).
#   - Violations name the WORKFLOW and its owning hot path (writer/object
#     store, redaction pipeline, renderer, text extractor), plus the worst
#     per-PDF contributor - not just a PDF filename.
#
# Reproducing / re-baselining the budgets:
#   scripts/download-smoke-corpus.sh              # corpus (gitignored)
#   scripts/check-perf-budgets.sh --update        # rewrites budgets from HEAD
# Budgets must be captured in Release on a quiet machine; the machine class,
# commit, and capture command are stamped into the budgets file. Absolute ms
# baselines are only comparable within a machine class - allocation baselines
# travel across machines, which is why allocation is the hard gate.
#
# Exit codes: 0 pass/skip/warn-mode, 1 budget violation (fail mode), 2 usage.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

CONFIG="${CONFIG:-Release}"
RUNS=3
CORPUS="${EXCISE_PERF_CORPUS:-test-pdfs/smoke}"
BUDGETS="tests/perf-budgets/workflow-budgets.json"
OUTPUT_DIR="${EXCISE_PERF_OUTPUT_DIR:-logs/perf-budgets/latest}"
MODE="fail"
TIME_GATE="${EXCISE_PERF_TIME_GATE:-warn}"
UPDATE=0
NO_BUILD=0

usage() {
    cat <<'EOF'
Focused performance regression gate (#602).

Usage: scripts/check-perf-budgets.sh [options]

Options:
  --runs N            Profiling passes; min per (pdf,step) is compared (default 3).
  --corpus DIR        PDF corpus (default test-pdfs/smoke; get it via
                      scripts/download-smoke-corpus.sh). Missing dir => SKIPPED, exit 77 (the runner shows a SKIPPED row).
  --budgets FILE      Budgets file (default tests/perf-budgets/workflow-budgets.json).
  --output-dir DIR    Report/run output (default logs/perf-budgets/latest).
  --mode fail|warn    fail: exit 1 on hard-budget violation (local default).
                      warn: always exit 0, print violations (CI default - shared
                      runners are too variable for a blocking wall-time gate,
                      and have no smoke corpus anyway).
  --time-gate warn|fail
                      Whether a 2.0x time breach fails (only meaningful with
                      --mode fail). Default warn: time is machine-variable;
                      allocation is the hard signal. release-smoke passes
                      --time-gate fail (known machine class).
  --update            Rewrite the budgets file from this measurement (baselines
                      re-anchor; tolerance bands and hot-path metadata kept).
  --no-build          Skip building Excise.RenderTools.
  -h, --help          This help.

Environment: CONFIG (Release required for gating; non-Release forces warn mode),
             EXCISE_PERF_CORPUS, EXCISE_PERF_OUTPUT_DIR, EXCISE_PERF_TIME_GATE.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --runs) RUNS="$2"; shift 2 ;;
        --corpus) CORPUS="$2"; shift 2 ;;
        --budgets) BUDGETS="$2"; shift 2 ;;
        --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
        --mode) MODE="$2"; shift 2 ;;
        --time-gate) TIME_GATE="$2"; shift 2 ;;
        --update) UPDATE=1; shift ;;
        --no-build) NO_BUILD=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

case "$MODE" in fail|warn) ;; *) echo "--mode must be fail|warn" >&2; exit 2 ;; esac
case "$TIME_GATE" in fail|warn) ;; *) echo "--time-gate must be fail|warn" >&2; exit 2 ;; esac

if [ ! -d "$CORPUS" ]; then
    echo "perf-budgets: SKIPPED - corpus not found at $CORPUS"
    echo "perf-budgets: fetch it with scripts/download-smoke-corpus.sh (gitignored)."
    exit 77   # prerequisite missing (LOCAL_GATES.md): a SKIPPED row, never a green
fi

if [ "$UPDATE" != "1" ] && [ ! -f "$BUDGETS" ]; then
    echo "perf-budgets: SKIPPED - budgets file not found at $BUDGETS (run with --update to create it)."
    exit 77
fi

if [ "$CONFIG" != "Release" ]; then
    echo "perf-budgets: CONFIG=$CONFIG is not Release; budgets are Release-anchored - forcing --mode warn."
    MODE="warn"
fi

TOOL="tools/Excise.RenderTools/bin/$CONFIG/net10.0/Excise.RenderTools.dll"
if [ "$NO_BUILD" != "1" ]; then
    echo "perf-budgets: building Excise.RenderTools ($CONFIG)"
    dotnet build tools/Excise.RenderTools/Excise.RenderTools.csproj -c "$CONFIG" --nologo -v quiet
fi
if [ ! -f "$TOOL" ]; then
    echo "perf-budgets: ERROR - $TOOL not found (build failed or wrong CONFIG)" >&2
    exit 2
fi

mkdir -p "$OUTPUT_DIR"
REPORT="$OUTPUT_DIR/perf-budget-report.json"

# Min-of-N: each pass writes its own incremental NDJSON + JSON under run-i/;
# the comparison below re-runs after EVERY pass so a partial/interrupted run
# still leaves a usable perf-budget-report.json behind.
for i in $(seq 1 "$RUNS"); do
    echo "perf-budgets: profiling pass $i/$RUNS"
    dotnet "$TOOL" profile-workflows \
        --corpus "$CORPUS" \
        --output-dir "$OUTPUT_DIR/run-$i" \
        --page-limit 8 --dpi 96 --zoom-dpi 192 --search-term the \
        > "$OUTPUT_DIR/run-$i.log" 2>&1

    python3 - "$OUTPUT_DIR" "$BUDGETS" "$REPORT" "$MODE" "$TIME_GATE" "$UPDATE" "$i" "$RUNS" "$CORPUS" <<'PY'
import glob, json, os, platform, subprocess, sys

out_dir, budgets_path, report_path, mode, time_gate, update, run_no, runs, corpus = sys.argv[1:10]
update = update == "1"
final = run_no == runs

# Metadata for budgeted workflows: gate class + owning hot path. The hard set
# is the #596 optimization surface (#743 save/redaction-save, #598/#599 render,
# #600 extract); the rest are advisory (reported, warned, never failing).
WORKFLOWS = {
    "save-roundtrip": ("hard",
        "Excise.Core writer/object store: PdfDocument.SaveToBytes -> WriteObjects -> "
        "GetObject/GetObjectFromStream object-stream materialization (#743; #597 report s3.1)"),
    "redaction-save": ("hard",
        "Excise.Core redaction pipeline + writer: RedactArea + StructureTreeRedactionScrubber "
        "tree walk + SaveToBytes (#743; #597 report s3.4). SECURITY-CRITICAL: never win this "
        "budget by short-cutting the glyph-removal pipeline - see CLAUDE.md redaction rules."),
    "all-page-render": ("hard",
        "Excise.Rendering SkiaRenderer: form-XObject execution (#598), DeviceCMYK blend + "
        "soft-mask compositing (#599), glyph-path text (#600); #597 report s3.2"),
    "text-extract": ("hard",
        "Excise.Core TextExtractor: content parse + letter accumulators + font/XObject "
        "resource materialization (#600; #597 report s3.3)"),
    "first-page-render": ("advisory", "Excise.Rendering SkiaRenderer first-page latency (#598/#599)"),
    "navigation-rerender": ("advisory", "Excise.Rendering re-render on navigation (#598/#601)"),
    "zoom-rerender": ("advisory", "Excise.Rendering re-render at zoom DPI (#598/#599/#601)"),
    "open": ("advisory", "Excise.Core parser: open to first structural access (#597)"),
    "search": ("advisory", "Excise.Core word segmentation + term scan over cached letters (#600)"),
}
POLICY = {
    "allocation": {"warnRatio": 1.15, "failRatio": 1.30, "minDeltaMB": 10,
                   "note": "managed GC bytes, driver thread; machine-stable => HARD gate; "
                           "deltas under minDeltaMB never warn/fail (micro-noise floor)"},
    "time": {"warnRatio": 1.50, "failRatio": 2.00, "minDeltaMs": 100,
             "note": "wall ms, min-of-N; machine-variable => advisory unless --time-gate fail; "
                     "deltas under minDeltaMs never warn/fail (fixed-overhead noise floor)"},
}

# ---- aggregate: min per (pdf, step) across completed runs ------------------
best = {}   # (pdf, step) -> {"ms":, "bytes":, "errors":}
for prof in sorted(glob.glob(os.path.join(out_dir, "run-*", "workflow-profile.json"))):
    with open(prof) as f:
        data = json.load(f)
    for rec in data.get("perPdf", []):
        key = (rec["pdf"], rec["step"])
        cur = best.setdefault(key, {"ms": float("inf"), "bytes": float("inf"), "errors": 0})
        if rec.get("status") == "ERROR":
            cur["errors"] += 1
            continue
        cur["ms"] = min(cur["ms"], rec["elapsedMs"])
        cur["bytes"] = min(cur["bytes"], rec["allocatedBytes"])

measured = {}  # step -> {pdf: {"ms":, "mb":}}
errors = {}
for (pdf, step), v in best.items():
    if v["ms"] == float("inf"):
        errors.setdefault(step, []).append(pdf)
        continue
    measured.setdefault(step, {})[pdf] = {
        "ms": round(v["ms"], 2), "mb": round(v["bytes"] / (1024.0 * 1024.0), 3)}

# ---- update mode: rewrite budgets from this measurement --------------------
if update:
    if not final:
        sys.exit(0)
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"],
                            capture_output=True, text=True).stdout.strip()
    import datetime
    budgets = {
        "schemaVersion": 1,
        "issues": ["#596", "#602"],
        "capturedUtc": datetime.datetime.now(datetime.timezone.utc)
            .strftime("%Y-%m-%dT%H:%M:%SZ"),
        "commit": commit,
        "machine": f"{platform.machine()} / {platform.platform()} / .NET SDK "
                   + subprocess.run(["dotnet", "--version"], capture_output=True,
                                    text=True).stdout.strip(),
        "reproduce": ("scripts/download-smoke-corpus.sh && scripts/check-perf-budgets.sh --update "
                      "(Release build, quiet machine; page-limit 8, dpi 96, zoom-dpi 192, "
                      f"search-term 'the', min of {runs} passes per (pdf,step))"),
        "machineCaveat": ("Time baselines (ms) are only comparable on this machine class; "
                          "re-baseline with --update on a different class, or gate time as warn. "
                          "Allocation baselines (MB) are machine-stable and are the hard gate."),
        "config": {"pageLimit": 8, "dpi": 96, "zoomDpi": 192, "searchTerm": "the",
                   "runs": int(runs), "buildConfig": "Release",
                   "aggregation": "min per (pdf,step) across runs; gate compares sums over "
                                  "the intersection of budgeted and measured PDFs"},
        "policy": POLICY,
        "workflows": {},
    }
    for step, (gate, hot) in WORKFLOWS.items():
        per = measured.get(step)
        if not per:
            continue
        budgets["workflows"][step] = {
            "gate": gate,
            "hotPath": hot,
            "baseline": {
                "totalMs": round(sum(p["ms"] for p in per.values()), 1),
                "totalAllocatedMB": round(sum(p["mb"] for p in per.values()), 1),
            },
            "perPdf": dict(sorted(per.items())),
        }
    os.makedirs(os.path.dirname(budgets_path), exist_ok=True)
    with open(budgets_path, "w") as f:
        json.dump(budgets, f, indent=2)
        f.write("\n")
    print(f"perf-budgets: budgets rewritten -> {budgets_path} (commit {commit})")
    sys.exit(0)

# ---- compare mode ----------------------------------------------------------
with open(budgets_path) as f:
    budgets = json.load(f)
policy = budgets.get("policy", POLICY)
a_warn, a_fail = policy["allocation"]["warnRatio"], policy["allocation"]["failRatio"]
t_warn, t_fail = policy["time"]["warnRatio"], policy["time"]["failRatio"]
a_floor = policy["allocation"].get("minDeltaMB", 10)
t_floor = policy["time"].get("minDeltaMs", 100)

rows, failures, warnings = [], [], []
for step, spec in budgets.get("workflows", {}).items():
    gate = spec.get("gate", "advisory")
    per_budget = spec.get("perPdf", {})
    per_meas = measured.get(step, {})
    common = sorted(set(per_budget) & set(per_meas))
    missing = sorted(set(per_budget) - set(per_meas))
    row = {"workflow": step, "gate": gate, "pdfsCompared": len(common),
           "pdfsMissing": missing, "status": "PASS", "checks": []}

    if errors.get(step):
        row["status"] = "ERROR"
        row["errorPdfs"] = errors[step]
        msg = f"{step}: workflow ERRORED on {', '.join(errors[step])} in every pass"
        (failures if gate == "hard" else warnings).append(
            {"workflow": step, "metric": "error", "message": msg,
             "hotPath": spec.get("hotPath", "")})
    if not common:
        row["status"] = "SKIPPED" if row["status"] == "PASS" else row["status"]
        rows.append(row)
        continue

    base_ms = sum(per_budget[p]["ms"] for p in common)
    base_mb = sum(per_budget[p]["mb"] for p in common)
    meas_ms = sum(per_meas[p]["ms"] for p in common)
    meas_mb = sum(per_meas[p]["mb"] for p in common)

    def worst(metric):
        return max(common, key=lambda p: per_meas[p][metric] / max(per_budget[p][metric], 1e-9))

    for metric, base, meas, warn_r, fail_r, floor, unit, hard_capable in (
            ("allocation", base_mb, meas_mb, a_warn, a_fail, a_floor, "MB", True),
            ("time", base_ms, meas_ms, t_warn, t_fail, t_floor, "ms", time_gate == "fail")):
        ratio = meas / max(base, 1e-9)
        check = {"metric": metric, "baseline": round(base, 1), "measured": round(meas, 1),
                 "ratio": round(ratio, 3), "warnRatio": warn_r, "failRatio": fail_r,
                 "unit": unit, "verdict": "ok"}
        if ratio > warn_r and (meas - base) >= floor:
            w = worst("mb" if metric == "allocation" else "ms")
            hint = ("allocation regression - machine-stable signal; look for per-call/"
                    "per-object churn on the hot path" if metric == "allocation" else
                    "wall-time regression - re-run to rule out machine noise; if it "
                    "reproduces, profile with dotnet-trace (see #597 baseline README)")
            msg = (f"{step} [{gate}] {metric}: {meas:.1f}{unit} vs budget "
                   f"{base:.1f}{unit} x{warn_r if ratio <= fail_r else fail_r} "
                   f"(ratio {ratio:.2f}); worst contributor: {w}; {hint}")
            entry = {"workflow": step, "metric": metric, "ratio": round(ratio, 3),
                     "worstPdf": w, "message": msg, "hotPath": spec.get("hotPath", "")}
            if ratio > fail_r and gate == "hard" and hard_capable:
                check["verdict"] = "fail"
                row["status"] = "FAIL"
                failures.append(entry)
            else:
                check["verdict"] = "warn"
                if row["status"] == "PASS":
                    row["status"] = "WARN"
                warnings.append(entry)
        row["checks"].append(check)
    rows.append(row)

report = {
    "schemaVersion": 1,
    "issues": ["#596", "#602"],
    "kind": "performance-budget",
    "note": "Performance signal only - reported separately from correctness/quality gates.",
    "mode": mode, "timeGate": time_gate,
    "runsCompleted": int(run_no), "runsPlanned": int(runs),
    "partial": not final,
    "corpus": corpus,
    "budgetsFile": budgets_path,
    "budgetsCommit": budgets.get("commit"),
    "budgetsMachine": budgets.get("machine"),
    "workflows": rows,
    "failures": failures,
    "warnings": warnings,
}
with open(report_path, "w") as f:
    json.dump(report, f, indent=2)
    f.write("\n")

if not final:
    print(f"perf-budgets: pass {run_no}/{runs} aggregated (partial report -> {report_path})")
    sys.exit(0)

print()
print(f"perf-budgets: report -> {report_path}")
print(f"perf-budgets: budgets {budgets_path} (captured {budgets.get('capturedUtc')} "
      f"@ {budgets.get('commit')} on {budgets.get('machine')})")
print()
print(f"{'workflow':<22} {'gate':<9} {'alloc MB (budget)':>20} {'time ms (budget)':>20} status")
for r in rows:
    a = next((c for c in r["checks"] if c["metric"] == "allocation"), None)
    t = next((c for c in r["checks"] if c["metric"] == "time"), None)
    fmt = lambda c: f"{c['measured']:.0f} ({c['baseline']:.0f})" if c else "-"
    print(f"{r['workflow']:<22} {r['gate']:<9} {fmt(a):>20} {fmt(t):>20} {r['status']}")
    miss = r.get("pdfsMissing", [])
    if miss:
        shown = ", ".join(miss[:3]) + (f", +{len(miss) - 3} more" if len(miss) > 3 else "")
        print(f"    (gated {r['pdfsCompared']} of {r['pdfsCompared'] + len(miss)} budgeted "
              f"PDFs; not measured: {shown})")
print()
for w in warnings:
    print(f"WARN: {w['message']}")
    print(f"      hot path: {w['hotPath']}")
for fl in failures:
    print(f"FAIL: {fl['message']}")
    print(f"      hot path: {fl['hotPath']}")
if failures:
    if mode == "fail":
        print("\nperf-budgets: FAIL - performance budget violated (performance signal, "
              "not a correctness failure).")
        sys.exit(1)
    print("\nperf-budgets: violations found, but --mode warn => exit 0 "
          "(shared-CI posture; run locally with --mode fail on a stable machine).")
elif warnings:
    print("\nperf-budgets: PASS with warnings.")
else:
    print("\nperf-budgets: PASS - all workflows within budget.")
sys.exit(0)
PY
    rc=$?
    if [ "$rc" != "0" ]; then
        exit "$rc"
    fi
done

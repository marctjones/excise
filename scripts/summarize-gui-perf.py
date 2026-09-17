#!/usr/bin/env python3
# TOOLING — not a gate (tests/gates-tooling.txt): the body of
# scripts/run-gui-perf-scenarios.sh's summarising step (#1497).
#
# Joins three sources into one run manifest and one human summary:
#   steps.jsonl   the in-app journal, one row per step boundary (managed heap,
#                 committed heap, CPU, viewer cache counters)
#   samples.tsv   the outer sampler, joined to steps by boundary sequence
#                 (RSS, physical footprint, load) — footprint has no in-process
#                 equivalent and is the number #1461/#1496 are argued in
#   metrics.jsonl the app's own #1491 sink (band render p50/p99, trims)
#
# THE ONE IDEA WORTH KEEPING
#   A measurement without its noise floor is not evidence. Every delta printed
#   here carries a verdict against
#       floor = max(noise spread, runner overhead, sampler overhead)
#   and a delta below its floor prints as BELOW-FLOOR rather than as a win.
#   Without a calibration file the floor is UNKNOWN — which is reported as
#   UNKNOWN, never silently treated as zero.
#
#   This is not a new idea in this repo. tests/reference-performance already
#   gates on oracle-relative ratios because absolute ms moved 1.27-2.24x
#   between two identical passes minutes apart, and
#   scripts/check-perf-budgets.sh already refuses to warn below minDeltaMB=10 /
#   minDeltaMs=100. This applies the same discipline to live GUI memory, where
#   until now the numbers came from a human driving the app.
import argparse
import json
import os
import statistics
import sys
from pathlib import Path

MB = 1024.0 * 1024.0

# Metrics whose deltas the summary reports, with the unit they print in.
TRACKED = [
    ("footprintMB", "MB", "physical footprint (outer; the #1461/#1496 number)"),
    ("rssMB", "MB", "resident set (outer)"),
    ("committedMB", "MB", "committed managed heap (in-app)"),
    ("liveHeapMB", "MB", "live managed heap (in-app)"),
    ("cpuTotalMs", "ms", "cumulative process CPU (in-app)"),
]


def median(values):
    """Mean-of-middle-two, NaN-filtering. Matches EditModeSwitchReportTests."""
    clean = sorted(v for v in values if v is not None and v == v)
    if not clean:
        return None
    mid = len(clean) // 2
    if len(clean) % 2 == 1:
        return clean[mid]
    return (clean[mid - 1] + clean[mid]) / 2.0


def percentile(values, pct):
    clean = sorted(v for v in values if v is not None and v == v)
    if not clean:
        return None
    index = max(0, min(len(clean) - 1, int(round(pct / 100.0 * (len(clean) - 1)))))
    return clean[index]


def read_steps(path):
    rows = []
    if not path.exists():
        return rows
    for line in path.read_text(errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError:
            # A run the machine killed can leave a torn last line. Everything
            # before it is still good; dropping it is correct.
            continue
    return rows


def read_samples(path):
    """samples.tsv keyed by boundary sequence. Later rows win (the boundary
    sample is taken after any periodic ones for the same seq)."""
    by_seq = {}
    if not path.exists():
        return by_seq
    lines = path.read_text(errors="replace").splitlines()
    if not lines:
        return by_seq
    header = lines[0].split("\t")
    for line in lines[1:]:
        parts = line.split("\t")
        if len(parts) != len(header):
            continue
        row = dict(zip(header, parts))
        if row.get("kind") != "boundary":
            continue
        try:
            seq = int(row["seq"])
        except (KeyError, ValueError):
            continue

        def num(name):
            text = (row.get(name) or "").strip()
            try:
                return float(text)
            except ValueError:
                return None

        by_seq[seq] = {
            "rssMB": num("rssMB"),
            "footprintMB": num("footprintMB"),
            "cpuSec": num("cpuSec"),
            "load1": num("load1"),
        }
    return by_seq


def read_metrics(path):
    """Band render times and trim counts from the #1491 sink."""
    band, single, trims, reclaims = [], [], 0, []
    if not path.exists():
        return {}
    for line in path.read_text(errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        name = obj.get("instrument")
        value = obj.get("value")
        if name is None or value is None:
            continue
        if name == "excise.viewer.continuous.band.render.duration":
            band.append(float(value))
        elif name == "excise.viewer.single_page.render.duration":
            single.append(float(value))
        elif name == "excise.app.cache_trim.requests":
            trims += float(value)
        elif name == "excise.app.heap_reclaim.duration":
            reclaims.append(float(value))

    out = {"bandRenders": len(band), "singleRenders": len(single),
           "cacheTrimRequests": trims, "heapReclaims": len(reclaims)}
    if band:
        out["bandRenderP50Ms"] = percentile(band, 50)
        out["bandRenderP99Ms"] = percentile(band, 99)
    if single:
        out["singleRenderP50Ms"] = percentile(single, 50)
        out["singleRenderP99Ms"] = percentile(single, 99)
    if reclaims:
        out["heapReclaimP50Ms"] = percentile(reclaims, 50)
    return out


def steps_from_samples(samples):
    """Synthesize step rows for a launch that ran WITHOUT the in-app runner.

    The runner-overhead control is a plain launch with EXCISE_PERF_SCENARIO
    unset — so there is no journal, only the outer sampler's rows. Without this,
    `collect_run` returned None for every control run, `build_calibration` saw an
    empty "off" group, runner overhead was never computed, and the floor
    silently fell back to noise-only. The in-app fields are None here, which is
    correct: nothing in-process was measuring.
    """
    rows = []
    for seq in sorted(samples):
        rows.append({"seq": seq, "scenario": None, "repeat": 1,
                     "step": "launch" if seq == 0 else "held", "op": "norunner",
                     "ok": True, "note": None, "wallMs": None, "monotonicMs": None})
    return rows


def collect_run(directory):
    """One (scenario, repeat) launch directory -> one manifest entry."""
    steps = read_steps(directory / "steps.jsonl")
    samples_for_fallback = read_samples(directory / "samples.tsv")
    runner_present = bool(steps)
    if not steps:
        steps = steps_from_samples(samples_for_fallback)
        if not steps:
            return None
    samples = samples_for_fallback
    metrics = read_metrics(directory / "metrics.jsonl")

    result = {}
    result_path = directory / "scenario-result.json"
    if result_path.exists():
        try:
            result = json.loads(result_path.read_text())
        except json.JSONDecodeError:
            result = {}

    def mb(step, key):
        """Absent means NOT MEASURED, which is None — never 0.

        A runner-off control run has no in-app fields at all, and rendering
        those as 0 MB would read as "the heap was empty" instead of "nobody
        looked".
        """
        value = step.get(key)
        return None if value is None else value / MB

    merged = []
    for step in steps:
        seq = step.get("seq")
        outer = samples.get(seq, {})
        merged.append({
            "seq": seq,
            "step": step.get("step"),
            "op": step.get("op"),
            "ok": step.get("ok", True),
            "note": step.get("note"),
            "wallMs": step.get("wallMs"),
            "monotonicMs": step.get("monotonicMs"),
            "liveHeapMB": mb(step, "liveHeapBytes"),
            "committedMB": mb(step, "committedBytes"),
            "fragmentedMB": mb(step, "fragmentedBytes"),
            "allocatedMB": mb(step, "allocatedBytes"),
            "workingSetMB": mb(step, "workingSetBytes"),
            # A runner-off control has no in-app CPU reading, but the outer
            # sampler's `ps -o time=` does — and the runner-overhead comparison
            # needs the two sides to be commensurable, so fall back to it.
            "cpuTotalMs": step.get("cpuTotalMs") if step.get("cpuTotalMs") is not None
                else (outer["cpuSec"] * 1000.0 if outer.get("cpuSec") is not None else None),
            "gen2Collections": step.get("gen2Collections"),
            "continuousResidentMB": mb(step, "continuousResidentBytes"),
            "continuousEntries": step.get("continuousEntries"),
            "singlePageEntries": step.get("singlePageEntries"),
            # #1551-#1554: absent in journals written before the multi-document
            # set existed, which is None (not measured), never 0.
            "openDocuments": step.get("openDocuments"),
            "documentWindows": step.get("documentWindows"),
            # Outer numbers. None when the boundary was not acknowledged in
            # time — recorded as absent rather than paired with a stale sample.
            "rssMB": outer.get("rssMB"),
            "footprintMB": outer.get("footprintMB"),
            "load1": outer.get("load1"),
            "outerSampled": bool(outer),
        })

    scenario = result.get("scenario") or steps[0].get("scenario")
    return {
        "scenario": scenario,
        "runnerPresent": runner_present,
        "repeat": result.get("repeat", steps[0].get("repeat", 1)),
        "directory": str(directory),
        "failures": result.get("failures", sum(1 for s in merged if not s["ok"])),
        "boundariesSampled": sum(1 for s in merged if s["outerSampled"]),
        "boundaries": len(merged),
        "runtimeMode": (result.get("configuration") or {}).get("runtimeMode"),
        "steps": merged,
        "metrics": metrics,
    }


def never_rendered(run):
    """Did this run open a document the viewer never actually rendered?

    The failure this catches, observed on the first live altona-close run: the
    runner opened the document through the SCRIPTING load path, which does
    "headless document loading (no thumbnails/rendering)". The file was parsed,
    the viewer was never driven, every cache counter stayed 0, and the scroll
    had nothing laid out to scroll -- yet the run emitted a complete row of
    plausible numbers and a confident verdict about #1461 that was simply not a
    measurement of it.

    A scenario that opens a document and then records no render measurements
    and no cache entries at any boundary did not measure what it claims to.
    That has to be loud, because every individual number still looks fine.
    """
    opened = any(s.get("op") == "open" for s in run["steps"])
    if not opened:
        return None
    metrics = run.get("metrics") or {}
    rendered = (metrics.get("bandRenders", 0) or 0) + (metrics.get("singleRenders", 0) or 0)
    entries = max((s.get("continuousEntries") or 0) for s in run["steps"])
    single = max((s.get("singlePageEntries") or 0) for s in run["steps"])
    if rendered == 0 and entries == 0 and single == 0:
        return ("opened a document but recorded 0 render measurements and 0 cache "
                "entries at every boundary - the viewer was never driven, so these "
                "numbers are NOT a measurement of this scenario")
    return None


def step_value(run, label, metric):
    for step in run["steps"]:
        if step.get("step") == label:
            return step.get(metric)
    return None


def evaluate_checks(group, checks, calibration, noise, scenario):
    """A scenario's declared checks (tests/gui-perf-scenarios.json "checks").

    `drop`: did `metric` fall from boundary `from` to boundary `to`? The delta
    is the median over the group's runs and is judged against this scenario's
    floor, so "it dropped" is only claimed above the noise. A rise, or no
    change, is reported as DID-NOT-DROP whatever the floor says: the check
    exists because closing a document is supposed to give memory back.
    """
    results = []
    scenario_floor = derive_floor(calibration, noise, scenario=scenario)
    for check in checks or []:
        if check.get("kind") != "drop":
            results.append({"check": check, "verdict": "UNKNOWN-CHECK"})
            continue
        metric = check["metric"]
        pairs = [(step_value(r, check["from"], metric), step_value(r, check["to"], metric)) for r in group]
        deltas = [b - a for a, b in pairs if a is not None and b is not None]
        delta = median(deltas) if deltas else None
        floor_entry = scenario_floor.get(metric)
        if delta is None:
            outcome = "NO-DATA"
        elif delta >= 0:
            outcome = "DID-NOT-DROP"
        else:
            judged = verdict(delta, floor_entry)
            outcome = "DROPPED" if judged == "IMPROVED" else f"DROPPED ({judged})"
        results.append({
            "check": check, "delta": delta, "runs": len(deltas),
            "from": median([a for a, _ in pairs if a is not None]),
            "to": median([b for _, b in pairs if b is not None]),
            "floor": (floor_entry or {}).get("value"), "verdict": outcome,
        })
    return results


def peak_and_final(run, metric):
    values = [s.get(metric) for s in run["steps"] if s.get(metric) is not None]
    if not values:
        return None, None
    return max(values), values[-1]


def baseline_value(run, metric):
    """The pre-open floor: the 'baseline' boundary the runner writes first."""
    for step in run["steps"]:
        if step.get("step") == "baseline" and step.get(metric) is not None:
            return step[metric]
    return None


def scenario_spread(runs, metric):
    """min-max spread of the PEAK of `metric` across repeats of one scenario.

    The peak, not the final value, because that is what a user's machine
    actually has to hold. Reported as an absolute span in the metric's unit,
    which is what a candidate optimisation has to beat.
    """
    peaks = [peak_and_final(r, metric)[0] for r in runs]
    peaks = [p for p in peaks if p is not None]
    if len(peaks) < 2:
        return None
    return {
        "n": len(peaks),
        "median": median(peaks),
        "min": min(peaks),
        "max": max(peaks),
        "spread": max(peaks) - min(peaks),
    }


def build_calibration(run_root, runs_by_key):
    """The four #1497 calibration measurements, as far as this run measured them."""
    calibration = {
        "method": {
            "runnerOverhead":
                "The `null` scenario (launch, settle, idle 10s, quit) with the "
                "in-app runner ON, against a plain launch of the same bundle "
                "with EXCISE_PERF_SCENARIO unset and the app held open for the "
                "same wall time then quit by Apple Event, 5 runs each. "
                "Question: does having the harness inside the process change "
                "the process? Expected to be near zero — the runner is a "
                "static class doing Task.Delay and counter reads — and a "
                "near-zero result is the correct one, not a failed measurement.",
            "samplerOverhead":
                "`w9-launch-idle` with the OUTER sampler at none, 5s and 1s, "
                "3 runs each. Question: does watching cost more than the thing "
                "watched? Pick the cheapest interval that still resolves the "
                "effect being chased.",
            "drivingFidelity":
                "NOT measured by this script. `irs-page30` driven in-app "
                "against the same 30 Page Downs delivered as real keyboard "
                "input, once. The residual is input dispatch plus hit testing. "
                "This is the honest limit of the instrument: driving through "
                "the view model is not a user's input path, and the residual "
                "bounds that gap rather than removing it.",
            "noiseFloor":
                "Every scenario x5 on a quiet machine, load1 recorded per run. "
                "Reported as median and min-max spread of each metric's peak.",
        },
        "measured": {},
        "floor": {},
    }

    # 1. runner overhead
    on = [r for key, r in runs_by_key.items() if key[0] == "calib-runner-on"]
    off = [r for key, r in runs_by_key.items() if key[0] == "calib-runner-off"]
    if on and off:
        for metric, unit, _ in TRACKED:
            on_peaks = [p for p in (peak_and_final(r, metric)[0] for r in on) if p is not None]
            off_peaks = [p for p in (peak_and_final(r, metric)[0] for r in off) if p is not None]
            if on_peaks and off_peaks:
                calibration["measured"].setdefault("runnerOverhead", {})[metric] = {
                    "unit": unit,
                    "runnerOnMedian": median(on_peaks),
                    "runnerOffMedian": median(off_peaks),
                    "delta": median(on_peaks) - median(off_peaks),
                    "n": [len(on_peaks), len(off_peaks)],
                }

    # 2. sampler overhead
    # "none" is the baseline: no outer sampling at all. The harness renamed it
    # from "end-only" and this loop kept the old name, so the one mode that
    # measures what sampling COSTS was silently dropped and "sampler overhead"
    # compared 1s against 5s only (#1497, found 2026-09-16). An unknown mode
    # directory is now an error rather than a quiet skip.
    known_modes = ("none", "5s", "1s")
    seen_modes = {key[0][len("calib-sampler-"):] for key in runs_by_key
                  if key[0].startswith("calib-sampler-")}
    unknown = seen_modes - set(known_modes)
    if unknown:
        raise SystemExit(f"summarize-gui-perf: unknown sampler mode dir(s): {sorted(unknown)}")
    for mode in known_modes:
        group = [r for key, r in runs_by_key.items() if key[0] == "calib-sampler-" + mode]
        if not group:
            continue
        for metric, unit, _ in TRACKED:
            peaks = [p for p in (peak_and_final(r, metric)[0] for r in group) if p is not None]
            if peaks:
                calibration["measured"].setdefault("samplerOverhead", {}) \
                    .setdefault(mode, {})[metric] = {
                        "unit": unit, "median": median(peaks), "n": len(peaks)}

    return calibration


def derive_floor(calibration, noise, scenario=None):
    """floor = max(noise spread, runner overhead, sampler overhead), per metric.

    ⚠️ The noise term is PER SCENARIO when one is named. Taking the max across
    every scenario makes Altona's ~20 MB jitter the floor for `w9-launch-idle`
    too, which sits near 336 MB in total — so a real 10 MB regression on the
    light scenario would read BELOW-FLOOR because an unrelated heavy scenario is
    noisy. The runner and sampler overheads are properties of the harness rather
    than of a scenario, so those stay global.

    UNKNOWN when nothing measured it. A missing floor must never read as zero:
    that is precisely how a sub-noise change gets reported as a win.
    """
    floor = {}
    for metric, unit, _ in TRACKED:
        candidates = []
        if scenario is None:
            # No scenario in hand (the run-wide summary): be conservative.
            spreads = [n[metric]["spread"] for n in noise.values()
                       if metric in n and n[metric] and n[metric].get("spread") is not None]
        else:
            entry = (noise.get(scenario) or {}).get(metric)
            spreads = [entry["spread"]] if entry and entry.get("spread") is not None else []
        if spreads:
            candidates.append(("noiseSpread", max(spreads)))

        runner = (calibration.get("measured", {}).get("runnerOverhead") or {}).get(metric)
        if runner and runner.get("delta") is not None:
            candidates.append(("runnerOverhead", abs(runner["delta"])))

        sampler = calibration.get("measured", {}).get("samplerOverhead") or {}
        sampler_values = [v[metric]["median"] for v in sampler.values()
                          if metric in v and v[metric].get("median") is not None]
        if len(sampler_values) >= 2:
            candidates.append(("samplerOverhead", max(sampler_values) - min(sampler_values)))

        if not candidates:
            floor[metric] = {"unit": unit, "value": None, "from": "UNKNOWN",
                             "note": "nothing in this run measured a floor for this metric; "
                                     "run --calibrate before calling any delta a win"}
            continue

        source, value = max(candidates, key=lambda c: c[1])
        entry = {"unit": unit, "value": value, "from": source,
                 "candidates": {name: v for name, v in candidates}}

        # A floor of exactly zero is almost never "this metric is noise-free".
        # It means the metric did not move at all across repeats, which in
        # practice means it was not captured — and a zero floor would then let
        # ANY delta read as a win, which is the failure this whole file exists
        # to prevent. Report it as UNKNOWN and say why.
        if value == 0:
            entry["value"] = None
            entry["from"] = "UNKNOWN"
            entry["note"] = (
                "measured spread was exactly 0, which means this metric did not vary "
                "across repeats at all — far more likely not captured than genuinely "
                "noise-free. Treated as UNKNOWN so a delta cannot pass against a zero floor.")
        floor[metric] = entry
    return floor


def verdict(delta, floor_entry):
    if delta is None:
        return "NO-DATA"
    if floor_entry is None or floor_entry.get("value") is None:
        return "FLOOR-UNKNOWN"
    if abs(delta) <= floor_entry["value"]:
        return "BELOW-FLOOR"
    return "IMPROVED" if delta < 0 else "REGRESSED"


def fmt(value, digits=1):
    if value is None:
        return "-"
    return f"{value:.{digits}f}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-dir", required=True)
    parser.add_argument("--scenario-file", required=True)
    parser.add_argument("--baseline")
    args = parser.parse_args()

    root = Path(args.run_dir)
    scenario_why = {}
    scenario_checks = {}
    try:
        doc = json.loads(Path(args.scenario_file).read_text())
        scenario_why = {s["id"]: s.get("why", "") for s in doc.get("scenarios", [])}
        scenario_checks = {s["id"]: s.get("checks") or [] for s in doc.get("scenarios", [])}
    except (OSError, json.JSONDecodeError):
        pass

    # Collect every launch directory: runs/, noise/, calib/.
    runs_by_key = {}
    for group in ("runs", "noise", "calib"):
        base = root / group
        if not base.is_dir():
            continue
        for directory in sorted(base.iterdir()):
            if not directory.is_dir():
                continue
            run = collect_run(directory)
            if run is None:
                continue
            name = directory.name
            if group == "calib":
                if name.startswith("runner-on"):
                    key0 = "calib-runner-on"
                elif name.startswith("runner-off"):
                    key0 = "calib-runner-off"
                elif name.startswith("sampler-"):
                    key0 = "calib-sampler-" + name[len("sampler-"):].rsplit("_r", 1)[0]
                else:
                    key0 = "calib-other"
            else:
                key0 = run["scenario"]
            runs_by_key[(key0, name)] = run

    measured_runs = [r for key, r in runs_by_key.items() if not key[0].startswith("calib-")]

    # Noise: per scenario, spread of each metric's peak across repeats.
    noise = {}
    scenarios = sorted({r["scenario"] for r in measured_runs if r["scenario"]})
    for scenario in scenarios:
        group = [r for r in measured_runs if r["scenario"] == scenario]
        entry = {}
        for metric, _unit, _desc in TRACKED:
            entry[metric] = scenario_spread(group, metric)
        noise[scenario] = entry

    calibration = build_calibration(root, runs_by_key)
    if any(entry and entry.get("spread") is not None
           for scenario_noise in noise.values() for entry in scenario_noise.values()):
        calibration["measured"]["noiseFloor"] = noise
    floor = derive_floor(calibration, noise)
    calibration["floor"] = floor

    manifest = {
        "schemaVersion": 1,
        "generatedUtc": __import__("datetime").datetime.now(
            __import__("datetime").timezone.utc).isoformat(),
        "issues": ["#1497"],
        "kind": "gui-perf-scenarios",
        "configuration": {
            "runDir": str(root),
            "scenarioFile": args.scenario_file,
            "meta": (root / "run-meta.txt").read_text() if (root / "run-meta.txt").exists() else "",
        },
        "runs": measured_runs,
        "calibrationRuns": [r for key, r in runs_by_key.items() if key[0].startswith("calib-")],
        "noise": noise,
        "floor": floor,
    }

    # Baseline comparison, if one was given.
    comparison = []
    if args.baseline:
        try:
            before = json.loads(Path(args.baseline).read_text())
        except (OSError, json.JSONDecodeError) as exc:
            print(f"baseline unreadable ({exc}); reporting without it", file=sys.stderr)
            before = None
        if before:
            before_by_scenario = {}
            for run in before.get("runs", []):
                before_by_scenario.setdefault(run["scenario"], []).append(run)
            for scenario in scenarios:
                now_group = [r for r in measured_runs if r["scenario"] == scenario]
                was_group = before_by_scenario.get(scenario, [])
                if not was_group:
                    comparison.append({"scenario": scenario, "verdict": "NEW",
                                       "note": "not present in the baseline"})
                    continue
                # This scenario's OWN noise, not the widest in the run (#4).
                scenario_floor = derive_floor(calibration, noise, scenario=scenario)
                for metric, unit, _desc in TRACKED:
                    now_peak = median([peak_and_final(r, metric)[0] for r in now_group])
                    was_peak = median([peak_and_final(r, metric)[0] for r in was_group])
                    delta = None if (now_peak is None or was_peak is None) else now_peak - was_peak
                    comparison.append({
                        "scenario": scenario, "metric": metric, "unit": unit,
                        "baseline": was_peak, "measured": now_peak, "delta": delta,
                        "floor": scenario_floor.get(metric, {}).get("value"),
                        "floorFrom": scenario_floor.get(metric, {}).get("from"),
                        "verdict": verdict(delta, scenario_floor.get(metric)),
                    })
            manifest["baseline"] = args.baseline
            manifest["comparison"] = comparison

    checks = {}
    for scenario in scenarios:
        if scenario_checks.get(scenario):
            group = [r for r in measured_runs if r["scenario"] == scenario]
            checks[scenario] = evaluate_checks(group, scenario_checks[scenario], calibration, noise, scenario)
    if checks:
        manifest["checks"] = checks

    (root / "run.json").write_text(json.dumps(manifest, indent=2, default=str))
    if calibration["measured"]:
        (root / "calibration.json").write_text(json.dumps(calibration, indent=2, default=str))

    # ------------------------------------------------------------ summary.tsv
    tsv = ["\t".join([
        "scenario", "repeat", "seq", "step", "op", "ok", "wallMs",
        "rssMB", "footprintMB", "committedMB", "liveHeapMB", "fragmentedMB",
        "continuousResidentMB", "cpuTotalMs", "gen2", "load1", "outerSampled",
        "openDocuments", "documentWindows", "note"])]
    for run in sorted(measured_runs, key=lambda r: (r["scenario"] or "", r["repeat"])):
        for step in run["steps"]:
            tsv.append("\t".join(str(x) for x in [
                run["scenario"], run["repeat"], step["seq"], step["step"], step["op"],
                step["ok"], fmt(step["wallMs"]),
                fmt(step["rssMB"]), fmt(step["footprintMB"]), fmt(step["committedMB"]),
                fmt(step["liveHeapMB"]), fmt(step["fragmentedMB"]),
                fmt(step["continuousResidentMB"]), fmt(step["cpuTotalMs"]),
                step["gen2Collections"], fmt(step["load1"], 2),
                step["outerSampled"], step.get("openDocuments"), step.get("documentWindows"),
                step["note"] or ""]))
    (root / "summary.tsv").write_text("\n".join(tsv) + "\n")

    # ------------------------------------------------------------- summary.md
    lines = ["# Live GUI performance scenarios (#1497)", ""]
    meta = manifest["configuration"]["meta"].strip()
    if meta:
        lines += ["```", meta, "```", ""]

    lines += [
        "## How to read this",
        "",
        "Every number below is a measurement on ONE machine under whatever load it",
        "had at the time (`load1` is recorded per row). Absolute footprint is not",
        "comparable across machines and is deliberately not gated anywhere.",
        "",
        "**What you CAN conclude from this output**",
        "",
        "- Whether the footprint after a close or replace returns toward the",
        "  pre-open floor — the `baseline` boundary is written before any step, so",
        "  \"comes back down\" has something to come back down to (#1461).",
        "- Whether a change moved a metric by more than this machine's measured",
        "  floor. Anything at or below the floor prints `BELOW-FLOOR`.",
        "- How the footprint splits between managed heap (`committedMB`,",
        "  `liveHeapMB`, `fragmentedMB`) and everything else — the #1496 question.",
        "- Band render p50/p99 and the tile-cache counters, per scenario.",
        "",
        "**What you CANNOT conclude**",
        "",
        "- Whether scrolling *feels* smooth. That stays human; p50/p99 and blank",
        "  tiles are proxies, not the thing.",
        "- That in-app driving equals a user's input path. It skips input dispatch",
        "  and hit testing. The driving-fidelity calibration BOUNDS that residual;",
        "  it does not remove it.",
        "- Anything about another machine, or about this one under different load.",
        "- That a `FLOOR-UNKNOWN` delta is real. It means nobody measured the noise.",
        "",
    ]

    lines += ["## Noise floor", ""]
    if any(v for n in noise.values() for v in n.values()):
        lines += ["| scenario | metric | n | median | min | max | spread |",
                  "|---|---|---:|---:|---:|---:|---:|"]
        for scenario in scenarios:
            for metric, unit, _desc in TRACKED:
                entry = noise[scenario].get(metric)
                if not entry:
                    continue
                lines.append(
                    f"| `{scenario}` | {metric} | {entry['n']} | {fmt(entry['median'])} | "
                    f"{fmt(entry['min'])} | {fmt(entry['max'])} | {fmt(entry['spread'])} |")
        lines.append("")
    else:
        lines += ["Only one repeat per scenario, so there is no spread to report.",
                  "Run with `--repeats 5` (or `--calibrate`) to measure a noise floor.",
                  ""]

    lines += ["## The floor every delta is judged against", "",
              "`floor = max(noise spread, runner overhead, sampler overhead)`", "",
              "| metric | floor | unit | from |", "|---|---:|---|---|"]
    for metric, unit, desc in TRACKED:
        entry = floor.get(metric) or {}
        value = entry.get("value")
        lines.append(
            f"| {metric} — {desc} | {'UNKNOWN' if value is None else fmt(value)} | "
            f"{unit} | {entry.get('from', 'UNKNOWN')} |")
    lines.append("")
    for metric, _unit, _desc in TRACKED:
        note = (floor.get(metric) or {}).get("note")
        if note:
            lines.append(f"- `{metric}`: {note}")
    if any((floor.get(m) or {}).get("note") for m, _u, _d in TRACKED):
        lines.append("")
    if all((floor.get(m) or {}).get("value") is None for m, _u, _d in TRACKED):
        lines += ["> ⚠️ **No floor was measured in this run, so no delta here is",
                  "> evidence of anything.** Run `--calibrate` first. A missing floor",
                  "> is reported as UNKNOWN rather than assumed to be zero, because",
                  "> assuming zero is exactly how a sub-noise change gets called a win.",
                  ""]

    lines += ["## Calibration", ""]
    for name, text in calibration["method"].items():
        measured = calibration["measured"].get(name)
        state = "measured in this run" if measured else "NOT measured in this run"
        lines += [f"### {name} — {state}", "", text, ""]
        if name == "runnerOverhead" and measured:
            lines += ["| metric | runner ON | runner OFF | delta | unit | |",
                      "|---|---:|---:|---:|---|---|"]
            inverted = []
            for metric, entry in measured.items():
                # A NEGATIVE delta means the control burned more than the
                # instrumented run — so the control is not a control. The
                # realistic cause is background work (thumbnail prewarm, the
                # text index) that the scenario's waitIdle happened to let
                # finish and the plain launch did not. The floor uses abs(),
                # which would quietly bank that as "harness cost", so say it.
                flag = ""
                if entry.get("delta") is not None and entry["delta"] < 0:
                    flag = "⚠️ control burned MORE"
                    inverted.append(metric)
                lines.append(
                    f"| {metric} | {fmt(entry['runnerOnMedian'])} | "
                    f"{fmt(entry['runnerOffMedian'])} | {fmt(entry['delta'])} | "
                    f"{entry['unit']} | {flag} |")
            lines.append("")
            if inverted:
                lines += [
                    f"> ⚠️ **Runner overhead came out NEGATIVE for "
                    f"{', '.join(inverted)}** — the uninstrumented control used more "
                    "than the instrumented run. That is not a harness cost; it means the "
                    "two launches did not do the same work. Most likely the control was "
                    "still finishing background work (thumbnail prewarm, the text index) "
                    "that the scenario's wait-for-idle absorbed. The floor takes the "
                    "absolute value, so this inflates the floor rather than shrinking it "
                    "— conservative, but it is measuring the wrong thing. Re-run the "
                    "control with a matched hold before quoting this number.",
                    ""]
        if name == "samplerOverhead" and measured:
            lines += ["| sample mode | metric | median | unit |", "|---|---|---:|---|"]
            for mode, metrics in measured.items():
                for metric, entry in metrics.items():
                    lines.append(
                        f"| {mode} | {metric} | {fmt(entry['median'])} | {entry['unit']} |")
            lines.append("")
        if name == "drivingFidelity":
            lines += [
                "To measure it, run the in-app side and then drive the same step by",
                "real keyboard input through computer-use, and compare `bandRenders`,",
                "`bandRenderP50Ms`/`P99` and peak `footprintMB`:",
                "",
                "```bash",
                "scripts/run-gui-perf-scenarios.sh --scenario irs-page30 --repeats 1",
                "# then, with the app launched by hand and EXCISE_TRACE_VIEWER set,",
                "# send 30 Page Down keystrokes and compare the same three numbers.",
                "```",
                "",
            ]

    lines += ["## Scenarios", ""]
    for scenario in scenarios:
        group = [r for r in measured_runs if r["scenario"] == scenario]
        lines += [f"### `{scenario}`", ""]
        why = scenario_why.get(scenario)
        if why:
            lines += [f"_{why}_", ""]

        failures = sum(r["failures"] for r in group)
        unsampled = sum(r["boundaries"] - r["boundariesSampled"] for r in group)
        lines.append(
            f"{len(group)} run(s), {failures} step failure(s), "
            f"{unsampled} boundary(ies) with no outer sample.")
        if unsampled:
            lines.append("")
            lines.append(
                "> Boundaries with no outer sample have no `footprintMB`/`rssMB` beside "
                "them. They are left EMPTY rather than filled from a neighbouring "
                "sample — a plausible-looking wrong number is worse than a gap.")
        lines.append("")

        representative = group[0]
        # Documents/windows only where a journal recorded them with more than
        # one document open at some boundary: the single-document tables keep
        # their shape.
        multi = any((s.get("openDocuments") or 0) > 1 for s in representative["steps"])
        docs_head, docs_rule = (" docs/windows |", "---|") if multi else ("", "")
        lines += ["| step | op | wall ms | footprint MB | rss MB | committed MB | "
                  "live MB | frag MB | tiles MB | gen2 |" + docs_head + " ok |",
                  "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|" + docs_rule + "---|"]
        for step in representative["steps"]:
            docs_cell = (f" {step.get('openDocuments')}/{step.get('documentWindows')} |" if multi else "")
            lines.append(
                f"| {step['step']} | {step['op']} | {fmt(step['wallMs'])} | "
                f"{fmt(step['footprintMB'])} | {fmt(step['rssMB'])} | "
                f"{fmt(step['committedMB'])} | {fmt(step['liveHeapMB'])} | "
                f"{fmt(step['fragmentedMB'])} | {fmt(step['continuousResidentMB'])} | "
                f"{step['gen2Collections']} |{docs_cell} {'yes' if step['ok'] else 'NO'} |")
        lines.append("")

        for result in checks.get(scenario, []):
            check = result["check"]
            if result["verdict"] == "UNKNOWN-CHECK":
                lines += [f"> ⚠️ Unknown check kind `{check.get('kind')}`; nothing was evaluated.", ""]
                continue
            flag = "" if result["verdict"].startswith("DROPPED") and "(" not in result["verdict"] else "⚠️ "
            lines += [
                f"{flag}**Check `{check['kind']}` {check['metric']}**: `{check['from']}` "
                f"{fmt(result['from'])} -> `{check['to']}` {fmt(result['to'])} "
                f"(median delta {fmt(result['delta'])} over {result['runs']} run(s), floor "
                f"{'UNKNOWN' if result['floor'] is None else fmt(result['floor'])}): "
                f"**{result['verdict']}**. _{check.get('why', '')}_", ""]

        for step in representative["steps"]:
            if step["note"]:
                lines.append(f"- `{step['step']}`: {step['note']}")
        if any(s["note"] for s in representative["steps"]):
            lines.append("")

        warning = never_rendered(representative)
        if warning:
            lines += [
                f"> 🚨 **THIS RUN DID NOT MEASURE WHAT IT CLAIMS.** It {warning}.",
                ">",
                "> Every number below is real, and every number below is about the wrong",
                "> thing. Do not quote them. Check that the open path actually drives the",
                "> viewer before re-running.",
                ""]

        # Did it come back down? The question #1461 is about.
        base_fp = baseline_value(representative, "footprintMB")
        peak_fp, final_fp = peak_and_final(representative, "footprintMB")
        if base_fp is not None and peak_fp is not None and final_fp is not None:
            retained = final_fp - base_fp
            lines += [
                f"Footprint: baseline {fmt(base_fp)} MB -> peak {fmt(peak_fp)} MB -> "
                f"final {fmt(final_fp)} MB (**{fmt(retained)} MB above baseline "
                f"at the end**).", ""]

        merged_metrics = representative.get("metrics") or {}
        if merged_metrics:
            bits = [f"{k}={fmt(v) if isinstance(v, float) else v}"
                    for k, v in sorted(merged_metrics.items())]
            lines += ["Render/trim metrics (from the #1491 sink): " + ", ".join(bits), ""]

    if comparison:
        lines += ["## Against the baseline", "",
                  f"Baseline: `{args.baseline}`", "",
                  "| scenario | metric | baseline | now | delta | floor | verdict |",
                  "|---|---|---:|---:|---:|---:|---|"]
        for row in comparison:
            if "metric" not in row:
                lines.append(f"| `{row['scenario']}` | - | - | - | - | - | {row['verdict']} |")
                continue
            lines.append(
                f"| `{row['scenario']}` | {row['metric']} | {fmt(row['baseline'])} | "
                f"{fmt(row['measured'])} | {fmt(row['delta'])} | "
                f"{'UNKNOWN' if row['floor'] is None else fmt(row['floor'])} | "
                f"{row['verdict']} |")
        lines += ["",
                  "`BELOW-FLOOR` means the change is smaller than this machine's",
                  "measured noise plus harness cost — it is **not** a win, and the",
                  "stopping rule says that when the best remaining candidate in an",
                  "area lands here, stop optimising that area and say so on its issue.",
                  ""]

    (root / "summary.md").write_text("\n".join(lines) + "\n")

    # ------------------------------------------------------------ console tail
    print(f"runs summarised   : {len(measured_runs)}")
    print(f"calibration runs  : {len(manifest['calibrationRuns'])}")
    for metric, unit, _desc in TRACKED:
        entry = floor.get(metric) or {}
        value = entry.get("value")
        print(f"floor {metric:<14}: "
              f"{'UNKNOWN' if value is None else fmt(value) + ' ' + unit} "
              f"({entry.get('from', 'UNKNOWN')})")
    for run in measured_runs:
        warning = never_rendered(run)
        if warning:
            print(f"!! {run['scenario']}: {warning}")

    for scenario, results in checks.items():
        for result in results:
            if not result["verdict"].startswith("DROPPED") or "(" in result["verdict"]:
                print(f"!! {scenario}: check {result['check'].get('kind')} "
                      f"{result['check'].get('metric')}: {result['verdict']}")

    total_failures = sum(r["failures"] for r in measured_runs)
    if total_failures:
        print(f"step failures     : {total_failures}  (see summary.md)")
    print(f"summary           : {root / 'summary.md'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

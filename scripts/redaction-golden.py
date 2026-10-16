#!/usr/bin/env python3
"""Cached reference redactions: record the CLI's outputs once, then check a changed engine against them without
re-running the old one.

    scripts/redaction-golden.py record --cli logs/aot-perf/jit/excise --label before-2075
    scripts/redaction-golden.py check  --cli logs/aot-perf/jit-new/excise --label before-2075

`record` redacts every (document, term, mode) of the matrix and stores the SHA-256 of each output and the exit code
under logs/redaction-golden/<label>.json (untracked: it depends on the local smoke corpus). `check` runs only the
CLI under test, in parallel, and reports each cell that differs in bytes or exit code. A performance change to the
redaction engine must leave every cell identical; a change that is meant to alter output re-records first.
The matrix covers the default (widened box), --fixed-marker (box drawn) and --close-width (no box), because the box
drawing and the writer are the code the speed work touches. This is a regression check against the previous
engine, not an independent oracle: the leak oracles (SavedPdfLeakScanner, mutool) stay in the redaction suites.
"""
import argparse, concurrent.futures as cf, hashlib, json, os, pathlib, subprocess, sys, tempfile, time

ROOT = pathlib.Path(__file__).resolve().parent.parent
GOLD = ROOT / "logs/redaction-golden"
DOCS = ["irs-w9", "irs-1040", "state-ds11-passport", "scotus-trump-v-us", "irs-1040-instructions"]
TERMS = ["Social", "the"]
MODES = {"default": [], "fixed-marker": ["--fixed-marker"], "close-width": ["--close-width"]}


def cells():
    return [(d, t, m) for d in DOCS for t in TERMS for m in MODES]


def one(cli, cell, scratch):
    d, t, m = cell
    out = f"{scratch}/{d}-{t}-{m}.pdf"
    started = time.time()
    p = subprocess.run([cli, "redact", str(ROOT / f"test-pdfs/smoke/{d}.pdf"), out, t, *MODES[m]], capture_output=True)
    h = hashlib.sha256(open(out, "rb").read()).hexdigest() if os.path.exists(out) else None
    if h:
        os.unlink(out)
    return "|".join(cell), {"rc": p.returncode, "sha256": h, "seconds": round(time.time() - started, 1)}


def run_matrix(cli, jobs):
    scratch = tempfile.mkdtemp(prefix="redact-golden-")
    cli = str(pathlib.Path(cli).resolve())
    # slowest documents first so the pool stays busy
    order = sorted(cells(), key=lambda c: -DOCS.index(c[0]) if c[0] == "irs-1040-instructions" else DOCS.index(c[0]))
    with cf.ThreadPoolExecutor(jobs) as ex:
        return dict(ex.map(lambda c: one(cli, c, scratch), order))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("mode", choices=["record", "check"])
    ap.add_argument("--cli", required=True)
    ap.add_argument("--label", required=True)
    ap.add_argument("--jobs", type=int, default=4)
    a = ap.parse_args()
    path = GOLD / f"{a.label}.json"
    started = time.time()
    got = run_matrix(a.cli, a.jobs)
    if a.mode == "record":
        GOLD.mkdir(parents=True, exist_ok=True)
        commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
        path.write_text(json.dumps({"commit": commit, "cli": a.cli, "cells": got}, indent=1))
        print(f"recorded {len(got)} cells to {path} in {time.time() - started:.0f} s")
        return
    ref = json.loads(path.read_text())["cells"]
    bad = [(k, ref.get(k), v) for k, v in got.items() if ref.get(k, {}).get("sha256") != v["sha256"] or ref.get(k, {}).get("rc") != v["rc"]]
    print(f"{len(got) - len(bad)}/{len(got)} cells identical to the recorded reference ({time.time() - started:.0f} s)")
    for k, r, g in bad:
        print(f"DIFFERENT {k}: reference rc={r and r['rc']} {r and r['sha256'] and r['sha256'][:12]}  now rc={g['rc']} {g['sha256'] and g['sha256'][:12]}")
    ref_s, new_s = sum(c["seconds"] for c in ref.values()), sum(c["seconds"] for c in got.values())
    print(f"summed redaction seconds: reference {ref_s:.0f}, now {new_s:.0f} (parallel jobs inflate both)")
    sys.exit(1 if bad else 0)


main()

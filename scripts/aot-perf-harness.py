#!/usr/bin/env python3
"""Headless performance harness for the excise CLI under NativeAOT (and, for comparison, ReadyToRun and JIT).

No window, no input, nothing that can take focus: every measurement is a fresh CLI process. Run it under `nice`.

    scripts/aot-perf-harness.py build  --out logs/aot-perf            # publish the three shapes
    scripts/aot-perf-harness.py run    --out logs/aot-perf --runs 5   # all workloads x documents x shapes
    scripts/aot-perf-harness.py profile --shape aot --workload redact --doc state-ds11-passport.pdf --out logs/aot-perf
    scripts/aot-perf-harness.py compare logs/aot-perf/run-A logs/aot-perf/run-B   # two runs, per-cell ratio

What `run` records per (document, workload, shape), median of --runs after a discarded warm-up, shapes interleaved:
wall, CPU, peak RSS (from /usr/bin/time -l), and what only the process can report through EXCISE_PERF_REPORT
(Excise.Cli/ProcessPerfReport.cs): managed allocation bytes, GC collections per generation and total GC pause.
Output bytes are recorded so a shape that writes a different file is visible. `profile` samples the CLI with macOS
`sample` (symbols resolve in the AOT binary) and prints the heaviest Excise frames, inclusive.
The NativeAOT shape carries the allocation numbers the engine workflow budgets cannot give (RenderTools is JIT-only).
"""
import argparse, json, os, pathlib, re, statistics as st, subprocess, sys, tempfile, time

ROOT = pathlib.Path(__file__).resolve().parent.parent
CORPUS = ROOT / "test-pdfs/smoke"
DOCS = ["irs-w9.pdf", "irs-1040.pdf", "state-ds11-passport.pdf", "irs-1040-instructions.pdf", "scotus-trump-v-us.pdf"]
OK_RC = {"redact": (0, 3)}          # redact exits 3 for a redaction that is not clean: the work was done and timed
WORKLOADS = {
    "info":     lambda b, pdf, out: [b, "info", pdf],
    "text":     lambda b, pdf, out: [b, "text", pdf],
    "render":   lambda b, pdf, out: [b, "render", pdf, "-o", f"{out}.png", "--dpi", "150"],
    "redact":   lambda b, pdf, out: [b, "redact", pdf, f"{out}.pdf", "Social"],
    "optimize": lambda b, pdf, out: [b, "optimize", pdf, f"{out}.pdf"],
    "ocr":      lambda b, pdf, out: [b, "ocr", pdf, "--page", "1"],
}
SHAPE_BIN = {"jit": "jit/excise", "r2r": "r2r/excise", "aot": "aot/excise"}
TIME_RE = {"wall": r"^\s*([\d.]+) real", "rss": r"^\s*(\d+)\s+maximum resident set size"}


def sh(cmd, **kw):
    return subprocess.run(cmd, cwd=ROOT, text=True, capture_output=True, **kw)


def build(a):
    out = pathlib.Path(a.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    steps = [
        ("aot", ["scripts/build-aot-cli.sh", "--quiet", "--output", str(out / "aot")]),
        ("r2r", ["dotnet", "publish", "Excise.Cli/Excise.Cli.csproj", "-c", "Release", "-r", "osx-arm64", "--self-contained", "true",
                 "-p:PublishReadyToRun=true", "-o", str(out / "r2r"), "--nologo", "-v", "quiet"]),
        ("jit", ["dotnet", "publish", "Excise.Cli/Excise.Cli.csproj", "-c", "Release", "-r", "osx-arm64", "--self-contained", "false", "-p:PublishReadyToRun=false", "-o", str(out / "jit"), "--nologo", "-v", "quiet"]),
    ]
    for name, cmd in steps:
        print(f"building {name} ...", flush=True)
        r = sh(["nice", *cmd])
        print(f"  rc={r.returncode}", r.stderr[-300:] if r.returncode else "")
        if r.returncode:
            sys.exit(1)
    meta = {"commit": sh(["git", "rev-parse", "HEAD"]).stdout.strip(), "built": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    (out / "shapes.json").write_text(json.dumps(meta))


def run_once(cmd, out_stem, report):
    if os.path.exists(report):
        os.unlink(report)
    env = dict(os.environ, EXCISE_PERF_REPORT=report)
    p = subprocess.run(["/usr/bin/time", "-l", *cmd], capture_output=True, text=True, env=env)
    m = {}
    for k, rx in TIME_RE.items():
        g = re.search(rx, p.stderr, re.M)
        if g:
            m[k] = float(g.group(1))
    try:
        m.update(json.load(open(report)))
    except Exception:
        pass
    size = sum(f.stat().st_size for f in pathlib.Path(out_stem).parent.glob(pathlib.Path(out_stem).name + ".*"))
    for f in pathlib.Path(out_stem).parent.glob(pathlib.Path(out_stem).name + ".*"):
        f.unlink()
    return p.returncode, m, size


def run(a):
    shapes_dir = pathlib.Path(a.shapes or a.out).resolve()
    shapes = {s: str(shapes_dir / SHAPE_BIN[s]) for s in a.shape}
    missing = [s for s, b in shapes.items() if not os.path.exists(b)]
    if missing:
        sys.exit(f"missing shapes {missing}: run `build` first (or pass --shapes DIR)")
    run_dir = pathlib.Path(a.out) / (a.name or time.strftime("run-%Y%m%d-%H%M%S"))
    run_dir.mkdir(parents=True, exist_ok=True)
    jl = run_dir / "rows.jsonl"
    rows = [json.loads(l) for l in jl.read_text().splitlines()] if jl.exists() else []       # resumable
    done = {(r["doc"], r["workload"]) for r in rows}
    scratch = tempfile.mkdtemp(prefix="aot-perf-")
    load = os.getloadavg()[0]
    (run_dir / "meta.json").write_text(json.dumps({"commit": sh(["git", "rev-parse", "HEAD"]).stdout.strip(), "load1AtStart": round(load, 2),
                                                   "runs": a.runs, "shapes": a.shape, "docs": a.docs, "workloads": a.workload}))
    for doc in a.docs:
        pdf = str(CORPUS / doc)
        if not os.path.exists(pdf):
            continue
        for wl in a.workload:
            if (doc, wl) in done:
                continue
            samples = {s: [] for s in shapes}
            sizes, first = {}, len(rows)
            for i in range(a.runs + 1):                      # run 0 is a warm-up (disk cache), discarded
                for s, b in shapes.items():
                    stem = f"{scratch}/{s}-{wl}"
                    rc, m, size = run_once(WORKLOADS[wl](b, pdf, stem), stem, f"{scratch}/{s}.report.json")
                    sizes[s] = size
                    if i and rc in OK_RC.get(wl, (0,)) and m:
                        samples[s].append(m)
            for s in shapes:
                ms = samples[s]
                if not ms:
                    rows.append({"doc": doc, "workload": wl, "shape": s, "failed": True})
                    continue
                med = lambda k: st.median(x[k] for x in ms if k in x) if any(k in x for x in ms) else None
                rows.append({"doc": doc, "workload": wl, "shape": s, "runs": len(ms), "wallS": med("wall"),
                             "cpuS": round(med("cpuMs") / 1000, 3) if med("cpuMs") is not None else None,
                             "rssMB": round(med("rss") / 2**20, 1), "allocMB": round(med("allocatedBytes") / 2**20, 1) if med("allocatedBytes") is not None else None,
                             "gcGen2": st.median(x["gcCollections"][2] for x in ms) if "gcCollections" in ms[0] else None,
                             "gcPauseMs": round(med("gcPauseMs"), 1) if med("gcPauseMs") is not None else None,
                             "outBytes": sizes[s]})
            with jl.open("a") as f:
                for r in rows[first:]:
                    f.write(json.dumps(r) + "\n")
            print(doc, wl, {s: next((r.get("wallS") for r in rows[first:] if r["shape"] == s), None) for s in shapes}, flush=True)
    summarize(run_dir)


def summarize(run_dir):
    run_dir = pathlib.Path(run_dir)
    rows = [json.loads(l) for l in (run_dir / "rows.jsonl").read_text().splitlines()]
    shapes = list(dict.fromkeys(r["shape"] for r in rows))
    cell = lambda r: "FAILED" if r.get("failed") else f"{r['wallS']:.2f} s / cpu {r['cpuS']} / RSS {r['rssMB']} / alloc {r['allocMB']} MB / gen2 {r['gcGen2']} / pause {r['gcPauseMs']} ms"
    lines = [f"# CLI performance by build shape ({run_dir.name})", "",
             json.dumps(json.loads((run_dir / "meta.json").read_text())), "",
             "| doc | workload | " + " | ".join(shapes) + " | outputs equal |", "|---|---|" + "---|" * (len(shapes) + 1)]
    for d in dict.fromkeys(r["doc"] for r in rows):
        for w in dict.fromkeys(r["workload"] for r in rows if r["doc"] == d):
            rs = {r["shape"]: r for r in rows if r["doc"] == d and r["workload"] == w}
            eq = len({rs[s].get("outBytes") for s in shapes if s in rs and not rs[s].get("failed")}) == 1
            lines.append(f"| {d} | {w} | " + " | ".join(cell(rs[s]) if s in rs else "-" for s in shapes) + f" | {'yes' if eq else 'NO'} |")
    (run_dir / "summary.md").write_text("\n".join(lines) + "\n")
    print(f"wrote {run_dir}/summary.md")


def compare(a):
    def load(p):
        return {(r["doc"], r["workload"], r["shape"]): r for r in (json.loads(l) for l in (pathlib.Path(p) / "rows.jsonl").read_text().splitlines()) if not r.get("failed")}
    A, B = load(a.a), load(a.b)
    print("| doc | workload | shape | wall B/A | cpu B/A | RSS B/A | alloc B/A |\n|---|---|---|---:|---:|---:|---:|")
    ratio = lambda x, y: "-" if not x or not y else f"{y / x:.2f}"
    for k in sorted(set(A) & set(B)):
        print(f"| {k[0]} | {k[1]} | {k[2]} | {ratio(A[k]['wallS'], B[k]['wallS'])} | {ratio(A[k]['cpuS'], B[k]['cpuS'])} | {ratio(A[k]['rssMB'], B[k]['rssMB'])} | {ratio(A[k]['allocMB'], B[k]['allocMB'])} |")


def profile(a):
    b = str(pathlib.Path(a.shapes or a.out).resolve() / SHAPE_BIN[a.shape])
    stem = tempfile.mkdtemp(prefix="aot-prof-") + "/o"
    cmd = WORKLOADS[a.workload](b, str(CORPUS / a.doc), stem)
    proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(a.delay)
    sample = pathlib.Path(a.out) / f"sample-{a.shape}-{a.workload}-{a.doc}.txt"
    subprocess.run(["sample", str(proc.pid), str(a.seconds), "-file", str(sample)], capture_output=True)
    proc.wait()
    text = sample.read_text().split("Call graph:")[1].split("Total number in stack")[0]
    inc = {}
    for l in text.splitlines():
        m = re.match(r"^([ +!:|]*)(\d+) (\S+)", l)
        if m and m.group(3).lower().startswith("excise"):
            inc[m.group(3)] = max(inc.get(m.group(3), 0), int(m.group(2)))
    print(f"heaviest Excise frames (inclusive samples, {a.seconds}s at 1 ms) from {sample}:")
    for n, c in sorted(inc.items(), key=lambda x: -x[1])[:a.top]:
        print(f"{c:7d} {n[:140]}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = ap.add_subparsers(dest="cmd", required=True)
    b = sp.add_parser("build"); b.add_argument("--out", required=True); b.set_defaults(f=build)
    r = sp.add_parser("run"); r.add_argument("--out", required=True); r.add_argument("--shapes"); r.add_argument("--name")
    r.add_argument("--runs", type=int, default=5); r.add_argument("--shape", action="append", default=None)
    r.add_argument("--doc", dest="docs", action="append", default=None); r.add_argument("--workload", action="append", default=None)
    r.set_defaults(f=run)
    p = sp.add_parser("profile"); p.add_argument("--out", required=True); p.add_argument("--shapes"); p.add_argument("--shape", default="aot")
    p.add_argument("--workload", required=True); p.add_argument("--doc", required=True)
    p.add_argument("--delay", type=float, default=1.0); p.add_argument("--seconds", type=int, default=5); p.add_argument("--top", type=int, default=30)
    p.set_defaults(f=profile)
    c = sp.add_parser("compare"); c.add_argument("a"); c.add_argument("b"); c.set_defaults(f=compare)
    a = ap.parse_args()
    if a.cmd == "run":
        a.shape = a.shape or ["jit", "r2r", "aot"]
        a.docs = a.docs or DOCS
        a.workload = a.workload or ["info", "text", "render", "redact", "optimize"]
    a.f(a)


main()

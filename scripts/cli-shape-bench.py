#!/usr/bin/env python3
"""Time the excise CLI workflows under each build shape (JIT build output, ReadyToRun publish, NativeAOT publish).

    scripts/cli-shape-bench.py --out logs/cli-shapes/result \
        --shape jit=Excise.Cli/bin/Release/net10.0/excise \
        --shape r2r=logs/cli-shapes/r2r/excise --shape aot=logs/cli-shapes/aot/excise

Every command is a fresh process (/usr/bin/time -l): wall, user+sys CPU and peak RSS, median of --runs, runs interleaved
across shapes so drift cancels. Output sizes are recorded so a shape that writes different bytes is visible (#1389: the
Homebrew SDK did). No window, no input: safe to run while the machine is in use (run it under `nice`).
"""
import argparse, json, os, pathlib, re, statistics as st, subprocess, sys, tempfile

CORPUS = "test-pdfs/smoke"
DOCS = ["irs-w9.pdf", "irs-1040.pdf", "state-ds11-passport.pdf", "irs-1040-instructions.pdf", "scotus-trump-v-us.pdf"]
WORKLOADS = {
    "info":     lambda b, pdf, out: [b, "info", pdf],
    "text":     lambda b, pdf, out: [b, "text", pdf],
    "render":   lambda b, pdf, out: [b, "render", pdf, "-o", f"{out}.png", "--dpi", "150"],
    "redact":   lambda b, pdf, out: [b, "redact", pdf, f"{out}.pdf", "Social"],
    "optimize": lambda b, pdf, out: [b, "optimize", pdf, f"{out}.pdf"],
}
TIME_RE = {
    "wall": re.compile(r"^\s*([\d.]+) real", re.M), "user": re.compile(r"([\d.]+) user"),
    "sys": re.compile(r"([\d.]+) sys"), "rss": re.compile(r"^\s*(\d+)\s+maximum resident set size", re.M),
}

def run_once(cmd, out):
    p = subprocess.run(["/usr/bin/time", "-l", *cmd], capture_output=True, text=True)
    m = {k: float(r.search(p.stderr).group(1)) for k, r in TIME_RE.items() if r.search(p.stderr)}
    size = sum(f.stat().st_size for f in pathlib.Path(out).parent.glob(pathlib.Path(out).name + ".*"))
    for f in pathlib.Path(out).parent.glob(pathlib.Path(out).name + ".*"): f.unlink()
    return p.returncode, m, size

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shape", action="append", required=True, help="name=path-to-excise-binary")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    shapes = dict(s.split("=", 1) for s in a.shape)
    os.makedirs(a.out, exist_ok=True)
    scratch = tempfile.mkdtemp(prefix="cli-shape-")
    # Resumable: finished (doc, workload) cells are appended to rows.jsonl and skipped on a rerun, so a stopped
    # run (machine restart) continues instead of starting over.
    jl = pathlib.Path(a.out, "rows.jsonl")
    rows = [json.loads(l) for l in jl.read_text().splitlines()] if jl.exists() else []
    done = {(r["doc"], r["workload"]) for r in rows}
    for doc in DOCS:
        pdf = f"{CORPUS}/{doc}"
        if not os.path.exists(pdf): continue
        for wl, mk in WORKLOADS.items():
            if (doc, wl) in done: continue
            first = len(rows)
            samples = {s: [] for s in shapes}
            sizes, rcs = {}, {}
            for i in range(a.runs + 1):                  # run 0 is a warm-up (disk cache), discarded
                for s, b in shapes.items():
                    out = f"{scratch}/{s}-{wl}"
                    rc, m, size = run_once(mk(os.path.abspath(b), pdf, out), out)
                    rcs[s] = rc; sizes[s] = size
                    if i and rc == 0 and m: samples[s].append(m)
            for s in shapes:
                ms = samples[s]
                if not ms: rows.append({"doc": doc, "workload": wl, "shape": s, "failed": True, "rc": rcs[s]}); continue
                med = lambda k: round(st.median(x[k] for x in ms), 3)
                rows.append({"doc": doc, "workload": wl, "shape": s, "runs": len(ms), "wallS": med("wall"),
                             "cpuS": round(st.median(x["user"] + x["sys"] for x in ms), 3),
                             "rssMB": round(st.median(x["rss"] for x in ms) / 2**20, 1), "outBytes": sizes[s]})
            with jl.open("a") as f:
                for r in rows[first:]: f.write(json.dumps(r) + "\n")
            print(doc, wl, {s: next((r["wallS"] for r in rows[first:] if r["shape"] == s and "wallS" in r), None) for s in shapes}, flush=True)
    json.dump(rows, open(f"{a.out}/rows.json", "w"), indent=1)
    names = list(shapes)
    with open(f"{a.out}/summary.md", "w") as f:
        f.write("| doc | workload | " + " | ".join(f"{n} wall s / cpu s / RSS MB" for n in names) + " | out bytes equal |\n|---|---|" + "---|" * (len(names) + 1) + "\n")
        for doc in DOCS:
            for wl in WORKLOADS:
                rs = {r["shape"]: r for r in rows if r["doc"] == doc and r["workload"] == wl}
                if not rs: continue
                cells = [f"{rs[n]['wallS']} / {rs[n]['cpuS']} / {rs[n]['rssMB']}" if n in rs and "wallS" in rs[n] else "FAILED" for n in names]
                eq = len({rs[n].get("outBytes") for n in names if n in rs and "wallS" in rs[n]}) == 1
                f.write(f"| {doc} | {wl} | " + " | ".join(cells) + f" | {'yes' if eq else 'NO'} |\n")
    print(f"wrote {a.out}/summary.md")

main()

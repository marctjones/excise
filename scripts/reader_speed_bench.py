#!/usr/bin/env python3
"""Responsiveness benchmark: excise vs Preview vs Adobe Acrobat (#1544).

TOOLING, not a gate (tests/gates-tooling.txt). The companion of
reader_bench.py (#1543, memory/CPU), sharing its launch, window and
verification machinery, and its fairness rules: same inputs, same window, a
fresh copy of each document, every app launched with `open -n -F`.

WHAT IT MEASURES: what a user SEES. tools/screen-probe records the app's window
only when its content changes, stamped with the display time. This driver
posts each input with CGEventPost and stamps it with mach_absolute_time, so the
two line up exactly (AppleScript keystrokes jitter by 50-100 ms: too coarse).

Per input event, over the app's PAGE AREA only (toolbars, banners and the
Acrobat page box are outside it):
  firstChangeMs   input -> first frame that differs from the frame before it
  drawnMs         input -> last frame that still differs from the settled one
  speedIndexMs    area under (1 - visual completeness), the web-perf measure
Per scroll: frames delivered per second, frame-gap p95, hitches (gaps longer
than two 60 Hz frames while input is still arriving), and the settle tail.

    scripts/reader_speed_bench.py --list
    scripts/reader_speed_bench.py --apps excise --docs irs --repeats 1
    scripts/reader_speed_bench.py                  # all apps, bench.json docs
    scripts/reader_speed_bench.py --calibrate      # probe cost + probe/internal agreement
    scripts/reader_speed_bench.py --analyze logs/reader-speed_<stamp>
"""
import argparse, ctypes, json, os, pathlib, statistics, struct, subprocess, sys, threading, time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import reader_bench as rb  # noqa: E402

ROOT = rb.ROOT
PROBE = ROOT / "tools/screen-probe/bin/screen-probe"
SPEED_DOCS = ["irs", "altona", "scan"]
PAGE_TURNS = 20
TURN_GAP_S = 1.5
BURST = 10
BURST_GAP_S = 0.05
SCROLL_EVENTS = 180          # 3 s at 60 Hz
SCROLL_PX = 12
FRAME_S = 1 / 60

# The page area of each app's 1200x800 window, in points: toolbars, sidebars,
# Acrobat's "long document" banner and page box, and excise's status bar are
# all outside it, so their changes cannot read as page rendering. Measured from
# window screenshots on 2026-09-16.
PAGE_REGION = {
    "excise":  (219, 90, 951, 753),
    "preview": (215, 52, 1195, 795),
    "acrobat": (352, 120, 752, 795),
}

_libc = ctypes.CDLL(None)
_libc.mach_absolute_time.restype = ctypes.c_uint64


class _Timebase(ctypes.Structure):
    _fields_ = [("numer", ctypes.c_uint32), ("denom", ctypes.c_uint32)]


_tb = _Timebase()
_libc.mach_timebase_info(ctypes.byref(_tb))


def mach_now():
    return _libc.mach_absolute_time()


def ticks_to_ms(t):
    return t * _tb.numer / _tb.denom / 1e6


# ------------------------------------------------------------------ input

def post_key(code, mods=()):
    import Quartz
    flags = 0
    if "option" in mods:
        flags |= Quartz.kCGEventFlagMaskAlternate
    if "command" in mods:
        flags |= Quartz.kCGEventFlagMaskCommand
    down = Quartz.CGEventCreateKeyboardEvent(None, code, True)
    up = Quartz.CGEventCreateKeyboardEvent(None, code, False)
    if flags:
        Quartz.CGEventSetFlags(down, flags)
        Quartz.CGEventSetFlags(up, flags)
    t = mach_now()
    Quartz.CGEventPost(Quartz.kCGHIDEventTap, down)
    Quartz.CGEventPost(Quartz.kCGHIDEventTap, up)
    return t


def move_pointer(x, y):
    import Quartz
    Quartz.CGEventPost(Quartz.kCGHIDEventTap,
                       Quartz.CGEventCreateMouseEvent(None, Quartz.kCGEventMouseMoved, (x, y), 0))


def post_scroll(px):
    import Quartz
    e = Quartz.CGEventCreateScrollWheelEvent(None, Quartz.kCGScrollEventUnitPixel, 1, -px)
    t = mach_now()
    Quartz.CGEventPost(Quartz.kCGHIDEventTap, e)
    return t


# ------------------------------------------------------------------ probe

class Probe:
    def __init__(self, pid, out):
        if not PROBE.exists():
            raise rb.RunFailed(f"no probe at {PROBE}; run scripts/build-screen-probe.sh")
        self.out = out
        self.proc = subprocess.Popen([str(PROBE), "--pid", str(pid), "--out", str(out)],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.events = []
        self.ready = threading.Event()
        threading.Thread(target=self._read, daemon=True).start()
        if not self.ready.wait(10):
            self.stop()
            raise rb.RunFailed(f"screen-probe never delivered a frame: {self.events}")

    def _read(self):
        for line in self.proc.stdout:
            try:
                ev = json.loads(line)
            except ValueError:
                continue
            self.events.append(ev)
            if ev.get("event") in ("ready", "error"):
                self.ready.set()

    def stop(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()
        return self.events


def read_frames(path):
    frames = []
    with open(path, "rb") as f:
        while True:
            head = f.read(16)
            if len(head) < 16:
                break
            t, w, h = struct.unpack("<QII", head)
            data = f.read(w * h)
            if len(data) < w * h:
                break
            frames.append((t, w, h, data))
    return frames


# ------------------------------------------------------------------ one run

def one_run(app_id, app, doc, repeat, out, cfg, excise_app, probe_on=True, extra_env=None):
    run_dir = out / app_id / doc["id"] / (f"r{repeat}" if probe_on else f"r{repeat}-noprobe")
    run_dir.mkdir(parents=True, exist_ok=True)
    doc_copy = run_dir / f"{doc['id']}-{app_id}-r{repeat}-{int(time.time())}.pdf"
    doc_copy.write_bytes((ROOT / doc["path"]).read_bytes())

    log = {"app": app_id, "doc": doc["id"], "repeat": repeat, "probe": probe_on,
           "failures": [], "events": [], "phases": {}}
    rb.park_pointer()
    t_open = mach_now()
    pid, before, t0 = rb.launch(app_id, app, doc_copy, run_dir, cfg, excise_app, extra_env=extra_env)
    tracker = rb.Tracker(app, pid, before, t0)
    probe = None
    try:
        # Cold launch: time until a window exists, then until its content settles.
        deadline = time.time() + 60
        while time.time() < deadline and not rb.main_window_id(pid):
            time.sleep(0.02)
        t_window = mach_now()
        log["phases"]["launch"] = {"openTick": t_open, "windowTick": t_window,
                                   "windowMs": ticks_to_ms(t_window - t_open)}
        if probe_on:
            probe = Probe(pid, run_dir / "launch.bin")
        rb.wait_settled(tracker, cfg["settle"])
        log["phases"]["launch"]["settledTick"] = mach_now()
        if probe:
            probe.stop(); probe = None

        rb.set_window(pid, cfg["window"])
        rb.wait_settled(tracker, cfg["settle"])
        page, evidence = rb.read_page(app_id, pid, run_dir, "start", cfg["window"])
        if page != 1 and not (app_id == "preview" and page == 2):
            raise rb.RunFailed(f"not at page 1 before the test: {evidence!r}")
        rb.front(pid)
        rb.park_pointer()
        time.sleep(1.0)

        if probe_on:
            probe = Probe(pid, run_dir / "interact.bin")
        time.sleep(1.0)
        nk = app["nextPage"]
        turns = min(PAGE_TURNS, doc["pages"] - 1)
        for i in range(turns):
            log["events"].append({"kind": "turn", "i": i, "tick": post_key(nk["keyCode"], nk["modifiers"])})
            time.sleep(TURN_GAP_S)
        page, evidence = rb.read_page(app_id, pid, run_dir, "after-turns", cfg["window"])
        expect = 1 + turns if not (app_id == "preview" and doc["id"] == "altona") else None
        if expect is not None and page != expect:
            log["failures"].append(f"after turns: expected page {expect}, read {page!r}")
        log["events"].append({"kind": "marker", "what": "screenshot", "tick": mach_now()})

        log["events"].append({"kind": "home", "tick": post_key(115)})
        time.sleep(3.0)
        burst_ticks = []
        for i in range(min(BURST, doc["pages"] - 1)):
            burst_ticks.append(post_key(nk["keyCode"], nk["modifiers"]))
            time.sleep(BURST_GAP_S)
        log["events"].append({"kind": "burst", "tick": burst_ticks[0], "ticks": burst_ticks})
        time.sleep(3.0)
        log["events"].append({"kind": "end", "tick": post_key(119)})
        time.sleep(3.0)
        log["events"].append({"kind": "revisit", "tick": post_key(115)})
        time.sleep(3.0)

        # Steady scroll over the middle of the page area.
        wx, wy = cfg["window"]["x"], cfg["window"]["y"]
        x0, y0, x1, y1 = PAGE_REGION[app_id]
        move_pointer(wx + (x0 + x1) / 2, wy + (y0 + y1) / 2)
        time.sleep(0.3)
        scroll_ticks = []
        for i in range(SCROLL_EVENTS):
            scroll_ticks.append(post_scroll(SCROLL_PX))
            time.sleep(FRAME_S)
        log["events"].append({"kind": "scroll", "tick": scroll_ticks[0], "lastTick": scroll_ticks[-1]})
        time.sleep(2.5)
        rb.park_pointer()
        log["events"].append({"kind": "marker", "what": "end", "tick": mach_now()})
    except (rb.RunFailed, RuntimeError, subprocess.TimeoutExpired) as e:
        log["failures"].append(f"aborted: {e}")
    finally:
        if probe:
            log["probeEvents"] = probe.stop()
        if not rb.quit_app(app, pid):
            log["failures"].append("did not quit; killed")
            try:
                os.kill(pid, 9)
            except ProcessLookupError:
                pass
        time.sleep(3)
    (run_dir / "run.json").write_text(json.dumps(log, indent=1))
    print(f"  {app_id:8} {doc['id']:7} r{repeat}{'' if probe_on else ' (no probe)'}  "
          f"{'OK' if not log['failures'] else 'FAIL ' + '; '.join(log['failures'])}", flush=True)
    return log


# ------------------------------------------------------------------ analysis

class Region:
    def __init__(self, app_id, w, h, win):
        sx, sy = w / win["width"], h / win["height"]
        x0, y0, x1, y1 = PAGE_REGION[app_id]
        self.box = (int(x0 * sx), int(y0 * sy), int(x1 * sx), int(y1 * sy))
        self.w, self.h = w, h

    def pixels(self, data):
        import numpy as np
        x0, y0, x1, y1 = self.box
        return np.frombuffer(data, dtype=np.uint8).reshape(self.h, self.w)[y0:y1, x0:x1].astype(np.int16)


def diff_fraction(a, b, level=12):
    """Share of page-area pixels that differ by more than `level` luma steps."""
    import numpy as np
    return float(np.mean(np.abs(a - b) > level))


def frame_at(frames, tick):
    """The frame on screen at `tick`: the last one displayed at or before it."""
    best = None
    for f in frames:
        if f[0] <= tick:
            best = f
        else:
            break
    return best


def analyze_event(frames, region, t_start, t_end, threshold=0.002):
    pre = frame_at(frames, t_start)
    window = [f for f in frames if t_start < f[0] < t_end]
    if pre is None:
        return {"error": "no frame before the event"}
    pre_px = region.pixels(pre[3])
    if not window:
        return {"responded": False}
    final_px = region.pixels(window[-1][3])
    total = diff_fraction(pre_px, final_px)
    first = drawn = None
    si = 0.0
    last_t, last_incomplete = t_start, 1.0
    for f in window:
        px = region.pixels(f[3])
        if first is None and diff_fraction(px, pre_px) > threshold:
            first = f[0]
        remaining = diff_fraction(px, final_px)
        if remaining > threshold:
            drawn = f[0]
        si += last_incomplete * ticks_to_ms(f[0] - last_t)
        last_t = f[0]
        last_incomplete = min(1.0, remaining / total) if total > threshold else 0.0
    if first is None:
        return {"responded": False, "frames": len(window)}
    drawn = drawn or first
    return {"responded": True, "frames": len(window), "changedFraction": round(total, 4),
            "firstChangeMs": round(ticks_to_ms(first - t_start), 1),
            "drawnMs": round(ticks_to_ms(drawn - t_start), 1),
            "speedIndexMs": round(si, 1)}


def analyze_scroll(frames, region, t_start, t_last_input, t_end):
    during = [f for f in frames if t_start <= f[0] <= t_last_input]
    gaps = [ticks_to_ms(b[0] - a[0]) for a, b in zip(during, during[1:])]
    after = [f for f in frames if t_last_input < f[0] < t_end]
    dur_s = ticks_to_ms(t_last_input - t_start) / 1000
    res = {"frames": len(during), "fps": round(len(during) / max(1e-6, dur_s), 1)}
    if gaps:
        gs = sorted(gaps)
        res.update(gapP50Ms=round(gs[len(gs) // 2], 1), gapP95Ms=round(gs[int(len(gs) * 0.95)], 1),
                   gapMaxMs=round(gs[-1], 1), hitches=sum(1 for g in gaps if g > 2.5 * FRAME_S * 1000))
    if after:
        # Settle tail: the last frame that still differs from the final one.
        final_px = region.pixels(after[-1][3])
        tail = None
        for f in after:
            if diff_fraction(region.pixels(f[3]), final_px) > 0.002:
                tail = f[0]
        res["settleTailMs"] = round(ticks_to_ms((tail or after[0][0]) - t_last_input), 1)
    return res


def analyze_run(run_dir, cfg):
    log = json.loads((run_dir / "run.json").read_text())
    res = {"app": log["app"], "doc": log["doc"], "repeat": log["repeat"], "failures": log["failures"]}
    launch = log["phases"].get("launch", {})
    if "windowMs" in launch:
        res["launchWindowMs"] = round(launch["windowMs"], 1)
    lb = run_dir / "launch.bin"
    if lb.exists() and "settledTick" in launch:
        # Before the window is resized, so no page region applies: the last
        # frame the launch produced before it settled is "launch drawn".
        fr = [f for f in read_frames(lb) if f[0] <= launch["settledTick"]]
        if fr:
            res["launchDrawnMs"] = round(ticks_to_ms(fr[-1][0] - launch["openTick"]), 1)
    ib = run_dir / "interact.bin"
    if not ib.exists():
        return res
    frames = read_frames(ib)
    if not frames:
        res["failures"] = res["failures"] + ["no interaction frames"]
        return res
    region = Region(log["app"], frames[0][1], frames[0][2], cfg["window"])
    evs = log["events"]
    per = {}
    for i, e in enumerate(evs):
        nxt = evs[i + 1]["tick"] if i + 1 < len(evs) else frames[-1][0] + 1
        if e["kind"] == "marker":
            continue
        if e["kind"] == "scroll":
            per["scroll"] = analyze_scroll(frames, region, e["tick"], e["lastTick"], nxt)
        elif e["kind"] == "burst":
            per["burst"] = analyze_event(frames, region, e["tick"], nxt)
        else:
            per.setdefault(e["kind"], []).append(analyze_event(frames, region, e["tick"], nxt))
    res["events"] = per
    return res


def median(xs):
    xs = [x for x in xs if x is not None]
    return statistics.median(xs) if xs else None


def p95(xs):
    xs = sorted(x for x in xs if x is not None)
    return xs[min(len(xs) - 1, int(len(xs) * 0.95))] if xs else None


def summarize(out):
    cfg = json.loads(rb.CONFIG.read_text())
    runs = []
    for run_dir in sorted(out.glob("*/*/r*")):
        if (run_dir / "run.json").exists() and not run_dir.name.endswith("noprobe"):
            r = analyze_run(run_dir, cfg)
            runs.append(r)
            (run_dir / "analysis.json").write_text(json.dumps(r, indent=1))
    lines = ["# Reader speed benchmark: excise vs Preview vs Adobe Acrobat (#1544)", "",
             f"Run: `{out}`", "",
             "Times in ms from the input event, over the app's page area. Median across repeats "
             "(page turns: median and p95 across all turns). `drawn` = the last frame that still "
             "differs from the settled frame; speed index = area under (1 - visual completeness). "
             "Timing floor: one 60 Hz frame (~17 ms).", ""]
    keyed = {}
    for r in runs:
        if r["failures"]:
            continue
        keyed.setdefault((r["doc"], r["app"]), []).append(r)
    for doc in SPEED_DOCS:
        present = [a for a in ("excise", "preview", "acrobat") if (doc, a) in keyed]
        if not present:
            continue
        lines += [f"## {doc}", "",
                  "| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | "
                  "no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |",
                  "|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
        for app in present:
            rs = keyed[(doc, app)]
            turns = [t for r in rs for t in r.get("events", {}).get("turn", [])]
            ok_turns = [t for t in turns if t.get("responded")]
            f = lambda v: "—" if v is None else f"{v:.0f}"
            ev = lambda k, m: median([(r.get("events", {}).get(k) or [{}])[0].get(m) if isinstance(r.get("events", {}).get(k), list)
                                      else (r.get("events", {}).get(k) or {}).get(m) for r in rs])
            lines.append(
                f"| {app} | {len(rs)} | {f(median([r.get('launchWindowMs') for r in rs]))} / {f(median([r.get('launchDrawnMs') for r in rs]))} | "
                f"{f(median([t['firstChangeMs'] for t in ok_turns]))} / {f(p95([t['firstChangeMs'] for t in ok_turns]))} | "
                f"{f(median([t['drawnMs'] for t in ok_turns]))} / {f(p95([t['drawnMs'] for t in ok_turns]))} | "
                f"{f(median([t['speedIndexMs'] for t in ok_turns]))} | {len(turns) - len(ok_turns)}/{len(turns)} | "
                f"{f(ev('burst', 'drawnMs'))} | {f(ev('end', 'drawnMs'))} | {f(ev('revisit', 'drawnMs'))} | "
                f"{f(ev('scroll', 'fps'))} | {f(ev('scroll', 'gapP95Ms'))} | {f(ev('scroll', 'hitches'))} | {f(ev('scroll', 'settleTailMs'))} |")
        lines.append("")
    fails = [r for r in runs if r["failures"]]
    if fails:
        lines += ["## Failed runs (excluded)", ""] + [f"- {r['app']} {r['doc']} r{r['repeat']}: {'; '.join(r['failures'])}" for r in fails]
    (out / "summary.md").write_text("\n".join(lines) + "\n")
    print("\n".join(lines))


# ------------------------------------------------------------------ calibration

def excise_render_stats(metrics_path):
    single, band = [], []
    if not metrics_path.exists():
        return {}
    for line in metrics_path.read_text().splitlines():
        try:
            j = json.loads(line)
        except ValueError:
            continue
        inst = j.get("instrument")
        if inst == "excise.viewer.single_page.render.duration":
            single.append(j["value"])
        elif inst == "excise.viewer.continuous.band.render.duration":
            band.append(j["value"])
    return {"bandN": len(band), "bandP50": median(band), "bandP95": p95(band),
            "singleN": len(single), "singleP50": median(single)}


def calibrate(out, cfg, docs, excise_app, repeats):
    """Probe cost: excise's OWN render timings with the probe on vs off.
    Agreement: probe 'drawn' vs excise's logged band-render durations."""
    app = cfg["apps"]["excise"]
    doc = docs["irs"]
    rows = {True: [], False: []}
    for r in range(1, repeats + 1):
        for on in (True, False):
            metrics = out / "excise" / "irs" / (f"r{r}" if on else f"r{r}-noprobe") / "metrics.jsonl"
            metrics.parent.mkdir(parents=True, exist_ok=True)
            one_run("excise", app, doc, r, out, cfg, excise_app, probe_on=on,
                    extra_env={"EXCISE_TRACE_VIEWER": str(metrics)})
            rows[on].append(excise_render_stats(metrics))
    res = {"probeOn": rows[True], "probeOff": rows[False]}
    on50 = median([x.get("bandP50") for x in rows[True]])
    off50 = median([x.get("bandP50") for x in rows[False]])
    res["bandRenderP50DeltaMs"] = None if on50 is None or off50 is None else round(on50 - off50, 1)
    (out / "calibration.json").write_text(json.dumps(res, indent=1))
    print(json.dumps({k: v for k, v in res.items() if k != "probeOn" and k != "probeOff"}, indent=1))
    print(f"band render p50: probe on {on50}, off {off50}")


# ------------------------------------------------------------------ main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apps", default="excise,preview,acrobat")
    ap.add_argument("--docs", default=",".join(SPEED_DOCS))
    ap.add_argument("--repeats", type=int, default=3)
    ap.add_argument("--excise-app", default=str(ROOT / "logs/reader-bench-bundle/excise.app"))
    ap.add_argument("--out")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--calibrate", action="store_true")
    ap.add_argument("--analyze")
    a = ap.parse_args()

    if a.analyze:
        summarize(pathlib.Path(a.analyze).resolve()); return

    cfg = json.loads(rb.CONFIG.read_text())
    apps = {k: v for k, v in cfg["apps"].items() if k in a.apps.split(",")}
    docs = {d["id"]: d for d in cfg["documents"] if d["id"] in a.docs.split(",") or a.calibrate}
    for d in docs.values():
        d["pages"] = int(rb.sh(["qpdf", "--show-npages", str(ROOT / d["path"])], check=True).stdout)
    runs = [(r, d, app) for r in range(1, a.repeats + 1) for d in a.docs.split(",") if d in docs for app in apps]
    if a.list:
        for r, d, app in runs:
            print(f"r{r} {app:8} {d}")
        print(f"{len(runs)} runs (~{len(runs) * 1.9:.0f} min)"); return

    rb.preflight(apps)
    out = pathlib.Path(a.out or ROOT / f"logs/reader-speed_{time.strftime('%Y%m%d_%H%M%S')}").resolve()
    out.mkdir(parents=True, exist_ok=True)
    (out / "run-meta.json").write_text(json.dumps({
        "sha": rb.sh(["git", "-C", str(ROOT), "rev-parse", "--short", "HEAD"]).stdout.strip(),
        "started": time.strftime("%Y-%m-%dT%H:%M:%S"), "calibrate": a.calibrate,
        "pageRegion": PAGE_REGION, "pageTurns": PAGE_TURNS, "turnGapS": TURN_GAP_S}, indent=1))
    if a.calibrate:
        calibrate(out, cfg, docs, a.excise_app, a.repeats)
        return
    print(f"==> {len(runs)} runs -> {out}", flush=True)
    for r, d, app in runs:
        one_run(app, apps[app], docs[d], r, out, cfg, a.excise_app)
    summarize(out)


if __name__ == "__main__":
    main()

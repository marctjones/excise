#!/usr/bin/env python3
"""Responsiveness benchmark: excise vs Preview vs Chrome vs Adobe Acrobat (#1544).

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

MULTI-DOCUMENT SET (#1551-#1554), OPTIONAL: `--multi` runs it INSTEAD of the
single-document runs. Per config in bench.json `multiDocument.configs`, the
app opens scan, then irs (the second into the running instance), every
document window is put on the bench frame, and then:
  switch   Cmd+` (windows) or Ctrl+Tab (tabs), `switches` times, `switchGapSeconds`
           apart; input -> first change / drawn of the page area. One switch
           there and back is made and verified by window title BEFORE recording,
           so both documents have been shown once.
  turn2    page turns in the SECOND document while the first stays open,
           verified by the page indicator afterwards.
A window switch changes which window is in front, not any window's content, so
these runs record a fixed screen region (the bench frame) through
`screen-probe --region` instead of one window.

    scripts/reader_speed_bench.py --multi --list
    scripts/reader_speed_bench.py --multi --repeats 1              # 4 runs, ~8 min (estimate)
    scripts/reader_speed_bench.py --multi --configs excise-tabs --repeats 1

Estimate ~2 min per run: launch and two settles, a verified switch pair,
10 switches x 2 s, 20 turns x 1.5 s, quit. excise's tab strip moves its page
area down; EXCISE_TAB_STRIP_PT is an estimate until measured from a run's
start-b.png.
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
    # Chrome: below its PDF toolbar (ends ~143 pt), right of the thumbnail sidebar
    # divider (300 pt), inside the window. Measured 2026-09-21 by drawing the box on a
    # saved 1200x800 window screenshot (a red rectangle framed exactly the page pane).
    "chrome":  (305, 150, 1185, 795),
}

# excise-tabs only: the tab strip (#1554) sits above the viewer once a window
# holds two documents, pushing the page area down. ESTIMATED from
# DocumentTabStrip.axaml (5 pt padding each side of one text line plus a 1 pt
# border), NOT measured: measure it from a multi run's start-b.png before
# quoting an excise-tabs switch number.
EXCISE_TAB_STRIP_PT = 32


def page_box(app_id, layout=None):
    x0, y0, x1, y1 = PAGE_REGION[app_id]
    if app_id == "excise" and layout == "tabs":
        y0 += EXCISE_TAB_STRIP_PT
    return (x0, y0, x1, y1)


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
    # Real Home/End/PageUp/PageDown events carry the Fn flag, and real arrow
    # keys carry Fn + NumericPad. Without them End did nothing in Preview
    # (2026-09-17: page stayed 5 of 126) while AppleScript's End worked.
    flags = 0
    if code in (115, 116, 119, 121):
        flags |= Quartz.kCGEventFlagMaskSecondaryFn
    if code in (123, 124, 125, 126):
        flags |= Quartz.kCGEventFlagMaskSecondaryFn | Quartz.kCGEventFlagMaskNumericPad
    if "option" in mods:
        flags |= Quartz.kCGEventFlagMaskAlternate
    if "command" in mods:
        flags |= Quartz.kCGEventFlagMaskCommand
    if "control" in mods:
        flags |= Quartz.kCGEventFlagMaskControl
    if "shift" in mods:
        flags |= Quartz.kCGEventFlagMaskShift
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
    def __init__(self, pid, out, region=None):
        """`region`: a window dict (x, y, width, height) to record as a fixed
        screen rectangle instead of the app's largest window."""
        if not PROBE.exists():
            raise rb.RunFailed(f"no probe at {PROBE}; run scripts/build-screen-probe.sh")
        self.out = out
        args = [str(PROBE), "--pid", str(pid), "--out", str(out)]
        if region:
            args += ["--region", f"{region['x']},{region['y']},{region['width']},{region['height']}"]
        self.proc = subprocess.Popen(args,
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
    doc_copy = rb.app_area(run_dir) / f"{doc['id']}-{app_id}-r{repeat}-{int(time.time())}.pdf"
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
        if app.get("clickToFocus"):
            # Chrome opens with keyboard focus outside the page pane (see the DRIVING
            # NOTES in reader_bench.py); one click in the page margin, before any
            # recording starts, is what a user does. The title must not change.
            focus = app["clickToFocus"]
            title_before = rb.window_title(pid)
            rb.front(pid)
            rb.click_in_window(cfg["window"], focus["xFraction"], focus["yFraction"])
            time.sleep(1.0)
            if rb.window_title(pid) != title_before:
                raise rb.RunFailed("the focus click changed the window title")
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
        # Preview and Chrome do not step exactly one page per key on Altona (a mix of 16
        # tiny pages and one large one), so the page count is not checked there.
        expect = 1 + turns if not (app_id in ("preview", "chrome") and doc["id"] == "altona") else None
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
        rb.collect_app_area(run_dir)
    (run_dir / "run.json").write_text(json.dumps(log, indent=1))
    print(f"  {app_id:8} {doc['id']:7} r{repeat}{'' if probe_on else ' (no probe)'}  "
          f"{'OK' if not log['failures'] else 'FAIL ' + '; '.join(log['failures'])}", flush=True)
    return log


# ------------------------------------------------------- multi-document run

MULTI_DOC_KEY = "switch"


def verify_front(app_id, pid, copy, previous, what):
    """Wait briefly for the front window to name `copy`; raise if it does not."""
    deadline = time.time() + 10
    title = ""
    while time.time() < deadline:
        rb.park_pointer()
        title = rb.front_title(pid)
        if rb.title_shows(app_id, title, copy.stem, previous):
            return title
        time.sleep(0.25)
    raise rb.RunFailed(f"{what}: front window shows {title[:60]!r}, not {copy.name}")


def multi_run(config, app, docs, repeat, out, cfg, excise_app, tabbing, probe_on=True):
    mcfg = cfg["multiDocument"]
    app_id = config["app"]
    layout = rb.expected_layout(config, tabbing)
    run_dir = out / config["id"] / MULTI_DOC_KEY / f"r{repeat}"
    run_dir.mkdir(parents=True, exist_ok=True)
    stamp = int(time.time())
    copies = []
    for d in docs:
        c = rb.app_area(run_dir) / f"{d['id']}-{config['id']}-r{repeat}-{stamp}.pdf"
        c.write_bytes((ROOT / d["path"]).read_bytes())
        copies.append(c)
    first, second = docs
    log = {"kind": "multi", "app": config["id"], "appId": app_id, "doc": MULTI_DOC_KEY,
           "docs": [d["id"] for d in docs], "repeat": repeat, "probe": probe_on,
           "layout": layout, "systemTabbing": tabbing, "failures": [], "events": [], "phases": {}}
    rb.park_pointer()
    pid, before, t0 = rb.launch(app_id, app, copies[0], run_dir, cfg, excise_app,
                                settings=rb.multi_settings(config))
    tracker = rb.Tracker(app, pid, before, t0)
    probe = None
    try:
        if not rb.wait_window(pid):
            raise rb.RunFailed("no window within 60 s")
        rb.set_window(pid, cfg["window"])
        titles = []
        title = ""
        for k, copy in enumerate(copies, start=1):
            ok, title, windows, note = rb.open_and_verify(
                app_id, app, pid, copy, k, layout, title, excise_app, cfg, first=(k == 1))
            if not ok:
                raise rb.RunFailed(note)
            titles.append(title)
            rb.wait_settled(tracker, cfg["settle"])

        page, evidence = rb.read_page(app_id, pid, run_dir, "start-b", cfg["window"])
        if page != 1 and not (app_id == "preview" and page == 2):
            raise rb.RunFailed(f"second document not at page 1 before the test: {evidence!r}")

        # One verified switch there and back, outside the recording: proves the
        # key really switches documents in this app and layout, and shows each
        # document once before anything is timed.
        sk = mcfg["switchKeys"][layout]
        rb.front(pid)
        post_key(sk["keyCode"], sk["modifiers"])
        verify_front(app_id, pid, copies[0], titles[1], "after the first switch")
        rb.wait_settled(tracker, cfg["settle"])
        post_key(sk["keyCode"], sk["modifiers"])
        verify_front(app_id, pid, copies[1], titles[0], "after switching back")
        rb.wait_settled(tracker, cfg["settle"])
        rb.park_pointer()
        time.sleep(1.0)

        if probe_on:
            probe = Probe(pid, run_dir / "interact.bin", region=cfg["window"])
        time.sleep(1.0)
        switches = mcfg["switches"] + (mcfg["switches"] % 2)     # even: end on the second document
        for i in range(switches):
            log["events"].append({"kind": "switch", "i": i,
                                  "tick": post_key(sk["keyCode"], sk["modifiers"])})
            time.sleep(mcfg["switchGapSeconds"])
        log["events"].append({"kind": "marker", "what": "title-check", "tick": mach_now()})
        verify_front(app_id, pid, copies[1], titles[0], "after the timed switches")

        nk = app["nextPage"]
        turns = min(PAGE_TURNS, second["pages"] - 1)
        rb.front(pid)
        rb.park_pointer()
        time.sleep(0.5)
        for i in range(turns):
            log["events"].append({"kind": "turn2", "i": i, "tick": post_key(nk["keyCode"], nk["modifiers"])})
            time.sleep(TURN_GAP_S)
        log["events"].append({"kind": "marker", "what": "end", "tick": mach_now()})
        page, evidence = rb.read_page(app_id, pid, run_dir, "after-turns", cfg["window"])
        if page != 1 + turns:
            log["failures"].append(f"after turns: expected page {1 + turns}, read {page!r}")
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
        rb.collect_app_area(run_dir)
    (run_dir / "run.json").write_text(json.dumps(log, indent=1))
    print(f"  {config['id']:15} {MULTI_DOC_KEY} r{repeat}  "
          f"{'OK' if not log['failures'] else 'FAIL ' + '; '.join(log['failures'])}", flush=True)
    return log


# ------------------------------------------------------------------ analysis

class Region:
    def __init__(self, app_id, w, h, win, layout=None):
        sx, sy = w / win["width"], h / win["height"]
        x0, y0, x1, y1 = page_box(app_id, layout)
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
    first = None
    drawn_idx = 0          # index of the first frame from which the page stays final
    si = 0.0
    last_t, last_incomplete = t_start, 1.0
    for i, f in enumerate(window):
        px = region.pixels(f[3])
        if first is None and diff_fraction(px, pre_px) > threshold:
            first = f[0]
        remaining = diff_fraction(px, final_px)
        if remaining > threshold:
            # Still not final: the page becomes complete no earlier than the NEXT
            # frame. (Reporting this frame's time undercounted a two-step draw.)
            drawn_idx = i + 1
        si += last_incomplete * ticks_to_ms(f[0] - last_t)
        last_t = f[0]
        last_incomplete = min(1.0, remaining / total) if total > threshold else 0.0
    if first is None:
        return {"responded": False, "frames": len(window)}
    drawn = window[min(drawn_idx, len(window) - 1)][0]
    if drawn < first:
        drawn = first
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
    res = {"app": log["app"], "doc": log["doc"], "repeat": log["repeat"], "failures": log["failures"],
           "kind": log.get("kind"), "layout": log.get("layout"), "systemTabbing": log.get("systemTabbing")}
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
    region = Region(log.get("appId", log["app"]), frames[0][1], frames[0][2], cfg["window"], log.get("layout"))
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
             "(page turns: median and p95 across all turns). `drawn` = the first frame from which the "
             "page area stays final; speed index = area under (1 - visual completeness). "
             "Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), "
             "because it is taken before the window is resized. "
             "Timing floor: one 60 Hz frame (~17 ms).", ""]
    # The after-turns page check is SOFT. On Altona the last two short pages are
    # on screen before the final turn, so that turn has nothing to scroll in
    # excise or Preview; excise's indicator then says 16 of 17 where Preview
    # names the fully visible 17. Every turn's timing is still valid, and turns
    # that changed nothing are counted in their own column, so such a run is
    # kept and noted rather than excluded.
    soft = lambda f: f.startswith("after turns:")
    keyed, notes = {}, []
    for r in runs:
        if any(not soft(f) for f in r["failures"]):
            continue
        notes += [f"- {r['app']} {r['doc']} r{r['repeat']}: {f}" for f in r["failures"]]
        keyed.setdefault((r["doc"], r["app"]), []).append(r)
    for doc in [d for d in SPEED_DOCS if any(k[0] == d for k in keyed)]:
        present = [a for a in ("excise", "preview", "chrome", "acrobat") if (doc, a) in keyed]
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
    multi_keys = sorted(k for k in keyed if k[0] == MULTI_DOC_KEY)
    if multi_keys:
        lines += multi_summary_lines(keyed, multi_keys)
    if notes:
        lines += ["## Kept with a note (see the soft-check comment in summarize)", ""] + notes + [""]
    fails = [r for r in runs if any(not soft(f) for f in r["failures"])]
    if fails:
        lines += ["## Failed runs (excluded)", ""] + [f"- {r['app']} {r['doc']} r{r['repeat']}: {'; '.join(r['failures'])}" for r in fails]
    (out / "summary.md").write_text("\n".join(lines) + "\n")
    print("\n".join(lines))


def multi_summary_lines(keyed, keys):
    f = lambda v: "—" if v is None else f"{v:.0f}"
    lines = ["## Multi-document: switching and turning pages in the second document", "",
             "`switch` = Cmd+` between windows or Ctrl+Tab between tabs, over a fixed screen region "
             "(the bench frame, every document window stacked on it). `turn2` = next-page in the second "
             "document while the first stays open. Median and p95 across every event of every run.", "",
             "| config | layout (system tabbing) | runs | switch first change p50 / p95 | switch drawn p50 / p95 | "
             "switch speed index p50 | no-response switches | turn2 first change p50 / p95 | turn2 drawn p50 / p95 | "
             "no-response turns |",
             "|---|---|---:|---|---|---:|---:|---|---|---:|"]
    for key in keys:
        rs = keyed[key]
        cells = []
        for kind in ("switch", "turn2"):
            evs = [e for r in rs for e in r.get("events", {}).get(kind, [])]
            ok = [e for e in evs if e.get("responded")]
            fc = [e["firstChangeMs"] for e in ok]
            dr = [e["drawnMs"] for e in ok]
            cells.append((f"{f(median(fc))} / {f(p95(fc))}", f"{f(median(dr))} / {f(p95(dr))}",
                          f(median([e["speedIndexMs"] for e in ok])), f"{len(evs) - len(ok)}/{len(evs)}"))
        sw, tu = cells
        layout = sorted({f"{r.get('layout')} ({r.get('systemTabbing')})" for r in rs})
        lines.append(f"| {key[1]} | {', '.join(layout)} | {len(rs)} | {sw[0]} | {sw[1]} | {sw[2]} | {sw[3]} | "
                     f"{tu[0]} | {tu[1]} | {tu[3]} |")
    lines += ["", "A no-response switch means the page area did not change after the key: the key did "
              "not switch documents, or both documents look the same in that region. The verified "
              "switch pair before recording rules out the first for the run as a whole.", ""]
    return lines


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
            run_dir = out / "excise" / "irs" / (f"r{r}" if on else f"r{r}-noprobe")
            run_dir.mkdir(parents=True, exist_ok=True)
            # The app writes its metrics in its own area (outside ~/Documents);
            # one_run copies *.jsonl back into run_dir when the app has quit.
            one_run("excise", app, doc, r, out, cfg, excise_app, probe_on=on,
                    extra_env={"EXCISE_TRACE_VIEWER": str(rb.app_area(run_dir) / "metrics.jsonl")})
            rows[on].append(excise_render_stats(run_dir / "metrics.jsonl"))
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
    ap.add_argument("--apps", default="excise,preview,chrome,acrobat")
    ap.add_argument("--docs", default=",".join(SPEED_DOCS))
    ap.add_argument("--repeats", type=int, default=3)
    ap.add_argument("--excise-app", default=str(ROOT / "logs/reader-bench-bundle/excise.app"))
    ap.add_argument("--out")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--calibrate", action="store_true")
    ap.add_argument("--analyze")
    ap.add_argument("--resume", action="store_true", help="skip runs whose run.json already exists in --out (continue a paused run)")
    ap.add_argument("--multi", action="store_true",
                    help="run the multi-document set (bench.json multiDocument) instead of the single-document runs")
    ap.add_argument("--configs", help="multi-document configs to run (default: all in bench.json)")
    a = ap.parse_args()

    if a.analyze:
        summarize(pathlib.Path(a.analyze).resolve()); return

    cfg = json.loads(rb.CONFIG.read_text())
    apps = {k: v for k, v in cfg["apps"].items() if k in a.apps.split(",")}
    if a.multi:
        return main_multi(a, cfg, apps)
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
        if a.resume and (out / app / d / f"r{r}" / "run.json").exists():
            print(f"  {app:8} {d:7} r{r}  skipped (already done: --resume)", flush=True)
            continue
        one_run(app, apps[app], docs[d], r, out, cfg, a.excise_app)
    summarize(out)


def main_multi(a, cfg, apps):
    configs = rb.select_multi_configs(cfg, apps, a.configs)
    tabbing = rb.system_tabbing()
    mcfg = cfg["multiDocument"]
    runs = [(r, c) for r in range(1, a.repeats + 1) for c in configs]
    if a.list:
        for r, c in runs:
            print(f"r{r} {c['id']:15} {'+'.join(mcfg['speedDocuments'])}  "
                  f"layout {rb.expected_layout(c, tabbing)} (system tabbing: {tabbing}), "
                  f"switch key {mcfg['switchKeys'][rb.expected_layout(c, tabbing)]['modifiers']}"
                  f"+{mcfg['switchKeys'][rb.expected_layout(c, tabbing)]['keyCode']}")
        print(f"{len(runs)} runs (~{len(runs) * 2:.0f} min, estimated at ~2 min per run)")
        return
    docs = rb.resolve_multi_docs(cfg, mcfg["speedDocuments"])
    if len(docs) != 2:
        sys.exit("multiDocument.speedDocuments must name exactly two documents")
    rb.preflight({c["app"]: apps[c["app"]] for c in configs}, multi=True)
    out = pathlib.Path(a.out or ROOT / f"logs/reader-speed-multi_{time.strftime('%Y%m%d_%H%M%S')}").resolve()
    out.mkdir(parents=True, exist_ok=True)
    (out / "run-meta.json").write_text(json.dumps({
        "sha": rb.sh(["git", "-C", str(ROOT), "rev-parse", "--short", "HEAD"]).stdout.strip(),
        "started": time.strftime("%Y-%m-%dT%H:%M:%S"), "multi": True, "systemTabbing": tabbing,
        "pageRegion": PAGE_REGION, "exciseTabStripPt": EXCISE_TAB_STRIP_PT,
        "pageTurns": PAGE_TURNS, "turnGapS": TURN_GAP_S}, indent=1))
    print(f"==> {len(runs)} multi-document runs -> {out} (system tabbing: {tabbing})", flush=True)
    for r, c in runs:
        multi_run(c, apps[c["app"]], docs, r, out, cfg, a.excise_app, tabbing)
    summarize(out)


if __name__ == "__main__":
    main()

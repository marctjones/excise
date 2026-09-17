#!/usr/bin/env python3
"""Like-for-like memory/CPU benchmark: excise vs Preview vs Adobe Acrobat (#1543).

TOOLING, not a gate (tests/gates-tooling.txt): it drives three GUI apps on one
machine, and absolute footprint is a property of that machine and its load.

HOW IT STAYS FAIR
  * Same keystrokes for every app, sent through System Events to the app's own
    process (made frontmost first). excise's in-app runner (#1497) cannot drive
    the other two, and its driving-fidelity check showed keystrokes do the same
    render work, so keystrokes are the common denominator.
  * Same window: 1200x800 at (0, 33), set through System Events.
  * No restored state. Every run opens a FRESH COPY of the document at a new
    path, so no app can restore a last page or zoom for it (that exact leak
    made #1497's first calibration measure nothing). Preview and Acrobat are
    opened with `open -F`; excise gets an isolated HOME.
  * Measured from OUTSIDE, summed over every process macOS holds the app
    RESPONSIBLE for (`responsibility_get_pid_responsible_for_pid`, the record
    Activity Monitor uses) plus its descendants. Acrobat's UI is an embedded
    browser plus seven system WebKit content processes, all responsible to
    Acrobat; counting only its main process would understate it by ~1 GB. A
    path-based guess was tried first and charged excise for CGPDFService and
    SafariPlatformSupport, which macOS started to index the copied PDF.
  * Every step is VERIFIED: the page indicator is read back (window title for
    Preview, OCR of the status bar for excise, OCR for Acrobat) and a step that
    did not reach its page is marked failed, not averaged in.
  * Apps are interleaved within each repeat so slow drift in machine state
    lands on all three, and the run refuses to start beside a test host.

    scripts/reader_bench.py --list
    scripts/reader_bench.py --apps excise,preview --docs w9 --repeats 1
    scripts/reader_bench.py                       # everything, bench.json repeats
    scripts/reader_bench.py --summarize logs/reader-bench_<stamp>
"""
import argparse, json, os, pathlib, re, shutil, signal, statistics, subprocess, sys, threading, time

ROOT = pathlib.Path(__file__).resolve().parent.parent
CONFIG = ROOT / "tests/reader-bench/bench.json"
MB = 1024 * 1024


# ----------------------------------------------------------------- helpers

def sh(cmd, check=False, timeout=60):
    return subprocess.run(cmd, capture_output=True, text=True, check=check, timeout=timeout)


def osa(*lines, timeout=30):
    args = ["osascript"]
    for l in lines:
        args += ["-e", l]
    r = sh(args, timeout=timeout)
    if r.returncode != 0:
        raise RuntimeError(f"osascript failed: {r.stderr.strip()}")
    return r.stdout.strip()


MOD = {"option": "option down", "command": "command down", "shift": "shift down", "control": "control down"}


def front(pid):
    osa(f'tell application "System Events" to set frontmost of (first process whose unix id is {pid}) to true')
    time.sleep(0.3)


def key(pid, code, mods=(), repeat=1, gap=0.25):
    park_pointer()
    front(pid)
    using = f" using {{{', '.join(MOD[m] for m in mods)}}}" if mods else ""
    osa('tell application "System Events"',
        f'repeat {repeat} times',
        f'key code {code}{using}',
        f'delay {gap}',
        'end repeat',
        'end tell', timeout=30 + repeat * (gap + 1))


def keystroke(pid, ch, mods=("command",)):
    front(pid)
    using = f" using {{{', '.join(MOD[m] for m in mods)}}}" if mods else ""
    osa(f'tell application "System Events" to keystroke "{ch}"{using}')


def largest_window(pid):
    """AppleScript reference to the app's LARGEST window. `window 1` is not safe:
    Acrobat's hover tooltip is a window and can be first."""
    return (f'(item (my biggest(every window of (first process whose unix id is {pid}))) '
            f'of (every window of (first process whose unix id is {pid})))')


_BIGGEST = """
on biggest(ws)
    set best to 1
    set bestArea to -1
    repeat with i from 1 to count of ws
        set s to size of item i of ws
        set a to (item 1 of s) * (item 2 of s)
        if a > bestArea then
            set bestArea to a
            set best to i
        end if
    end repeat
    return best
end biggest
"""


def osa_se(body):
    """Run `body` inside `tell application "System Events"` with biggest() defined."""
    script = _BIGGEST + 'tell application "System Events"\n' + body + '\nend tell'
    r = sh(["osascript", "-e", script], timeout=30)
    if r.returncode != 0:
        raise RuntimeError(f"osascript failed: {r.stderr.strip()}")
    return r.stdout.strip()


def set_window(pid, w, timeout=10.0):
    """A window can be on screen (CGWindowList) a moment before System Events'
    accessibility list contains it; retry briefly instead of failing the run."""
    win = largest_window(pid)
    deadline = time.time() + timeout
    while True:
        try:
            osa_se(f'set position of {win} to {{{w["x"]}, {w["y"]}}}\n'
                   f'set size of {win} to {{{w["width"]}, {w["height"]}}}')
            return
        except RuntimeError:
            if time.time() > deadline:
                raise
            time.sleep(0.5)


def window_title(pid):
    try:
        return osa_se(f'get name of {largest_window(pid)}')
    except RuntimeError:
        return ""


def park_pointer():
    """Move the pointer off every window: a pointer resting on a page makes
    Acrobat pop an accessibility-summary tooltip window mid-measurement."""
    import Quartz
    main = Quartz.CGDisplayBounds(Quartz.CGMainDisplayID())
    pt = (main.size.width - 2, main.size.height - 2)
    Quartz.CGEventPost(Quartz.kCGHIDEventTap,
                       Quartz.CGEventCreateMouseEvent(None, Quartz.kCGEventMouseMoved, pt, 0))


def main_window_id(pid):
    import Quartz
    best = None
    for w in Quartz.CGWindowListCopyWindowInfo(Quartz.kCGWindowListOptionOnScreenOnly, Quartz.kCGNullWindowID):
        if w.get("kCGWindowOwnerPID") != pid or w.get("kCGWindowLayer", 0) != 0:
            continue
        b = w["kCGWindowBounds"]
        area = b["Width"] * b["Height"]
        if best is None or area > best[0]:
            best = (area, w["kCGWindowNumber"])
    return best[1] if best else None


_LIBC = None


def responsible_pid(pid):
    """The pid macOS holds responsible for `pid` (XPC services map to their client app)."""
    global _LIBC
    import ctypes
    if _LIBC is None:
        _LIBC = ctypes.CDLL(None)
        _LIBC.responsibility_get_pid_responsible_for_pid.argtypes = [ctypes.c_int]
        _LIBC.responsibility_get_pid_responsible_for_pid.restype = ctypes.c_int
    return _LIBC.responsibility_get_pid_responsible_for_pid(pid)


def cpu_seconds(t):
    # ps `time` on macOS: "M:SS.ss" or "H:MM:SS"
    parts = t.strip().split(":")
    try:
        vals = [float(p) for p in parts]
    except ValueError:
        return 0.0
    s = 0.0
    for v in vals:
        s = s * 60 + v
    return s


def ps_table():
    r = sh(["ps", "-axww", "-o", "pid=,ppid=,rss=,time=,comm="])
    rows = {}
    for line in r.stdout.splitlines():
        m = re.match(r"\s*(\d+)\s+(\d+)\s+(\d+)\s+(\S+)\s+(.*)$", line)
        if m:
            pid, ppid, rss, t, comm = m.groups()
            rows[int(pid)] = {"ppid": int(ppid), "rssKB": int(rss), "cpu": cpu_seconds(t), "comm": comm}
    return rows


def footprints(pids):
    """Per-process phys_footprint and its peak, via `footprint -j` (~35 ms)."""
    pids = [p for p in pids if p]
    if not pids:
        return {}
    out = f"/tmp/reader-bench-fp-{os.getpid()}.json"
    args = ["footprint", "-f", "bytes", "--noCategories", "-j", out]
    for p in pids:
        args += ["-p", str(p)]
    sh(args, timeout=30)
    try:
        d = json.load(open(out))
    except Exception:
        return {}
    res = {}
    for proc in d.get("processes", []):
        aux = proc.get("auxiliary", {})
        res[proc["pid"]] = {"fp": aux.get("phys_footprint", proc.get("footprint", 0)),
                            "peak": aux.get("phys_footprint_peak", 0)}
    return res


def ocr_page(png, region):
    """Read 'Page N' (or 'N of M' / 'N / M') out of a window screenshot region."""
    crop = png.with_suffix(".page.png")
    x, y, w, h = region
    sh(["sips", "-c", str(h), str(w), "--cropOffset", str(y), str(x), str(png), "--out", str(crop)])
    r = sh(["tesseract", str(crop), "-", "--psm", "6"])
    text = r.stdout
    m = re.search(r"Page\s+(\d+)", text) or re.search(r"(\d+)\s*(?:/|of)\s*\d+", text)
    return (int(m.group(1)) if m else None), text.strip()


def ocr_acrobat_page_box(png, win):
    """Acrobat's current-page box: white digits in a bordered dark box on the
    right rail. Plain OCR misreads it; inverted, thresholded and enlarged it
    read 1, 4, 8, 31, 126 and 1 correctly (2026-09-16). Geometry is for the
    bench's 1200x800 window at 2x."""
    from PIL import Image, ImageOps
    W, H = win["width"] * 2, win["height"] * 2
    box = (W - 74, int(H * 0.65125), W - 22, int(H * 0.65125) + 50)
    im = Image.open(png).convert("L").crop(box)
    im = ImageOps.invert(im).point(lambda v: 255 if v > 150 else 0)
    im = ImageOps.expand(im.resize((im.width * 4, im.height * 4)), border=40, fill=255)
    out = png.with_suffix(".pagebox.png")
    im.save(out)
    text = sh(["tesseract", str(out), "-", "--psm", "7",
               "-c", "tessedit_char_whitelist=0123456789"]).stdout.strip()
    return (int(text) if text.isdigit() else None), text


def collect_app_area(run_dir):
    """Copy the app's log back next to the results, then delete its area."""
    area = app_area(run_dir)
    for f in list(area.glob("*.log")) + list(area.glob("*.jsonl")):
        shutil.copyfile(f, pathlib.Path(run_dir) / f.name)
    shutil.rmtree(area, ignore_errors=True)


def app_area(run_dir):
    """Where the APP under test reads and writes: its isolated HOME, its log and
    the document copy. Never under ~/Documents: macOS protects that folder, and
    every rebuild of the ad-hoc-signed bundle is a new app to it, so the app's
    first file read there blocks on an "Excise would like to access files in
    your Documents folder" prompt (2026-09-17: four launches stuck in
    WindowSettings.Load until killed). Results stay in logs/; only the files
    the app itself touches live here."""
    rel = pathlib.Path(run_dir).resolve().relative_to(ROOT / "logs")
    area = pathlib.Path("/private/tmp/excise-reader-bench") / rel
    area.mkdir(parents=True, exist_ok=True)
    return area


# ----------------------------------------------------------------- sampler

class Tracker:
    """Owns the set of processes attributed to one app and samples them."""

    def __init__(self, app_cfg, main_pid, baseline_pids, t0):
        self.cfg = app_cfg
        self.main = main_pid
        self.baseline = baseline_pids
        self.t0 = t0
        self.known = {}        # pid -> {comm, firstSeen, lastSeen, cpu, rssKB}
        self.samples = []
        self.lock = threading.Lock()
        self.stop = threading.Event()

    def candidates(self, table):
        """Processes macOS holds this app responsible for, plus descendants."""
        owned = {self.main}
        for pid in table:
            if pid != self.main and responsible_pid(pid) == self.main:
                owned.add(pid)
        changed = True
        while changed:
            changed = False
            for pid, r in table.items():
                if pid not in owned and r["ppid"] in owned:
                    owned.add(pid); changed = True
        return owned

    def sample(self, label=None):
        table = ps_table()
        now = time.time() - self.t0
        with self.lock:
            owned = self.candidates(table)
            for pid in owned:
                r = table.get(pid)
                if not r:
                    continue
                k = self.known.setdefault(pid, {"comm": r["comm"], "firstSeen": now})
                k.update(lastSeen=now, cpu=r["cpu"], rssKB=r["rssKB"])
            fps = footprints(sorted(p for p in owned if p in table))
            for pid, f in fps.items():
                k = self.known.get(pid)
                if k is not None:
                    k["peakFp"] = max(k.get("peakFp", 0), f["peak"])
            rec = {"t": round(now, 3), "label": label,
                   "procs": {str(p): {"rss": table[p]["rssKB"] * 1024, "cpu": table[p]["cpu"],
                                      "fp": fps.get(p, {}).get("fp")} for p in owned if p in table}}
            self.samples.append(rec)
            return rec

    def cpu_total(self, pids=None):
        with self.lock:
            return sum(k.get("cpu", 0) for p, k in self.known.items() if pids is None or p in pids)

    def loop(self, interval):
        while not self.stop.wait(interval):
            try:
                self.sample()
            except Exception as e:                      # a sampler must not kill a run
                self.samples.append({"t": time.time() - self.t0, "error": str(e)})


def wait_settled(tracker, settle):
    """Wait until the app's summed CPU stays under the threshold."""
    deadline = time.time() + settle["timeoutSeconds"]
    quiet = 0
    last_cpu, last_t = tracker.cpu_total(), time.time()
    while time.time() < deadline:
        time.sleep(1.0)
        tracker.sample()
        cpu, t = tracker.cpu_total(), time.time()
        pct = (cpu - last_cpu) / max(1e-6, t - last_t) * 100
        last_cpu, last_t = cpu, t
        quiet = quiet + 1 if pct < settle["cpuPercentBelow"] else 0
        if quiet >= settle["quietSeconds"]:
            return True
    return False


# ----------------------------------------------------------------- one run

class RunFailed(Exception):
    pass


def launch(app_id, app, doc_copy, run_dir, cfg, excise_app, extra_env=None):
    """Launch every app the same way: `open -n -F -a <bundle>`.

    Launching excise directly (Popen) made this script's own process tree
    responsible for excise's helper services, so they would have been charged
    to nobody. Through `open`, launchd is the parent and each app is
    responsible for itself. excise's state goes to an isolated HOME (`--env`).
    """
    before = set(ps_table())
    t0 = time.time()
    args = ["open", "-n", "-F"]
    if app["launch"] == "exec":
        bundle = str(pathlib.Path(excise_app).resolve())
        home = app_area(run_dir) / "home"
        conf = home / "Library/Application Support/Excise.App"
        conf.mkdir(parents=True, exist_ok=True)
        w = cfg["window"]
        (conf / "window.json").write_text(json.dumps(
            {"X": w["x"], "Y": w["y"], "Width": w["width"], "Height": w["height"], "IsMaximized": False}))
        log = str(app_area(run_dir) / "app.log")
        args += ["-a", bundle, "--env", f"HOME={home}", "--stdout", log, "--stderr", log]
        for k, v in (extra_env or {}).items():
            args += ["--env", f"{k}={v}"]
        match = bundle + "/Contents/MacOS/Excise.App"
    else:
        args += ["-a", app["bundlePath"]]
        match = None
    if doc_copy:
        args += (["--args"] if app["launch"] == "exec" else []) + [str(doc_copy)]
    sh(args, check=True)
    pid = None
    for _ in range(60):
        for p, r in ps_table().items():
            if p in before:
                continue
            if (match and r["comm"] == match) or (not match and r["comm"].endswith(app["processPath"])):
                pid = p
                break
        if pid:
            break
        time.sleep(0.5)
    if not pid:
        raise RunFailed("app process never appeared")
    return pid, before, t0


def wait_window(pid, timeout=60):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if main_window_id(pid):
            return True
        time.sleep(0.5)
    return False


def quit_app(app, pid):
    try:
        keystroke(pid, "q")
    except RuntimeError:
        pass
    for _ in range(40):
        if pid not in ps_table():
            return True
        time.sleep(0.5)
    return False


def read_page(app_id, pid, run_dir, label, win_cfg):
    """Return (page, evidence). Preview reports it in the title; others by OCR."""
    wid = main_window_id(pid)
    png = run_dir / f"{label}.png"
    if wid:
        sh(["screencapture", "-x", "-o", "-l", str(wid), str(png)])
    if app_id == "preview":
        title = window_title(pid)
        m = re.search(r"Page (\d+) of (\d+)", title)
        return (int(m.group(1)) if m else None), title
    if not png.exists():
        return None, "no screenshot"
    scale = 2
    if app_id == "excise":
        # status bar page indicator, bottom right (retina pixels)
        region = (int((win_cfg["width"] - 350) * scale), int((win_cfg["height"] - 45) * scale), 350 * scale, 45 * scale)
    else:
        return ocr_acrobat_page_box(png, win_cfg)
    return ocr_page(png, region)


def tolerated_start(app_id, label, page):
    """Preview reports the page occupying most of the view. Resizing its window
    right after opening shifts the scroll position, and on Altona's short
    landscape pages that makes page 2 the reported one (5 of 5 runs on
    2026-09-16; Home still reads page 1 later in the same run). The document is
    open and at its start, which is all verify-start is for."""
    return app_id == "preview" and label == "verify-start" and page == 2


def one_run(app_id, app, doc, repeat, out, cfg, excise_app, extra_env=None):
    run_dir = out / app_id / (doc["id"] if doc else "empty") / f"r{repeat}"
    run_dir.mkdir(parents=True, exist_ok=True)
    doc_copy = None
    if doc:
        # A fresh path per run: no app can restore a last page or zoom for it.
        doc_copy = app_area(run_dir) / f"{doc['id']}-{app_id}-r{repeat}-{int(time.time())}.pdf"
        shutil.copyfile(ROOT / doc["path"], doc_copy)

    steps, failures = [], []
    park_pointer()
    pid, before, t0 = launch(app_id, app, doc_copy, run_dir, cfg, excise_app, extra_env)
    tracker = Tracker(app, pid, before, t0)
    sampler = threading.Thread(target=tracker.loop, args=(1.0,), daemon=True)
    sampler.start()

    def boundary(label, ok=True, note=None, page=None, expect=None):
        rec = tracker.sample(label)
        steps.append({"step": label, "t": rec["t"], "ok": ok, "note": note,
                      "page": page, "expectPage": expect})
        if not ok:
            failures.append(label)

    def verify_open(label):
        """The document must really be open: a page indicator alone reads "Page 1"
        on excise's empty window too."""
        stem = doc_copy.stem
        if app_id == "preview":
            title = window_title(pid)
            ok, evidence = stem in title, title
        elif app_id == "excise":
            png = run_dir / f"{label}.png"
            wid = main_window_id(pid)
            if wid:
                sh(["screencapture", "-x", "-o", "-l", str(wid), str(png)])
            crop = png.with_suffix(".title.png")
            sh(["sips", "-c", "70", str(cfg["window"]["width"] * 2 - 600), "--cropOffset", "0", "300",
                str(png), "--out", str(crop)])
            evidence = sh(["tesseract", str(crop), "-", "--psm", "7"]).stdout.strip()
            ok = "No document" not in evidence and stem.split("-")[0] in evidence
        else:
            # Acrobat titles the window with the PDF's /Title, not the file name,
            # and names its Home window "Acrobat".
            title = window_title(pid)
            ok = bool(title) and title not in ("Acrobat", "Adobe Acrobat") and not title.startswith("Welcome")
            evidence = title
        boundary(label, ok=ok, note=None if ok else f"document not open: {evidence[:80]!r}")
        if not ok:
            raise RunFailed(f"{label}: document not open")

    def verify(label, expect):
        page, evidence = read_page(app_id, pid, run_dir, label, cfg["window"])
        ok = page == expect or tolerated_start(app_id, label, page)
        boundary(label, ok=ok, page=page, expect=expect,
                 note=None if ok else f"expected page {expect}, read {page!r} from {evidence[:80]!r}")

    try:
        # An EMPTY launch is each app's natural empty state, whatever that is:
        # excise shows its window, Acrobat its Home screen, and Preview only an
        # Open panel -- which belongs to a helper process, not to Preview. So
        # only a document run requires, and sizes, a window of the app's own.
        if doc is not None:
            if not wait_window(pid):
                raise RunFailed("no window within 60 s")
            try:
                set_window(pid, cfg["window"])
            except RuntimeError as e:
                raise RunFailed(f"could not size the window: {e}")
        else:
            time.sleep(5)
        settled = wait_settled(tracker, cfg["settle"])
        boundary("opened", ok=settled, note=None if settled else "never settled")

        if doc is None:
            time.sleep(30); boundary("idle-30s")
        else:
            verify_open("document-open")
            verify("verify-start", 1)
            nk = app["nextPage"]
            key(pid, nk["keyCode"], nk["modifiers"], repeat=30, gap=cfg["pageStepSeconds"])
            wait_settled(tracker, cfg["settle"])
            verify("paged-30", 31 if doc["pages"] >= 31 else doc["pages"])
            time.sleep(20); boundary("idle-20s-after-paging")

            key(pid, 119)                               # End
            wait_settled(tracker, cfg["settle"])
            verify("at-end", doc["pages"])
            key(pid, 115)                               # Home
            wait_settled(tracker, cfg["settle"])
            verify("back-at-start", 1)
            time.sleep(30); boundary("idle-30s")

            keystroke(pid, "w")                         # close the document
            wait_settled(tracker, cfg["settle"])
            time.sleep(20); boundary("closed-idle-20s")
    except (RunFailed, RuntimeError, subprocess.TimeoutExpired) as e:
        failures.append(f"aborted: {e}")
    finally:
        if any("no window" in f for f in failures) and pid in ps_table():
            # A launch that never shows a window is a finding, not noise: excise
            # hung once in ~10 launches on 2026-09-16 right after "Creating main
            # window", at 0.28 s CPU in 77 s. Capture where it is stuck before
            # the process is killed.
            sh(["sample", str(pid), "3", "-file", str(run_dir / "hang.sample.txt")], timeout=60)
            if app_id == "excise":
                stack = sh([os.path.expanduser("~/.dotnet/tools/dotnet-stack"), "report", "-p", str(pid)], timeout=60)
                (run_dir / "hang.dotnet-stack.txt").write_text(stack.stdout + stack.stderr)
        pre_quit = tracker.sample("pre-quit")
        quit_t = time.time() - t0
        clean = quit_app(app, pid)
        tracker.stop.set()
        sampler.join(timeout=5)
        if not clean:
            failures.append("did not quit within 20 s; killed")
            try:
                os.kill(pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        time.sleep(10)
        alive_after = set(ps_table())
        collect_app_area(run_dir)

    # Attribution was decided per sample by responsibility, so every process
    # ever seen is owned. Helpers that OUTLIVED the app are reported, because
    # a pooled service still holding memory after quit is worth knowing.
    owned = list(tracker.known)
    excluded = [{"pid": p, "comm": k["comm"]} for p, k in tracker.known.items()
                if p != pid and p in alive_after]
    result = {
        "app": app_id, "doc": doc["id"] if doc else "empty", "repeat": repeat,
        "mainPid": pid, "failures": failures, "steps": steps,
        "owned": {str(p): tracker.known[p] for p in owned},
        "ownedButOutlivedApp": excluded,
        "samples": tracker.samples, "quitAt": quit_t,
        "load1": os.getloadavg()[0],
    }
    (run_dir / "result.json").write_text(json.dumps(result, indent=1))
    print(f"  {app_id:8} {result['doc']:7} r{repeat}  "
          f"{'OK' if not failures else 'FAIL ' + '; '.join(failures)}", flush=True)
    return result


# ----------------------------------------------------------------- summary

def totals(sample, owned):
    fp = rss = 0
    for p, v in sample.get("procs", {}).items():
        if p in owned:
            fp += v.get("fp") or 0
            rss += v.get("rss") or 0
    return fp, rss


def summarize(out):
    results = [json.loads(p.read_text()) for p in sorted(out.glob("*/*/r*/result.json"))]
    rows = {}
    for r in results:
        owned = set(r["owned"])
        main = str(r["mainPid"])
        key_ = (r["app"], r["doc"])
        row = rows.setdefault(key_, {"runs": 0, "failed": 0, "at": {}, "peakFp": [], "peakFpMain": [],
                                     "cpu": [], "idleCpu": [], "procCount": []})
        row["runs"] += 1
        tolerated = {st["step"] for st in r["steps"]
                     if not st["ok"] and tolerated_start(r["app"], st["step"], st.get("page"))}
        r["failures"] = [f for f in r["failures"] if f not in tolerated]
        if r["failures"]:
            row["failed"] += 1
            continue
        for s in r["samples"]:
            if s.get("label"):
                fp, rss = totals(s, owned)
                mfp = (s["procs"].get(main) or {}).get("fp") or 0
                row["at"].setdefault(s["label"], []).append((fp, rss, mfp))
        row["peakFp"].append(max((totals(s, owned)[0] for s in r["samples"] if "procs" in s), default=0))
        row["peakFpMain"].append(r["owned"][main].get("peakFp", 0))
        row["cpu"].append(sum(k.get("cpu", 0) for k in r["owned"].values()))
        row["procCount"].append(len(owned))
        # idle CPU: the last idle window before quit
        labelled = [s for s in r["samples"] if s.get("label")]
        for a, b in zip(labelled, labelled[1:]):
            if b["label"] in ("idle-30s",) and a["label"] in ("back-at-start", "opened"):
                ca = sum(v["cpu"] for p, v in a["procs"].items() if p in owned)
                cb = sum(v["cpu"] for p, v in b["procs"].items() if p in owned)
                row["idleCpu"].append((cb - ca) / max(1e-6, b["t"] - a["t"]) * 100)

    def med(xs):
        return statistics.median(xs) if xs else None

    def spread(xs):
        return (max(xs) - min(xs)) if len(xs) > 1 else None

    summary = {"generated": time.strftime("%Y-%m-%dT%H:%M:%S"), "rows": []}
    lines = ["# Reader benchmark: excise vs Preview vs Adobe Acrobat (#1543)", "",
             f"Run: `{out}`", "",
             "Footprint = macOS physical footprint, summed over every process the app owns "
             "(main + children + per-app helper services that exit with it). Medians over "
             "successful repeats; ± is the min–max spread. MB.", ""]
    order = ["empty", "w9", "irs", "altona", "scan"]
    labels = ["opened", "paged-30", "idle-20s-after-paging", "idle-30s", "closed-idle-20s"]
    for doc in order:
        present = [(a, d) for (a, d) in rows if d == doc]
        if not present:
            continue
        lines += [f"## {doc}", "",
                  "| app | runs ok | procs | " + " | ".join(labels) + " | peak (all) | peak (main) | CPU s | idle CPU % |",
                  "|---|---|---:|" + "---:|" * len(labels) + "---:|---:|---:|---:|"]
        for app in ["excise", "preview", "acrobat"]:
            row = rows.get((app, doc))
            if not row:
                continue
            cells = []
            for lb in labels:
                vals = [v[0] / MB for v in row["at"].get(lb, [])]
                cells.append("—" if not vals else f"{med(vals):.0f}" + (f" ±{spread(vals):.0f}" if spread(vals) is not None else ""))
            pk = [v / MB for v in row["peakFp"]]
            pkm = [v / MB for v in row["peakFpMain"]]
            lines.append(f"| {app} | {row['runs'] - row['failed']}/{row['runs']} | {med(row['procCount']) or '—'} | "
                         + " | ".join(cells)
                         + f" | {med(pk):.0f} | {med(pkm):.0f} | {med(row['cpu']):.1f} | "
                         + (f"{med(row['idleCpu']):.2f}" if row["idleCpu"] else "—") + " |"
                         if pk else f"| {app} | 0/{row['runs']} | — |" + " — |" * (len(labels) + 4))
            summary["rows"].append({"app": app, "doc": doc, **{k: v for k, v in row.items() if k != "at"},
                                    "at": {lb: [list(x) for x in v] for lb, v in row["at"].items()}})
        lines.append("")
    fails = [(r["app"], r["doc"], r["repeat"], r["failures"]) for r in results if r["failures"]]
    tolerated_runs = sum(1 for r in results for st in r["steps"]
                         if not st["ok"] and tolerated_start(r["app"], st["step"], st.get("page")))
    if tolerated_runs:
        lines += [f"Note: {tolerated_runs} Preview start check(s) read page 2 after the window resize and were "
                  "accepted (see tolerated_start).", ""]
    if fails:
        lines += ["## Failed runs (excluded from the medians)", ""]
        lines += [f"- {a} {d} r{k}: {'; '.join(f)}" for a, d, k, f in fails]
    (out / "summary.md").write_text("\n".join(lines) + "\n")
    (out / "summary.json").write_text(json.dumps(summary, indent=1))
    print("\n".join(lines))


# ----------------------------------------------------------------- main

def preflight(apps):
    busy = sh(["pgrep", "-fl", r"dotnet (test|build)|run-full-suite|testhost"]).stdout.strip()
    if busy:
        sys.exit(f"refusing to run beside test/build processes:\n{busy}")
    table = ps_table()
    for app_id, app in apps.items():
        if app["launch"] == "open":
            running = [p for p, r in table.items() if r["comm"].endswith(app["processPath"])]
            if running:
                sys.exit(f"{app['name']} is already running (pid {running}); quit it first")
    if not any(r["comm"].rsplit("/", 1)[-1] == "caffeinate" for r in table.values()):
        print("WARNING: no caffeinate running; the screen may lock and block keystrokes", flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apps", default="excise,preview,acrobat")
    ap.add_argument("--docs", default="empty,w9,irs,altona,scan")
    ap.add_argument("--repeats", type=int)
    ap.add_argument("--excise-app", default=str(ROOT / "logs/reader-bench-bundle/excise.app"))
    ap.add_argument("--out")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--summarize")
    a = ap.parse_args()

    if a.summarize:
        summarize(pathlib.Path(a.summarize)); return

    cfg = json.loads(CONFIG.read_text())
    apps = {k: v for k, v in cfg["apps"].items() if k in a.apps.split(",")}
    want = a.docs.split(",")
    docs = [d for d in cfg["documents"] if d["id"] in want]
    for d in docs:
        p = ROOT / d["path"]
        if not p.exists():
            sys.exit(f"missing fixture {p}" + (f" (run {d['make']})" if d.get("make") else ""))
        d["pages"] = int(sh(["qpdf", "--show-npages", str(p)], check=True).stdout)
    plan_docs = ([None] if "empty" in want else []) + docs
    repeats = a.repeats or cfg["repeats"]
    runs = [(r, d, app) for r in range(1, repeats + 1) for d in plan_docs for app in apps]
    if a.list:
        for r, d, app in runs:
            print(f"r{r} {app:8} {d['id'] if d else 'empty'}")
        print(f"{len(runs)} runs (~{len(runs) * 2.2:.0f} min)"); return
    if "excise" in apps and not (pathlib.Path(a.excise_app) / "Contents/MacOS/Excise.App").exists():
        sys.exit(f"no excise bundle at {a.excise_app}; build it with scripts/build-macos-app.sh --output logs/reader-bench-bundle")

    preflight(apps)
    # ABSOLUTE: `open` starts the app from "/", so a relative HOME, log or
    # document path silently points nowhere (the first excise test opened no
    # document at all and still read "Page 1" from the empty window).
    out = pathlib.Path(a.out or ROOT / f"logs/reader-bench_{time.strftime('%Y%m%d_%H%M%S')}").resolve()
    out.mkdir(parents=True, exist_ok=True)
    meta = {"sha": sh(["git", "-C", str(ROOT), "rev-parse", "--short", "HEAD"]).stdout.strip(),
            "machine": sh(["sysctl", "-n", "machdep.cpu.brand_string"]).stdout.strip(),
            "memGB": int(sh(["sysctl", "-n", "hw.memsize"]).stdout) // 2**30,
            "macOS": sh(["sw_vers", "-productVersion"]).stdout.strip(),
            "preview": sh(["defaults", "read", "/System/Applications/Preview.app/Contents/Info", "CFBundleShortVersionString"]).stdout.strip(),
            "acrobat": sh(["defaults", "read", "/Applications/Adobe Acrobat DC/Adobe Acrobat.app/Contents/Info", "CFBundleShortVersionString"]).stdout.strip(),
            "config": cfg, "started": time.strftime("%Y-%m-%dT%H:%M:%S")}
    (out / "run-meta.json").write_text(json.dumps(meta, indent=1))
    print(f"==> {len(runs)} runs -> {out}", flush=True)
    for r, d, app in runs:
        one_run(app, apps[app], d, r, out, cfg, a.excise_app)
    summarize(out)


if __name__ == "__main__":
    main()

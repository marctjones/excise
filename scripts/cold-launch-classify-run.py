#!/usr/bin/env python3
"""Classify one run of scripts/run-cold-launch-double-open.sh (#1819, #1629).

Usage: cold-launch-classify-run.py <run dir> <A name> <B name> <delay> <run> "<pids>"

Reads <run dir>/app.log (excise's stdout), state.txt (the AX reader's lines)
and focus.txt, and prints one TSV row:
delay, run, verdict, arrival, processes, focus, detail, state.
Re-runnable on stored evidence, so a classifier fix can re-score old runs.
"""
import re, sys
d, a, b, delay, run, pids = sys.argv[1:7]
npids = len(pids.split())

def ms(ts):
    h, m, s = ts.split(":")
    return int((int(h) * 3600 + int(m) * 60 + float(s)) * 1000)

events = []  # (ms, message)
ts = None
try:
    for raw in open(f"{d}/app.log", errors="replace"):
        m = re.match(r"^(\d\d:\d\d:\d\d\.\d+) \w+:", raw)
        if m:
            ts = ms(m.group(1))
            continue
        if ts is not None and raw.strip():
            events.append((ts, raw.strip()))
except FileNotFoundError:
    pass

def first(pred):
    for t, msg in events:
        if pred(msg):
            return t
    return None

main_window = first(lambda s: s.startswith("Main window created successfully"))
a_step1 = first(lambda s: "STEP 1: LoadDocumentAsync START" in s and s.endswith(a))
# STEP 13 does not name its file, and the two loads can interleave. Match it
# by page count ("PDF loaded. Pages: N, ..., File: <name>"); when A and B have
# the same count, the first completion after A's start is a lower bound.
pages = {}
for _, msg in events:
    m = re.match(r"PDF loaded\. Pages: (\d+), .*File: (.+)$", msg)
    if m:
        pages[m.group(2)] = m.group(1)
a_step13 = None
for t, msg in events:
    if a_step1 is None or t < a_step1 or "STEP 13: LoadDocumentAsync COMPLETE" not in msg:
        continue
    m = re.search(r"Total pages: (\d+)", msg)
    if a in pages and pages.get(a) != pages.get(b) and m and m.group(1) != pages[a]:
        continue
    a_step13 = t
    break
# A load that failed has no STEP 13 and must not borrow B's.
a_failed = first(lambda s: s.startswith("!!! ERROR in LoadDocumentAsync") and s.endswith(a))
if a_failed is not None:
    a_step13 = None
queued = [msg for _, msg in events if msg.startswith("Queued ")]
opens = [(t, msg) for t, msg in events if re.match(r"Opening \d+ PDF\(s\) from a startup/open event", msg)]
# Each open request logs "Opening ..." and then, synchronously, its load's
# STEP 1; pair them to find when B's request reached the workspace.
request_of = {}
pending = None
for t, msg in events:
    if re.match(r"Opening \d+ PDF\(s\) from a startup/open event", msg):
        pending = t
    elif "STEP 1: LoadDocumentAsync START" in msg and pending is not None:
        request_of[msg.rsplit("/", 1)[-1]] = pending
        pending = None

queued_count = sum(int(re.match(r"Queued (\d+)", q).group(1)) for q in queued)
if any(msg.startswith("Opening 2 PDF(s)") for _, msg in opens) or queued_count >= 2:
    arrival = "queued"
elif b in request_of:
    b_req = request_of[b]
    if main_window is not None and b_req < main_window:
        arrival = "queued"
    elif a_step1 is None or b_req < a_step1:
        arrival = "pre"
    elif a_step13 is None or b_req < a_step13:
        arrival = "inload"
    else:
        arrival = "after"
    if a_step1 is not None:
        load = f"{a_step13 - a_step1}ms" if a_step13 is not None else "unfinished"
        arrival += f"(B at A.start{b_req - a_step1:+d}ms, A load {load})"
else:
    arrival = f"unknown(opens={len(opens)},queued={queued_count})"
if a_failed is not None:
    arrival = "A-failed-to-open;" + arrival

windows = []
for raw in open(f"{d}/state.txt", errors="replace"):
    raw = raw.rstrip("\n")
    if raw.startswith("WINDOW\t"):
        _, title, inwin, tabs, status = (raw.split("\t") + [""] * 5)[:5]
        windows.append((title, inwin, tabs, status))

problems = []
present = set()
for title, inwin, tabs, status in windows:
    if tabs.startswith("<"):
        tab_names = [title] if title else []
    else:
        tab_names = [re.sub(r" \[\d+/\d+\]$", "", t) for t in tabs.split("|")]
    for t in tab_names:
        present.add(t)
        if t.lower().startswith("untitled"):
            problems.append(f"tab '{t}'")
    if title and not tabs.startswith("<") and title not in tab_names:
        problems.append(f"title '{title}' names no tab of its window ({tabs})")
    if inwin and title and inwin != title:
        problems.append(f"title bar '{title}' vs in-window title '{inwin}'")
for doc in (a, b):
    if doc not in present:
        problems.append(f"{doc} in no tab or window")
if not windows:
    problems.append("no windows read")

focus = open(f"{d}/focus.txt").read().strip()
stole = "stolen" if "Excise" in focus else "kept"
verdict = "FAIL" if problems else "PASS"
state = " || ".join(f"{t} | {w} | {tb}" for t, w, tb, _ in windows) or "-"
detail = "; ".join(problems) or "-"
print("\t".join([delay, run, verdict, arrival, str(npids), stole, detail, state]))

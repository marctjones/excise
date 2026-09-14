#!/usr/bin/env python3
"""Self / inclusive time per frame from an EVENTED speedscope profile (dotnet-trace).

Body of scripts/profile-render.sh (#1351); usable on any dotnet-trace speedscope file.
Usage: profile_render_analyze.py <speedscope.json> [--profile N|all] [--top 15]
       [--chains REGEX] [--dump-stacks FILE]
dotnet-trace emits CPU_TIME / UNMANAGED_CODE_TIME as pseudo-leaf frames; self
time is charged to the deepest REAL frame and the pseudo-leaf is kept as a tag
(managed CPU vs time in native code called from that frame).
Inclusive time counts a frame once per stack (recursion-safe).
"""
import json, sys, re, collections, argparse

ap = argparse.ArgumentParser()
ap.add_argument("path")
ap.add_argument("--profile", default="all")
ap.add_argument("--top", type=int, default=15)
ap.add_argument("--chains", default=None, help="regex: print parent chains of matching frames")
ap.add_argument("--dump-stacks", default=None, help="write collapsed stacks to this file")
ap.add_argument("--width", type=int, default=140)
# Idle thread-pool workers park in these frames. They are real thread time but not
# work, and after a Parallel.For they can out-rank the actual hotspot in TOP SELF.
ap.add_argument("--hide", default=r"LowLevelLifoSemaphore\.WaitNative|WaitHandle\.WaitOneNoCheck|Monitor\.Wait|Thread\.Sleep",
                help="regex of SELF frames to omit from the TOP SELF listing (totals keep them); '' shows all")
a = ap.parse_args()

d = json.load(open(a.path))
frames = [f["name"] for f in d["shared"]["frames"]]
profiles = d["profiles"]
sel = range(len(profiles)) if a.profile == "all" else [int(a.profile)]
PSEUDO = {"CPU_TIME", "UNMANAGED_CODE_TIME"}

self_t = collections.Counter()
self_kind = collections.Counter()
kind_t = collections.Counter()
incl_t = collections.Counter()
stack_t = collections.Counter()
total = 0.0
per_thread = []
for pi in sel:
    p = profiles[pi]
    stack = []
    last = None
    ptotal = 0.0
    for e in p["events"]:
        at = e["at"]
        if last is not None and stack and at > last:
            dt = at - last
            ptotal += dt
            real = [f for f in stack if frames[f] not in PSEUDO]
            tag = frames[stack[-1]] if frames[stack[-1]] in PSEUDO else "NONE"
            kind_t[tag] += dt
            if real:
                self_t[real[-1]] += dt
                self_kind[(real[-1], tag)] += dt
            for fr in set(real):
                incl_t[fr] += dt
            stack_t[tuple(real)] += dt
        last = at
        if e["type"] == "O":
            stack.append(e["frame"])
        else:
            if stack and stack[-1] == e["frame"]:
                stack.pop()
            elif e["frame"] in stack:
                idx = len(stack) - 1 - stack[::-1].index(e["frame"])
                del stack[idx:]
    per_thread.append((ptotal, p.get("name", str(pi)), pi))
    total += ptotal

per_thread.sort(reverse=True)
print(f"unit={profiles[0].get('unit')} profiles={len(profiles)} total thread time={total:.1f}")
for t, n, pi in per_thread[:8]:
    print(f"  thread[{pi}] {n}: {t:.1f} ({100*t/total:.1f}%)")
print("  kind split:", {k: round(v, 1) for k, v in kind_t.items()})

def short(n):
    n = re.sub(r"\(.*", "", n)
    return n if len(n) <= a.width else "..." + n[-a.width:]

hide = re.compile(a.hide) if a.hide else None
hidden = sum(t for fr, t in self_t.items() if hide and hide.search(frames[fr]))
shown = [(fr, t) for fr, t in self_t.most_common() if not (hide and hide.search(frames[fr]))]
print(f"\nTOP {a.top} SELF (managed CPU_TIME / UNMANAGED_CODE_TIME)"
      + (f" — {hidden:.1f} ({100*hidden/total:.1f}%) idle-wait self time hidden by --hide" if hidden else ""))
for fr, t in shown[:a.top]:
    m = self_kind[(fr, "CPU_TIME")]; u = self_kind[(fr, "UNMANAGED_CODE_TIME")]
    print(f"  {t:8.1f} {100*t/total:5.1f}%  [m {m:7.1f} | u {u:7.1f}]  {short(frames[fr])}")
print(f"\nTOP {a.top} INCLUSIVE")
for fr, t in incl_t.most_common(a.top):
    print(f"  {t:8.1f} {100*t/total:5.1f}%  {short(frames[fr])}")

if a.chains:
    rx = re.compile(a.chains)
    agg = collections.Counter()
    for st, t in stack_t.items():
        for i, fr in enumerate(st):
            if rx.search(frames[fr]):
                parents = tuple(short(frames[x])[-100:] for x in st[max(0, i - 10):i + 1])
                agg[parents] += t
                break
    print(f"\nCHAINS for /{a.chains}/")
    for ch, t in agg.most_common(12):
        print(f"  {t:8.1f} {100*t/total:5.1f}%")
        for c in ch:
            print("        " + c)

if a.dump_stacks:
    with open(a.dump_stacks, "w") as f:
        for st, t in stack_t.most_common():
            f.write(";".join(short(frames[x]) for x in st) + f" {t:.3f}\n")

#!/usr/bin/env bash
#
# Chrome colour-literal gate (#1827, #1992).
#
# App chrome takes its colours from tokens (Excise.App/Styles/Brushes.axaml,
# ThemeDictionaries), so light/dark and the design system switch it in one
# place. A literal colour in a view stays the same in every theme: a light
# panel in dark mode, or a blue that survived a palette change. This scans
# Excise.App/Views/*.axaml and Excise.App/Styles/Controls.axaml for colour
# literals (hex or named) in colour attributes and colour Setters, comments
# stripped, and fails any not listed in tests/chrome-color-allowlist.tsv.
#
# The allowlist holds DOCUMENT data (typewriter ink swatches, sticky-note
# colours), text on a coloured fill (white on the danger/accent fills) and the
# tab strip's deliberate local palette. Each row is file, literal, count, reason:
# the count is exact, so adding one more swatch, or removing one and leaving a
# stale row, both fail.
#
# Usage: scripts/check-chrome-color-literals.sh [repo-root]
set -euo pipefail

ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"

python3 - "$ROOT" <<'PY'
import collections, glob, os, re, sys

root = sys.argv[1]
allow_path = os.path.join(root, "tests", "chrome-color-allowlist.tsv")
files = sorted(glob.glob(os.path.join(root, "Excise.App", "Views", "*.axaml")))
controls = os.path.join(root, "Excise.App", "Styles", "Controls.axaml")
if os.path.exists(controls):
    files.append(controls)
if not files:
    print("FAIL: no .axaml files found under Excise.App/Views (wrong root?)")
    sys.exit(1)
if not os.path.exists(allow_path):
    print(f"FAIL: missing {os.path.relpath(allow_path, root)}")
    sys.exit(1)

NAMED = ("White|Black|Red|Green|Blue|Yellow|Orange|OrangeRed|Gray|Grey|LightGray|DarkGray|"
         "Silver|WhiteSmoke|Gainsboro|Purple|Pink|Brown|Navy|Teal|Maroon|Olive|Lime|Aqua|Fuchsia|"
         "DodgerBlue|SteelBlue|SlateGray|Crimson|Gold|Ivory|Beige|Khaki|Coral|Tomato|Salmon")
LITERAL = r"(#[0-9A-Fa-f]{3,8}|(?:%s))" % NAMED
COLOR_PROPS = "Background|Foreground|BorderBrush|Fill|Stroke|Color|CaretBrush|SelectionBrush"
attr_re = re.compile(r'\b(?:%s)="%s"' % (COLOR_PROPS, LITERAL))
setter_re = re.compile(r'<Setter\s+Property="(?:%s)"\s+Value="%s"' % (COLOR_PROPS, LITERAL))

found = collections.Counter()
where = collections.defaultdict(list)
for path in files:
    rel = os.path.relpath(path, root)
    text = open(path, encoding="utf-8").read()
    # Blank out comments but keep line numbers.
    text = re.sub(r"<!--.*?-->", lambda m: re.sub(r"[^\n]", " ", m.group(0)), text, flags=re.S)
    for n, line in enumerate(text.split("\n"), 1):
        for m in list(attr_re.finditer(line)) + list(setter_re.finditer(line)):
            lit = m.group(1).upper() if m.group(1).startswith("#") else m.group(1)
            found[(rel, lit)] += 1
            where[(rel, lit)].append(n)

allowed = {}
for raw in open(allow_path, encoding="utf-8"):
    line = raw.rstrip("\n")
    if not line.strip() or line.startswith("#"):
        continue
    cols = line.split("\t")
    if len(cols) < 4 or not cols[3].strip():
        print(f"FAIL: allowlist row needs file, literal, count, reason: {line!r}")
        sys.exit(1)
    lit = cols[1].upper() if cols[1].startswith("#") else cols[1]
    allowed[(cols[0], lit)] = int(cols[2])

bad = []
for key, count in sorted(found.items()):
    ok = allowed.get(key, 0)
    if count > ok:
        bad.append(f"  {key[0]}:{','.join(map(str, where[key]))}  {key[1]} x{count} (allowed {ok}) — use a token from Styles/Brushes.axaml")
stale = [f"  {k[0]}  {k[1]} allowed {v}, found {found.get(k, 0)}"
         for k, v in sorted(allowed.items()) if found.get(k, 0) < v]

if bad or stale:
    if bad:
        print("FAIL: chrome colour literals outside the allowlist (#1827):")
        print("\n".join(bad))
    if stale:
        print("FAIL: stale allowlist rows (fewer literals than allowed; lower or remove the row):")
        print("\n".join(stale))
    sys.exit(1)

total = sum(found.values())
print(f"==> chrome colour-literal audit OK ({len(files)} files; {total} literals, all allowlisted document data or text-on-fill)")
PY

#!/usr/bin/env bash
# check-font-width-sync.sh — #1143: the standard-14 widths exist in two places
# (Excise.Core/Fonts/StandardFontMetrics.cs and
# tests/redaction-corpus/std14-widths.json, read by the corpus generator).
# A silent drift between them corrupts the recall benchmark without any gate
# noticing. This asserts the JSON matches the C# source for codes 32-126.
#
# Cheap and static: parses both, compares. No build, no corpus.

set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
JSON="$ROOT/tests/redaction-corpus/std14-widths.json"
SRC="$ROOT/Excise.Core/Fonts/StandardFontMetrics.cs"

GREEN=$'\033[32m'; RED=$'\033[31m'; RESET=$'\033[0m'
[ -f "$JSON" ] || { echo "${RED}missing $JSON${RESET}"; exit 1; }
[ -f "$SRC" ] || { echo "${RED}missing $SRC${RESET}"; exit 1; }

python3 - "$JSON" "$SRC" <<'PY'
import json, re, sys
jpath, spath = sys.argv[1], sys.argv[2]
jw = json.load(open(jpath))

# Parse the C# short[] tables: `private static readonly short[] Name = { ... };`
src = open(spath).read()
tables = {}
for m in re.finditer(r'short\[\]\s+(\w+)\s*=\s*\{([^}]*)\}', src):
    name, body = m.group(1), m.group(2)
    # Strip // comments first: the tables carry `// 32-41` range labels whose
    # digits would otherwise be counted as widths.
    body = re.sub(r'//[^\n]*', '', body)
    nums = [int(x) for x in re.findall(r'-?\d+', body)]
    if len(nums) == 95:   # codes 32-126
        tables[name] = nums

# Map JSON face names (PostScript) to C# table identifiers.
alias = {
    "Helvetica":"Helvetica","Helvetica-Bold":"HelveticaBold",
    "Helvetica-Oblique":"HelveticaOblique",
    "Times-Roman":"TimesRoman","Times-Bold":"TimesBold","Times-Italic":"TimesItalic",
    "Courier":"Courier",
}
problems = 0
for face, widths in jw.items():
    cs = alias.get(face)
    if cs is None:
        continue  # generator carries faces the C# table may not (e.g. Courier-Bold); skip
    if cs not in tables:
        print(f"  MISSING C# table for {face} ({cs})"); problems += 1; continue
    for i in range(95):
        if widths[i] != tables[cs][i]:
            print(f"  DRIFT {face} code {i+32}: json={widths[i]} src={tables[cs][i]}")
            problems += 1
            if problems > 20: break
if problems:
    print(f"\n\033[31m{problems} width mismatch(es) — StandardFontMetrics.cs and the corpus JSON have drifted\033[0m")
    sys.exit(1)
print("\033[32m✓ standard-14 widths in sync (JSON matches StandardFontMetrics for 32-126)\033[0m")
PY

#!/usr/bin/env bash
#
# XAML resource-key audit (#1800).
#
# Every {DynamicResource X} / {StaticResource X} in Excise.App/**/*.axaml must
# name a key the app defines with x:Key="X", or one listed in THEME_KEYS below.
#
# A DynamicResource whose key resolves to nothing leaves the property unset and
# reports nothing: no build error, no exception, no log line. #1800 found three
# (AccentBrush twice, MonospaceFontFamily once) that had been dead since they
# were written.
#
# What this does NOT prove: that the definition is in SCOPE where the key is
# used. It checks "defined somewhere in the app". Icons.axaml is included per
# window and the tab-strip brushes are scoped to their UserControl, so a key
# used outside its scope still passes. The same rigour as shell-xaml's PathIcon
# check; it closes the misspelt / never-defined / deleted-definition class.
#
# THEME_KEYS holds keys the loaded themes (FluentAvaloniaTheme,
# Avalonia.Themes.Fluent) provide, which the app therefore does not define.
# It is empty: nothing in the app references a theme key today. Add one only
# after confirming the loaded theme really resolves it, and say where.
#
# Usage: scripts/check-xaml-resource-keys.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 - <<'PY'
import os
import re
import sys

APP = "Excise.App"

# key -> where the loaded theme defines it
THEME_KEYS = {}

REFERENCE = re.compile(
    r"\{(Dynamic|Static)Resource\s+(?:ResourceKey\s*=\s*)?([A-Za-z_][A-Za-z0-9_.]*)")
DEFINITION = re.compile(r'\bx:Key\s*=\s*"([^"{][^"]*)"')
COMMENT = re.compile(r"<!--.*?-->", re.S)


def blank_comments(text):
    # Keep newlines so reported line numbers still match the file.
    return COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)


if not os.path.isdir(APP):
    print(f"FAIL: XAML resource-key audit (#1800): {APP}/ not found under {os.getcwd()}")
    sys.exit(1)

files = []
for dirpath, dirnames, filenames in os.walk(APP):
    dirnames[:] = sorted(d for d in dirnames if d not in ("bin", "obj"))
    files.extend(os.path.join(dirpath, f) for f in sorted(filenames) if f.endswith(".axaml"))

defined = set()
references = []  # (path, line, kind, key)
for path in files:
    with open(path, encoding="utf-8") as fh:
        text = blank_comments(fh.read())
    defined.update(DEFINITION.findall(text))
    for m in REFERENCE.finditer(text):
        line = text.count("\n", 0, m.start()) + 1
        references.append((path, line, m.group(1), m.group(2)))

failures = []
if not files:
    failures.append(f"no .axaml files under {APP}/: the scan is vacuous")
if files and not references:
    failures.append(f"no {{Dynamic,Static}}Resource references in {len(files)} .axaml files: "
                    "the scan is vacuous, or the reference pattern stopped matching")
for path, line, kind, key in references:
    if key not in defined and key not in THEME_KEYS:
        failures.append(f"{path}:{line}: {{{kind}Resource {key}}} has no x:Key definition "
                        "in Excise.App and is not a declared theme key")

if failures:
    print("FAIL: XAML resource-key audit (#1800):")
    for line in failures:
        print("  - " + line)
    sys.exit(1)

keys = {r[3] for r in references}
print(f"==> XAML resource-key audit OK ({len(references)} references to {len(keys)} keys "
      f"across {len(files)} .axaml files resolve; {len(THEME_KEYS)} declared theme keys)")
PY

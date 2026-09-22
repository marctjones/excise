#!/usr/bin/env bash
#
# Main-shell XAML audit (#559, #1773).
#
# The three grep-style assertions of VisualPolishAuditTests, moved out of the
# 8 GB Excise.App.Tests host where they only ran at t1. They read three .axaml
# files and need no Avalonia, no build and no document.
#
#   1. The shell uses VECTOR icons for its toolbar and menu affordances: the
#      icon resources and the two PathIcon styles exist, and no toolbar/menu
#      item went back to an emoji Content/Text.
#   2. Every PathIcon StaticResource in MainWindow.axaml resolves to a key that
#      Icons.axaml defines. A key that does not resolve is a runtime failure on
#      opening the window, not a build error.
#   3. The audited toolbar/menu surface keeps its CommandId (the accessibility
#      command ids automation drives) and the primary tooltips.
#
# The screenshot-capture test in VisualPolishAuditTests stays in the App suite:
# it renders real windows and needs the headless Avalonia session.
#
# Usage: scripts/check-shell-xaml.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 - <<'PY'
import re
import sys

MAIN = "Excise.App/Views/MainWindow.axaml"
ICONS = "Excise.App/Styles/Icons.axaml"
CONTROLS = "Excise.App/Styles/Controls.axaml"

failures = []


def read(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return fh.read()
    except OSError as ex:
        failures.append(f"cannot read {path}: {ex}")
        return ""


main = read(MAIN)
icons = read(ICONS)
controls = read(CONTROLS)


def require(text, needle, where, why):
    if needle not in text:
        failures.append(f"{where} does not contain {needle!r} ({why})")


def forbid(text, needle, where, why):
    if needle in text:
        failures.append(f"{where} contains {needle!r} ({why})")


# 1. Vector icons for toolbar and menu affordances.
for needle in ("IconFolderOpen", "IconRedact",
               'PathIcon Classes="toolbar-icon"', 'PathIcon Classes="menu-icon"'):
    require(main, needle, MAIN, "the shell must use vector PathIcons")
for emoji_prefix in ('Content="\U0001F4C1', 'Content="\U0001F4BE',
                     'Content="\U0001F50D', 'Content="\U0001F4CB',
                     'Text="\U0001F4C4'):
    forbid(main, emoji_prefix, MAIN, "an emoji glyph replaced a vector icon")
for key in ("IconFolderOpen", "IconSave", "IconSearch", "IconRedact", "IconPage"):
    require(icons, key, ICONS, "the icon resource must be defined")
for style in ("PathIcon.toolbar-icon", "PathIcon.menu-icon"):
    require(controls, style, CONTROLS, "the PathIcon style must be defined")

# 2. Every referenced PathIcon resource resolves.
referenced = sorted(set(re.findall(
    r'PathIcon\b[^>]*\bData="\{StaticResource (Icon[A-Za-z0-9_]+)\}"', main, re.S)))
defined = set(re.findall(r'x:Key="(Icon[A-Za-z0-9_]+)"', icons))
if not referenced:
    failures.append(f"{MAIN} references no PathIcon resources: the scan is vacuous, or the shell lost its icons")
unresolved = [k for k in referenced if k not in defined]
if unresolved:
    failures.append("PathIcon StaticResource keys with no definition in "
                    f"{ICONS}: {', '.join(unresolved)}")

# 3. The audited command surface and primary tooltips.
required_commands = [
    "view.toggleOutline", "view.toggleThumbnails", "view.toggleContinuous",
    "app.open", "app.save", "form.saveFlattenedCopy",
    "edit.selectTextMode", "edit.typewriterMode",
    "annotation.toggleHighlightMode", "annotation.addStickyNote",
    "redaction.toggleMode", "redaction.apply",
    "form.toggleAuthoring", "form.autoDetectFields", "search.open",
    "document.rotateLeft", "document.rotateRight",
    "view.zoomOut", "view.zoomIn", "view.zoomFitWidth",
]
for command in required_commands:
    require(main, f'CommandId="{command}"', MAIN,
            "part of the audited toolbar/menu surface")
for tip in ('ToolTip.Tip="Open PDF (Ctrl+O)"', 'ToolTip.Tip="Redaction Mode (R)"',
            'ToolTip.Tip="Form Authoring Mode', 'ToolTip.Tip="Find Text (Ctrl+F)"'):
    require(main, tip, MAIN, "primary toolbar tooltip")

if failures:
    print("FAIL: main-shell XAML audit (#559):")
    for line in failures:
        print("  - " + line)
    sys.exit(1)

print(f"==> main-shell XAML audit OK ({len(referenced)} PathIcon keys resolve, "
      f"{len(required_commands)} command ids present)")
PY

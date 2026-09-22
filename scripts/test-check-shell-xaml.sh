#!/usr/bin/env bash
#
# Regression test for the #559/#1773 main-shell XAML audit.
#
# Standalone-reproduction convention (scripts/test-check-fixture-locators.sh):
# copy the real script into a synthetic repo holding minimal MainWindow.axaml /
# Icons.axaml / Controls.axaml, PLANT each defect the audit exists to catch,
# watch it go RED, restore, watch it go GREEN. A gate that has never been seen
# red is not accepted (#1012).
#
# Usage: scripts/test-check-shell-xaml.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAIL=0
RC=0

COMMANDS=(view.toggleOutline view.toggleThumbnails view.toggleContinuous app.open app.save
          form.saveFlattenedCopy edit.selectTextMode edit.typewriterMode
          annotation.addHighlight annotation.addStickyNote redaction.toggleMode redaction.apply
          form.toggleAuthoring form.autoDetectFields search.open document.rotateLeft
          document.rotateRight view.zoomOut view.zoomIn view.zoomFitWidth)

write_main() { # write_main <repo> [extra-line]
  local repo="$1" extra="${2:-}"
  {
    echo '<Window>'
    echo '  <Button ToolTip.Tip="Open PDF (Ctrl+O)"><PathIcon Classes="toolbar-icon" Data="{StaticResource IconFolderOpen}" /></Button>'
    echo '  <Button ToolTip.Tip="Redaction Mode (R)"><PathIcon Classes="toolbar-icon"'
    echo '      Data="{StaticResource IconRedact}" /></Button>'
    echo '  <Button ToolTip.Tip="Form Authoring Mode (F)" />'
    echo '  <Button ToolTip.Tip="Find Text (Ctrl+F)" />'
    echo '  <MenuItem><MenuItem.Icon><PathIcon Classes="menu-icon" Data="{StaticResource IconSave}" /></MenuItem.Icon></MenuItem>'
    for c in "${COMMANDS[@]}"; do echo "  <Button CommandId=\"$c\" />"; done
    [ -n "$extra" ] && echo "  $extra"
    echo '</Window>'
  } > "$repo/Excise.App/Views/MainWindow.axaml"
}

write_icons() { # write_icons <repo> [omit-key]
  local repo="$1" omit="${2:-}"
  {
    echo '<ResourceDictionary>'
    for k in IconFolderOpen IconSave IconSearch IconRedact IconPage; do
      [ "$k" = "$omit" ] && continue
      echo "  <StreamGeometry x:Key=\"$k\">M0,0</StreamGeometry>"
    done
    echo '</ResourceDictionary>'
  } > "$repo/Excise.App/Styles/Icons.axaml"
}

write_controls() {
  printf '<Styles><Style Selector="PathIcon.toolbar-icon" /><Style Selector="PathIcon.menu-icon" /></Styles>\n' \
    > "$1/Excise.App/Styles/Controls.axaml"
}

make_repo() { # make_repo <repo>
  local repo="$1"
  mkdir -p "$repo/scripts" "$repo/Excise.App/Views" "$repo/Excise.App/Styles"
  cp "$HERE/check-shell-xaml.sh" "$repo/scripts/check-shell-xaml.sh"
  chmod +x "$repo/scripts/check-shell-xaml.sh"
  write_main "$repo"
  write_icons "$repo"
  write_controls "$repo"
}

run_gate() { RC=0; "$1/scripts/check-shell-xaml.sh" >"$2" 2>&1 || RC=$?; }

expect_green() { # expect_green <label> <repo> <log>
  run_gate "$2" "$3"
  if [[ "$RC" -ne 0 ]]; then echo "FAIL: $1: expected GREEN, gate exited $RC"; cat "$3"; FAIL=1; fi
}

expect_red() { # expect_red <label> <repo> <log> <needle>
  run_gate "$2" "$3"
  if [[ "$RC" -eq 0 ]]; then echo "FAIL: $1: expected RED, gate accepted the planted defect"; cat "$3"; FAIL=1; return; fi
  grep -qF -- "$4" "$3" || { echo "FAIL: $1: gate went red but did not name '$4'"; cat "$3"; FAIL=1; }
}

R="$WORK/repo"
make_repo "$R"

# 1. Clean is GREEN, and the multi-line PathIcon (Data on the next line) resolves.
expect_green "clean shell" "$R" "$WORK/clean.log"
grep -qF "main-shell XAML audit OK" "$WORK/clean.log" || { echo "FAIL: clean shell did not report OK"; cat "$WORK/clean.log"; FAIL=1; }

# 2. A PathIcon StaticResource with no definition (would fail at window open).
write_main "$R" '<PathIcon Classes="menu-icon" Data="{StaticResource IconDoesNotExist}" />'
expect_red "an unresolved PathIcon key" "$R" "$WORK/unresolved.log" "IconDoesNotExist"
write_main "$R"
expect_green "after removing the unresolved key" "$R" "$WORK/g2.log"

# 3. A referenced icon whose definition was deleted from Icons.axaml.
write_icons "$R" IconRedact
expect_red "IconRedact deleted from Icons.axaml" "$R" "$WORK/deleted.log" "IconRedact"
write_icons "$R"
expect_green "after restoring Icons.axaml" "$R" "$WORK/g3.log"

# 4. An emoji Content on a toolbar button (the pre-vector-icon regression).
write_main "$R" '<Button Content="📁 Open" />'
expect_red "an emoji toolbar Content" "$R" "$WORK/emoji.log" "an emoji glyph replaced a vector icon"
write_main "$R"
expect_green "after removing the emoji" "$R" "$WORK/g4.log"

# 5. A toolbar command that lost its CommandId.
sed 's/CommandId="redaction.apply"/CommandId="redaction.renamed"/' "$R/Excise.App/Views/MainWindow.axaml" > "$WORK/main.tmp"
cp "$WORK/main.tmp" "$R/Excise.App/Views/MainWindow.axaml"
expect_red "a renamed CommandId" "$R" "$WORK/cmd.log" 'CommandId="redaction.apply"'
write_main "$R"
expect_green "after restoring the CommandId" "$R" "$WORK/g5.log"

# 6. A lost primary tooltip.
sed 's/Open PDF (Ctrl+O)/Open/' "$R/Excise.App/Views/MainWindow.axaml" > "$WORK/main.tmp"
cp "$WORK/main.tmp" "$R/Excise.App/Views/MainWindow.axaml"
expect_red "a lost primary tooltip" "$R" "$WORK/tip.log" "Open PDF (Ctrl+O)"
write_main "$R"
expect_green "after restoring the tooltip" "$R" "$WORK/g6.log"

# 7. A lost PathIcon style.
printf '<Styles><Style Selector="PathIcon.toolbar-icon" /></Styles>\n' > "$R/Excise.App/Styles/Controls.axaml"
expect_red "a lost menu-icon style" "$R" "$WORK/style.log" "PathIcon.menu-icon"
write_controls "$R"
expect_green "after restoring Controls.axaml" "$R" "$WORK/g7.log"

# 8. A vacuous scan: a shell with no PathIcon resources at all, or a missing file.
printf '<Window />\n' > "$R/Excise.App/Views/MainWindow.axaml"
expect_red "a shell that references no PathIcon resources" "$R" "$WORK/vacuous.log" "references no PathIcon resources"
write_main "$R"
rm "$R/Excise.App/Styles/Icons.axaml"
expect_red "a missing Icons.axaml" "$R" "$WORK/missing.log" "cannot read Excise.App/Styles/Icons.axaml"
write_icons "$R"
expect_green "after restoring Icons.axaml again" "$R" "$WORK/g8.log"

if [[ "$FAIL" -ne 0 ]]; then
  echo "test-check-shell-xaml.sh: FAILED"
  exit 1
fi
echo "test-check-shell-xaml.sh: OK (7 planted defects RED then GREEN; vacuous and missing inputs refused)"

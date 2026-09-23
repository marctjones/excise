#!/usr/bin/env bash
#
# Regression test for the #1800 XAML resource-key audit.
#
# Standalone-reproduction convention (scripts/test-check-shell-xaml.sh): copy
# the real script into a synthetic repo holding a minimal Excise.App, PLANT each
# defect the audit exists to catch, watch it go RED, restore, watch it go GREEN.
# A gate that has never been seen red is not accepted (#1012).
#
# Usage: scripts/test-check-xaml-resource-keys.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAIL=0
RC=0

write_brushes() { # write_brushes <repo> [omit-key]
  local repo="$1" omit="${2:-}"
  {
    echo '<ResourceDictionary>'
    for k in BrandPrimaryBrush HoverBrush IconSave; do
      [ "$k" = "$omit" ] && continue
      echo "  <SolidColorBrush x:Key=\"$k\" Color=\"#0078D4\"/>"
    done
    echo '  <ControlTheme x:Key="{x:Type Button}" TargetType="Button"/>'
    echo '</ResourceDictionary>'
  } > "$repo/Excise.App/Styles/Brushes.axaml"
}

write_view() { # write_view <repo> [extra-line]
  local repo="$1" extra="${2:-}"
  {
    echo '<Window>'
    echo '  <!-- {DynamicResource CommentedOutKey} is inside a comment and must be ignored -->'
    echo '  <Button Background="{DynamicResource HoverBrush}"'
    echo '          BorderBrush="{DynamicResource ResourceKey=BrandPrimaryBrush}"/>'
    echo '  <TextBlock Foreground="{Binding Source={StaticResource BrandPrimaryBrush}}"/>'
    echo '  <PathIcon Data="{StaticResource IconSave}"/>'
    [ -n "$extra" ] && echo "  $extra"
    echo '</Window>'
  } > "$repo/Excise.App/Views/V.axaml"
}

make_repo() { # make_repo <repo>
  local repo="$1"
  mkdir -p "$repo/scripts" "$repo/Excise.App/Views" "$repo/Excise.App/Styles"
  cp "$HERE/check-xaml-resource-keys.sh" "$repo/scripts/check-xaml-resource-keys.sh"
  chmod +x "$repo/scripts/check-xaml-resource-keys.sh"
  write_brushes "$repo"
  write_view "$repo"
}

run_gate() { RC=0; "$1/scripts/check-xaml-resource-keys.sh" >"$2" 2>&1 || RC=$?; }

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

# 1. Clean is GREEN: ResourceKey= syntax, a nested StaticResource, a commented-out
#    key and an {x:Type} key all handled.
expect_green "clean app" "$R" "$WORK/clean.log"
grep -qF "XAML resource-key audit OK (4 references to 3 keys" "$WORK/clean.log" \
  || { echo "FAIL: clean app did not report the expected counts"; cat "$WORK/clean.log"; FAIL=1; }

# 2. The #1800 defect: a DynamicResource naming a key nothing defines, reported with file:line.
write_view "$R" '<Button BorderBrush="{DynamicResource AccentBrush}"/>'
expect_red "an undefined DynamicResource" "$R" "$WORK/dyn.log" \
  "Excise.App/Views/V.axaml:7: {DynamicResource AccentBrush}"
write_view "$R"
expect_green "after removing it" "$R" "$WORK/g2.log"

# 3. An undefined StaticResource, spelled with ResourceKey=.
write_view "$R" '<TextBlock FontFamily="{StaticResource ResourceKey=MonospaceFontFamily}"/>'
expect_red "an undefined StaticResource" "$R" "$WORK/static.log" "{StaticResource MonospaceFontFamily}"
write_view "$R"
expect_green "after removing it" "$R" "$WORK/g3.log"

# 4. Keys are case-sensitive, as Avalonia's lookup is.
write_view "$R" '<Button Background="{DynamicResource hoverBrush}"/>'
expect_red "a key differing only in case" "$R" "$WORK/case.log" "{DynamicResource hoverBrush}"
write_view "$R"
expect_green "after removing it" "$R" "$WORK/g4.log"

# 5. A referenced key whose definition was deleted.
write_brushes "$R" HoverBrush
expect_red "HoverBrush deleted from Brushes.axaml" "$R" "$WORK/deleted.log" "{DynamicResource HoverBrush}"
write_brushes "$R"
expect_green "after restoring Brushes.axaml" "$R" "$WORK/g5.log"

# 6. A definition that exists only in build output does not count.
rm "$R/Excise.App/Styles/Brushes.axaml"
mkdir -p "$R/Excise.App/obj"
write_brushes "$R"
mv "$R/Excise.App/Styles/Brushes.axaml" "$R/Excise.App/obj/Brushes.axaml"
expect_red "definitions only under obj/" "$R" "$WORK/obj.log" "{DynamicResource HoverBrush}"
rm -rf "$R/Excise.App/obj"
write_brushes "$R"
expect_green "after restoring Brushes.axaml again" "$R" "$WORK/g6.log"

# 7. Vacuous scans are refused: no references, no .axaml files, no Excise.App.
printf '<Window />\n' > "$R/Excise.App/Views/V.axaml"
expect_red "an app that references no resources" "$R" "$WORK/norefs.log" "the scan is vacuous"
rm "$R/Excise.App/Views/V.axaml" "$R/Excise.App/Styles/Brushes.axaml"
expect_red "an app with no .axaml files" "$R" "$WORK/nofiles.log" "no .axaml files under Excise.App/"
rm -rf "$R/Excise.App"
expect_red "a missing Excise.App" "$R" "$WORK/noapp.log" "Excise.App/ not found"
mkdir -p "$R/Excise.App/Views" "$R/Excise.App/Styles"
write_brushes "$R"
write_view "$R"
expect_green "after restoring the app" "$R" "$WORK/g7.log"

if [[ "$FAIL" -ne 0 ]]; then
  echo "test-check-xaml-resource-keys.sh: FAILED"
  exit 1
fi
echo "test-check-xaml-resource-keys.sh: OK (5 planted defects RED then GREEN; vacuous and missing inputs refused)"

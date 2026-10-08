#!/usr/bin/env bash
#
# Regression test for the chrome colour-literal gate (#1827).
#
# Standalone-reproduction convention (scripts/test-check-xaml-resource-keys.sh):
# copy the real script into a synthetic repo, PLANT each defect the gate exists
# to catch, watch it go RED, restore, watch it go GREEN. A gate that has never
# been seen red is not accepted (#1012).
#
# Usage: scripts/test-check-chrome-color-literals.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAIL=0
R="$WORK/repo"
mkdir -p "$R/scripts" "$R/tests" "$R/Excise.App/Views" "$R/Excise.App/Styles"
cp "$HERE/check-chrome-color-literals.sh" "$R/scripts/"

write_view() { # write_view [extra-line]
  {
    echo '<Window>'
    echo '  <!-- <Border Background="#123456"/> is commented out and must be ignored -->'
    echo '  <Border Background="{DynamicResource CardBrush}"/>'
    echo '  <Border Background="#D0021B"/>'
    if [ -n "${1:-}" ]; then echo "  $1"; fi
    echo '</Window>'
  } > "$R/Excise.App/Views/V.axaml"
}

write_allow() { # write_allow [extra-row]
  {
    printf '# file\tliteral\tcount\treason\n'
    printf 'Excise.App/Views/V.axaml\t#D0021B\t1\tink swatch\n'
    if [ -n "${1:-}" ]; then printf "%b\n" "$1"; fi
  } > "$R/tests/chrome-color-allowlist.tsv"
}

expect() { # expect <green|red> <label> [expected-output]
  local want="$1" label="$2" needle="${3:-}" rc=0 out
  out="$(bash "$R/scripts/check-chrome-color-literals.sh" "$R" 2>&1)" || rc=$?
  if [ "$want" = green ] && [ $rc -ne 0 ]; then
    echo "FAIL [$label]: expected GREEN, got rc=$rc"; echo "$out"; FAIL=1
  elif [ "$want" = red ] && [ $rc -eq 0 ]; then
    echo "FAIL [$label]: expected RED, gate passed"; echo "$out"; FAIL=1
  elif [ -n "$needle" ] && ! grep -qF -- "$needle" <<<"$out"; then
    echo "FAIL [$label]: output lacks '$needle'"; echo "$out"; FAIL=1
  else
    echo "ok   [$label]"
  fi
}

write_view; write_allow
expect green "allowlisted literal; commented literal ignored"

write_view '<TextBlock Foreground="#336699"/>'
expect red "new hex literal in an attribute" "#336699"

write_view '<Style Selector="Border"><Setter Property="Background" Value="White"/></Style>'
expect red "named colour in a colour Setter" "White"

write_view '<Border Background="#d0021b"/>'
expect red "one more swatch than allowed (case-insensitive)" "x2 (allowed 1)"

write_view; write_allow 'Excise.App/Views/V.axaml\t#ABCDEF\t1\tgone'
expect red "stale allowlist row" "stale allowlist rows"

write_view; write_allow 'Excise.App/Views/V.axaml\t#ABCDEF\t1\t'
expect red "allowlist row without a reason" "needs file, literal, count, reason"

write_view; write_allow
expect green "restored"

rm "$R/tests/chrome-color-allowlist.tsv"
expect red "missing allowlist" "missing"
write_allow

rm "$R/Excise.App/Views/V.axaml"
expect red "no views (wrong root) refused" "no .axaml files"

[ $FAIL -eq 0 ] && echo "PASS: chrome colour-literal gate goes red on every planted defect" || exit 1

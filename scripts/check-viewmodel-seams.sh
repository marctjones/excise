#!/usr/bin/env bash
#
# View-model / host seam guard (#1477, #1500, #1773).
#
# Four architecture rules that had no behavioural test able to fail when they
# were broken, so each was a source scan inside Excise.App.Tests - the 8 GB,
# ~19-minute host - and therefore only ran at t1. They read source text; they
# need no build, no Avalonia and no document (#1773).
#
#   1. Picker routing (#1477). Every file Open/Save picker call goes through
#      Excise.App/Services/StoragePickers.cs, which runs the macOS
#      accessory-view cleanup after a chosen file, a cancel and an exception.
#      A picker that skips it leaves Avalonia's file-type accessory looping in
#      AppKit layout: 4.6-5.3% idle CPU for the rest of the session, with no
#      failing behavioural test.
#
#   2. No Application.Current in a view model (#1500 step 1). Host access goes
#      through IWindowHost / ITextClipboard / IFilePicker. Exactly ONE read is
#      documented and allowed: ShowErrorDialogAsync in MainWindowViewModel.cs,
#      which the design deletes in its step 13. The count is asserted as
#      exactly one, and that doubles as the scan's positive control - a
#      pattern that stopped matching would otherwise read as a clean repo.
#
#   3. No direct settings persistence in a view model (#1500 step 2).
#      window.json / zoom.txt / recent.txt are reached only through
#      ISettingsStore and IRecentFilesStore, so a test can supply an in-memory
#      store and the view-mode leak class loses its mechanism.
#
#   4. The viewer owns display rendering. PdfViewerControl owns interactive
#      scheduling and bitmap retention; the view model must not hold a display
#      renderer (SkiaRenderer / IPageImageRenderer) or revive the removed
#      parallel CurrentPageImage path. This replaces a reflection test over
#      MainWindowViewModel's fields and constructor parameters. A type name
#      cannot appear in a field, a constructor parameter or a property without
#      being written down, so a token scan over the view-model sources sees
#      everything the reflection did, and is stricter: it also refuses a
#      local or a comment-free mention.
#
# Comment lines (//, ///, and block-comment continuations starting with *) are
# skipped: they talk ABOUT these names on purpose.
#
# Usage: scripts/check-viewmodel-seams.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VIEWMODELS="Excise.App/ViewModels"
HELPER="Excise.App/Services/StoragePickers.cs"
FAIL=0

if [ ! -d "$VIEWMODELS" ]; then
  echo "FAIL: $VIEWMODELS does not exist; an empty scan is a vacuous pass"
  exit 1
fi

# scan_code <regex> <file...>: matching non-comment lines as "path:line:text".
scan_code() {
  local pattern="$1"; shift
  grep -nE "$pattern" "$@" 2>/dev/null \
    | grep -vE '^[^:]+:[0-9]+:[[:space:]]*(//|\*)' || true
}

list_sources() { # list_sources <dir...>
  find "$@" -type f -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | LC_ALL=C sort
}

VM_SOURCES=()
while IFS= read -r f; do VM_SOURCES+=("$f"); done < <(list_sources "$VIEWMODELS")
if [ ${#VM_SOURCES[@]} -eq 0 ]; then
  echo "FAIL: found no C# sources under $VIEWMODELS to scan"
  exit 1
fi

echo "==> view-model seam guard over ${#VM_SOURCES[@]} view-model sources"

# ---------------------------------------------------------------------------
# 1. Picker routing.
# ---------------------------------------------------------------------------
PICKER_RE='\.(OpenFilePickerAsync|SaveFilePickerAsync)[[:space:]]*\('

if [ ! -f "$HELPER" ]; then
  echo "FAIL: the picker helper is missing: $HELPER"
  FAIL=1
elif ! grep -qE "$PICKER_RE" "$HELPER"; then
  echo "FAIL: the picker scan pattern does not match the helper's own calls in $HELPER."
  echo "      A pattern that cannot match the code it guards is a vacuous pass."
  FAIL=1
else
  APP_SOURCES=()
  while IFS= read -r f; do
    [ "$f" = "$HELPER" ] && continue
    APP_SOURCES+=("$f")
  done < <(list_sources Excise.App Excise.Avalonia)
  PICKER_OFFENDERS=$(grep -nE "$PICKER_RE" "${APP_SOURCES[@]}" 2>/dev/null || true)
  if [ -n "$PICKER_OFFENDERS" ]; then
    echo
    echo "FAIL: a file picker is called directly instead of through StoragePickers (#1477)."
    echo "      Route it through StoragePickers.OpenFilesAsync / SaveFileAsync so the macOS"
    echo "      accessory cleanup runs. Offending lines:"
    echo "$PICKER_OFFENDERS" | sed 's/^/  /'
    FAIL=1
  fi
fi

# ---------------------------------------------------------------------------
# 2. Application.Current: exactly the one documented read.
# ---------------------------------------------------------------------------
APPCURRENT=$(scan_code 'Application[[:space:]]*\.[[:space:]]*Current' "${VM_SOURCES[@]}")
APPCURRENT_COUNT=0
[ -n "$APPCURRENT" ] && APPCURRENT_COUNT=$(printf '%s\n' "$APPCURRENT" | wc -l | tr -d ' ')

if [ "$APPCURRENT_COUNT" -ne 1 ]; then
  echo
  echo "FAIL: expected exactly ONE Application.Current read in the view models, found $APPCURRENT_COUNT (#1500)."
  echo "      Only ShowErrorDialogAsync in MainWindowViewModel.cs may still read the lifetime;"
  echo "      it owns a raw Avalonia Window against the lifetime's main window and is deleted by"
  echo "      the code-behind step. Route new host access through IWindowHost / ITextClipboard."
  echo "      (Zero means that method is gone: tighten this rule to forbid the read outright.)"
  [ -n "$APPCURRENT" ] && printf '%s\n' "$APPCURRENT" | sed 's/^/  /'
  FAIL=1
elif ! printf '%s\n' "$APPCURRENT" | grep -q '^Excise.App/ViewModels/MainWindowViewModel\.cs:'; then
  echo
  echo "FAIL: the one allowed Application.Current read must be in MainWindowViewModel.cs (#1500):"
  printf '%s\n' "$APPCURRENT" | sed 's/^/  /'
  FAIL=1
fi

# ---------------------------------------------------------------------------
# 3. Persisted settings only through the injected stores.
# ---------------------------------------------------------------------------
SETTINGS=$(scan_code 'WindowSettings[[:space:]]*\.[[:space:]]*(Load|Update)[[:space:]]*\(|AppPaths[[:space:]]*\.' "${VM_SOURCES[@]}")
if [ -n "$SETTINGS" ]; then
  echo
  echo "FAIL: a view model reads or writes persisted settings directly (#1500 step 2)."
  echo "      window.json / zoom.txt / recent.txt go through ISettingsStore and IRecentFilesStore."
  echo "$SETTINGS" | sed 's/^/  /'
  FAIL=1
fi

# ---------------------------------------------------------------------------
# 4. The viewer owns display rendering.
# ---------------------------------------------------------------------------
# Explicit identifier boundaries rather than \b: \b is not POSIX ERE, and BSD
# grep (macOS) and GNU grep disagree about it.
DISPLAY_RENDER=$(scan_code '(^|[^A-Za-z0-9_])(SkiaRenderer|IPageImageRenderer|CurrentPageImage)([^A-Za-z0-9_]|$)' "${VM_SOURCES[@]}")
if [ -n "$DISPLAY_RENDER" ]; then
  echo
  echo "FAIL: a view model references a display renderer or the removed CurrentPageImage path."
  echo "      PdfViewerControl owns display bitmaps; the view model must not revive the removed"
  echo "      parallel display path. Offending lines:"
  echo "$DISPLAY_RENDER" | sed 's/^/  /'
  FAIL=1
fi

if [ "$FAIL" -ne 0 ]; then
  exit 1
fi

echo "==> view-model seam guard OK (pickers routed, 1 documented Application.Current, no direct settings, no display renderer)"

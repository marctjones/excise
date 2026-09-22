#!/usr/bin/env bash
#
# Regression test for the #1477/#1500/#1773 view-model seam guard.
#
# Standalone-reproduction convention (scripts/test-check-fixture-locators.sh):
# copy the real script into a synthetic repo, PLANT each violation the gate
# exists to catch (a direct OpenFilePickerAsync outside the picker helper; an
# Application.Current use in a view model; direct settings persistence; a
# display renderer in a view model), watch it go RED, remove it, watch it go
# GREEN. A gate that has never been seen red is not accepted (#1012).
#
# Usage: scripts/test-check-viewmodel-seams.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAIL=0
RC=0
VMDIR="Excise.App/ViewModels"

make_repo() { # make_repo <repo>
  local repo="$1"
  mkdir -p "$repo/scripts" "$repo/$VMDIR" "$repo/Excise.App/Services" \
           "$repo/Excise.App/Views" "$repo/Excise.Avalonia/Controls"
  cp "$HERE/check-viewmodel-seams.sh" "$repo/scripts/check-viewmodel-seams.sh"
  chmod +x "$repo/scripts/check-viewmodel-seams.sh"

  # The helper: the ONLY place a file picker may be called. Its own calls are
  # the scan's positive control.
  cat > "$repo/Excise.App/Services/StoragePickers.cs" <<'CS'
namespace Excise.App.Services;
public static class StoragePickers
{
    public static async Task<object> OpenFilesAsync(IStorageProvider p, object o)
    {
        try { return await p.OpenFilePickerAsync(o); } finally { }
    }
    public static async Task<object> SaveFileAsync(IStorageProvider p, object o)
    {
        try { return await p.SaveFilePickerAsync(o); } finally { }
    }
}
CS

  # The one documented Application.Current read, plus comments that talk ABOUT
  # the forbidden names (which must not count).
  cat > "$repo/$VMDIR/MainWindowViewModel.cs" <<'CS'
namespace Excise.App.ViewModels;
public partial class MainWindowViewModel
{
    // Previously read Application.Current.ApplicationLifetime and WindowSettings.Load(...).
    /// <summary>Not an AppPaths.Foo read, and no SkiaRenderer or CurrentPageImage here.</summary>
    private async Task ShowErrorDialogAsync()
    {
        var lifetime = global::Avalonia.Application.Current?.ApplicationLifetime;
    }
}
CS
  cat > "$repo/$VMDIR/MainWindowViewModel.Commands.cs" <<'CS'
namespace Excise.App.ViewModels;
public partial class MainWindowViewModel
{
    private readonly IFilePicker _picker = null!;
}
CS
  cat > "$repo/Excise.App/Views/MainWindow.axaml.cs" <<'CS'
namespace Excise.App.Views;
public class MainWindow { }
CS
  cat > "$repo/Excise.Avalonia/Controls/PdfViewerControl.cs" <<'CS'
namespace Excise.Avalonia.Controls;
public class PdfViewerControl { private SkiaRendererCache _cache = null!; }
CS
}

run_gate() { # run_gate <repo> <log>  -> sets RC
  RC=0
  "$1/scripts/check-viewmodel-seams.sh" >"$2" 2>&1 || RC=$?
}

expect_green() { # expect_green <label> <repo> <log>
  run_gate "$2" "$3"
  if [[ "$RC" -ne 0 ]]; then echo "FAIL: $1: expected GREEN, gate exited $RC"; cat "$3"; FAIL=1; fi
}

expect_red() { # expect_red <label> <repo> <log> <needle-in-output>
  run_gate "$2" "$3"
  if [[ "$RC" -eq 0 ]]; then echo "FAIL: $1: expected RED, gate accepted the planted violation"; cat "$3"; FAIL=1; return; fi
  grep -qF -- "$4" "$3" || { echo "FAIL: $1: gate went red but did not name '$4'"; cat "$3"; FAIL=1; }
}

# plant_red_then_green <label> <repo> <file> <content> <needle>
# Writes <file> (relative to the repo), expects RED naming <needle>, removes it,
# expects GREEN again.
plant_red_then_green() {
  local label="$1" repo="$2" file="$3" content="$4" needle="$5"
  mkdir -p "$(dirname "$repo/$file")"
  printf '%s\n' "$content" > "$repo/$file"
  expect_red "$label" "$repo" "$WORK/red.log" "$needle"
  rm "$repo/$file"
  expect_green "after removing: $label" "$repo" "$WORK/green.log"
}

R="$WORK/repo"
make_repo "$R"

# ---------------------------------------------------------------------------
# 1. The clean repo is GREEN. Its comments mention every forbidden name and
#    Avalonia holds a SkiaRendererCache identifier; none of that may trip it.
# ---------------------------------------------------------------------------
expect_green "clean repo" "$R" "$WORK/clean.log"
grep -qF "view-model seam guard OK" "$WORK/clean.log" || { echo "FAIL: clean repo did not report OK"; cat "$WORK/clean.log"; FAIL=1; }

# ---------------------------------------------------------------------------
# 2. Picker routing (#1477): a picker called outside the helper.
# ---------------------------------------------------------------------------
plant_red_then_green "direct OpenFilePickerAsync in a view" "$R" "Excise.App/Views/Direct.cs" \
  'namespace Excise.App.Views; class Direct { async Task M(IStorageProvider p) { await p.OpenFilePickerAsync(null); } }' \
  "Excise.App/Views/Direct.cs:1"
plant_red_then_green "direct SaveFilePickerAsync in Excise.Avalonia" "$R" "Excise.Avalonia/Controls/Direct.cs" \
  'namespace Excise.Avalonia.Controls; class Direct { async Task M(IStorageProvider p) { await p.SaveFilePickerAsync(null); } }' \
  "Excise.Avalonia/Controls/Direct.cs:1"
plant_red_then_green "direct picker in a view model" "$R" "$VMDIR/MainWindowViewModel.Files.cs" \
  'namespace Excise.App.ViewModels; partial class MainWindowViewModel { async Task M(IStorageProvider p) { await p.OpenFilePickerAsync(null); } }' \
  "$VMDIR/MainWindowViewModel.Files.cs:1"

# ---------------------------------------------------------------------------
# 3. Application.Current: exactly one, in MainWindowViewModel.cs.
# ---------------------------------------------------------------------------
plant_red_then_green "a second Application.Current in a view model" "$R" "$VMDIR/PreferencesViewModel.cs" \
  'namespace Excise.App.ViewModels; class PreferencesViewModel { object L => Application.Current!.ApplicationLifetime!; }' \
  "found 2"

# A comment mentioning it is fine (already in the clean repo, and again here).
printf '// Application.Current.ApplicationLifetime is talked about, not read\n' > "$R/$VMDIR/Note.cs"
expect_green "Application.Current in a comment only" "$R" "$WORK/comment.log"
rm "$R/$VMDIR/Note.cs"

# Zero reads (the documented method was deleted) is also RED: the positive
# control. A pattern that stopped matching would otherwise read as a clean repo.
cp "$R/$VMDIR/MainWindowViewModel.cs" "$WORK/MainWindowViewModel.cs.orig"
printf 'namespace Excise.App.ViewModels;\npublic partial class MainWindowViewModel { }\n' > "$R/$VMDIR/MainWindowViewModel.cs"
expect_red "zero Application.Current reads" "$R" "$WORK/zero.log" "found 0"
cp "$WORK/MainWindowViewModel.cs.orig" "$R/$VMDIR/MainWindowViewModel.cs"
expect_green "after restoring the documented read" "$R" "$WORK/restored.log"

# The one allowed read is not portable to another file.
mv "$R/$VMDIR/MainWindowViewModel.cs" "$R/$VMDIR/Other.cs"
printf 'namespace Excise.App.ViewModels;\npublic partial class MainWindowViewModel { }\n' > "$R/$VMDIR/MainWindowViewModel.cs"
expect_red "the one read moved to another file" "$R" "$WORK/moved.log" "must be in MainWindowViewModel.cs"
rm "$R/$VMDIR/Other.cs"
cp "$WORK/MainWindowViewModel.cs.orig" "$R/$VMDIR/MainWindowViewModel.cs"
expect_green "after restoring MainWindowViewModel.cs" "$R" "$WORK/restored2.log"

# ---------------------------------------------------------------------------
# 4. Direct settings persistence in a view model (#1500 step 2).
# ---------------------------------------------------------------------------
plant_red_then_green "WindowSettings.Load in a view model" "$R" "$VMDIR/Zoom.cs" \
  'namespace Excise.App.ViewModels; class Zoom { void M() { var s = WindowSettings.Load(); } }' \
  "$VMDIR/Zoom.cs:1"
plant_red_then_green "WindowSettings.Update in a view model" "$R" "$VMDIR/Zoom.cs" \
  'namespace Excise.App.ViewModels; class Zoom { void M() { WindowSettings . Update (s => s); } }' \
  "$VMDIR/Zoom.cs:1"
plant_red_then_green "AppPaths in a view model" "$R" "$VMDIR/Recent.cs" \
  'namespace Excise.App.ViewModels; class Recent { string P => AppPaths.RecentFiles; }' \
  "$VMDIR/Recent.cs:1"

# ---------------------------------------------------------------------------
# 5. The viewer owns display rendering: no renderer / CurrentPageImage in a VM.
# ---------------------------------------------------------------------------
plant_red_then_green "a SkiaRenderer field in a view model" "$R" "$VMDIR/Render.cs" \
  'namespace Excise.App.ViewModels; class Render { private readonly SkiaRenderer _r = null!; }' \
  "$VMDIR/Render.cs:1"
plant_red_then_green "an IPageImageRenderer constructor parameter" "$R" "$VMDIR/Render.cs" \
  'namespace Excise.App.ViewModels; class Render { Render(IPageImageRenderer r) { } }' \
  "$VMDIR/Render.cs:1"
plant_red_then_green "a revived CurrentPageImage property" "$R" "$VMDIR/Render.cs" \
  'namespace Excise.App.ViewModels; class Render { public object? CurrentPageImage { get; set; } }' \
  "$VMDIR/Render.cs:1"

# ---------------------------------------------------------------------------
# 6. A vacuous scan is a failure: no helper, a helper that no longer calls a
#    picker (so the pattern proves nothing), an empty view-model folder.
# ---------------------------------------------------------------------------
H="$WORK/nohelper"
make_repo "$H"
rm "$H/Excise.App/Services/StoragePickers.cs"
expect_red "a missing helper" "$H" "$WORK/nohelper.log" "the picker helper is missing"

D="$WORK/deadpattern"
make_repo "$D"
printf 'namespace Excise.App.Services;\npublic static class StoragePickers { }\n' > "$D/Excise.App/Services/StoragePickers.cs"
expect_red "a helper the scan pattern cannot match" "$D" "$WORK/deadpattern.log" "cannot match the code it guards"

E="$WORK/emptyvm"
make_repo "$E"
rm "$E/$VMDIR"/*.cs
expect_red "an empty view-model folder" "$E" "$WORK/emptyvm.log" "found no C# sources"

if [[ "$FAIL" -ne 0 ]]; then
  echo "test-check-viewmodel-seams.sh: FAILED"
  exit 1
fi
echo "test-check-viewmodel-seams.sh: OK (picker routing, Application.Current, settings, display renderer: each planted RED then GREEN; vacuous scans refused)"

#!/usr/bin/env bash
#
# Selftest for scripts/verify-doc-claims.sh (#1012).
#
# That gate pins "the docs and the code still say the same thing". It is a pile
# of `grep -Fq` calls, and the #941 audit found one of them guarding nothing:
# it grepped for the bare substring "trust", which a signature-verification
# formatter contains a dozen times over, so DELETING the limitation disclosure
# — falsifying the README claim exactly — still printed "doc-claim check
# passed". A gate made of greps needs a test that the greps discriminate.
#
# Hermetic: verify-doc-claims.sh derives its ROOT from its own location, so a
# copy of it in a temp tree reads that tree as the repo. The files it pins are
# copied in from the real checkout (so the pins stay honest about real content)
# and mutated THERE — the working copy is never touched.
#
# Sub-second. Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/verify-doc-claims.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

# ── build a pristine mirror of exactly the files the gate reads ──────────────
# Derived FROM the gate, not hard-coded: a new pin gets its file copied
# automatically instead of making this selftest fail for the wrong reason.
P="$TMP/pristine"
mkdir -p "$P/scripts"
cp "$SCRIPT" "$P/scripts/verify-doc-claims.sh"

pinned_files() {
    {
        grep -E '^[[:space:]]*require_(file_text|code_text)[[:space:]]' "$SCRIPT" | awk '{print $2}'
        # require_wired_command's two implicit files
        echo Excise.App/ViewModels/MainWindowViewModel.Commands.cs
        echo Excise.App/Views/MainWindow.axaml
    } | sort -u
}

count=0
while IFS= read -r f; do
    [[ -z "$f" ]] && continue
    [[ -f "$ROOT/$f" ]] || fail "verify-doc-claims.sh pins $f, which does not exist in the checkout"
    mkdir -p "$P/$(dirname "$f")"
    cp "$ROOT/$f" "$P/$f"
    count=$((count + 1))
done < <(pinned_files)
[[ "$count" -gt 5 ]] || fail "only $count pinned files found — the extraction above stopped working"

R="$TMP/root"
reset_tree() { rm -rf "$R"; cp -R "$P" "$R"; }
run() {
    set +e
    OUT="$("$R/scripts/verify-doc-claims.sh" 2>&1)"
    RC=$?
    set -e
}
# strip_line <file> <literal>  — falsify one pinned claim
strip_line() {
    grep -vF "$2" "$R/$1" > "$R/$1.tmp" || true
    mv "$R/$1.tmp" "$R/$1"
    grep -Fq "$2" "$R/$1" && fail "mutation did not reach: '$2' is still in $1"
    return 0
}

echo "==> selftest: verify-doc-claims.sh ($count pinned files)"

# ── 1. The unmutated mirror passes ───────────────────────────────────────────
# Without this the failures below would prove nothing: a tree the gate cannot
# read at all "fails" for every mutation, including no mutation.
reset_tree
run
[[ "$RC" -eq 0 ]] || fail "the unmutated mirror must pass, or the failures below mean nothing (exit $RC)
$OUT"
echo "    unmutated mirror                     exit 0"

# ── 2. Delete the README claim -> FAIL ───────────────────────────────────────
reset_tree
strip_line README.md "OS trust-chain validation limitations"
run
[[ "$RC" -ne 0 ]] || fail "deleting the pinned README claim must fail the gate
$OUT"
echo "    README claim deleted                 exit $RC"

# ── 3. Delete the CODE that delivers it -> FAIL ──────────────────────────────
# The #941 case verbatim: the README still promises the disclosure, the
# formatter no longer makes it. Before #941 this passed.
reset_tree
strip_line Excise.App/Services/SignatureVerificationSummaryFormatter.cs \
    "certificate revocation (CRL/OCSP) is not checked"
run
[[ "$RC" -ne 0 ]] || fail "deleting the implementation behind a README promise must fail the gate
$OUT"
grep -q "SignatureVerificationSummaryFormatter" <<<"$OUT" \
    || fail "the failure must name the file whose claim was falsified:
$OUT"
echo "    disclosure removed from the code     exit $RC"

# ── 4. A command declared but bound to NOTHING -> FAIL ───────────────────────
# The feature the README promises exists on the ViewModel and is unreachable
# from the UI — the reason require_wired_command pins both ends.
reset_tree
# Rename the binding rather than deleting the line: deleting it would also take
# the menu item's LABEL, which a different pin covers, and the gate would then
# fail for the wrong reason — a mutation that reaches the wrong guard proves
# nothing about this one.
sed -i.bak 's/MoveCurrentPageLaterCommand/MoveCurrentPageLaterCmd/g' "$R/Excise.App/Views/MainWindow.axaml"
rm -f "$R/Excise.App/Views/MainWindow.axaml.bak"
grep -Fq "MoveCurrentPageLaterCommand" "$R/Excise.App/Views/MainWindow.axaml" \
    && fail "mutation did not reach: the binding is still in MainWindow.axaml"
run
[[ "$RC" -ne 0 ]] || fail "a command declared but bound to nothing must fail the gate
$OUT"
grep -q "bound to nothing" <<<"$OUT" || fail "expected the unbound-command verdict:
$OUT"
echo "    command unbound in the XAML          exit $RC"

echo "==> verify-doc-claims.sh selftest OK"

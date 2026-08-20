#!/usr/bin/env bash
#
# Open-source licence compliance (#1068, moved out of xunit).
#
# Every third-party NuGet package Excise.App actually ships must appear in the
# embedded manifest the About dialog reads, with a licence the user can act on
# AND its verbatim text. Permissive licences require the notice to travel with
# the redistribution; a link is not an attribution.
#
# ── Why this is a SCRIPT and not a test ──────────────────────────────────────
#
# It used to be three [Fact]s in Excise.App.Tests. That gave a compliance check
# a blast radius of 1,300 correctness tests — and not hypothetically: it shelled
# out to `dotnet list package`, MSBuild node-reuse workers inherited the pipe,
# the read never returned, and THREE consecutive full-suite runs aborted with
# "host process exited unexpectedly". 1,310 correctness tests had no verdict
# because a licence check was blocked on a pipe.
#
# It never gated them logically. xunit runs an assembly in one process, so any
# test that can hang or crash the host takes every other test's RESULT with it.
# The rule this encodes: no compliance check may be able to stop correctness
# tests from reporting. Static file checks belong in scripts/ at t0, beside
# check-doc-claim-freshness.sh and the rest.
#
# ── No second copies ─────────────────────────────────────────────────────────
#
# The excluded-package set and the set of SPDX ids with a built-in licence body
# both already exist in the tree. This script RE-DERIVES them from their source
# rather than restating them, so it cannot drift the way a hand-mirrored copy
# does (#1064 is the same concern for the generator side).
#
# Usage: scripts/check-license-compliance.sh
#
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO"

MANIFEST="Excise.App/Assets/third-party-licenses.json"
ASSETS="Excise.App/obj/project.assets.json"
GENERATOR="scripts/generate-license-manifest.sh"
SPDX_TEXTS="Excise.App/ViewModels/SpdxLicenseTexts.cs"

for f in "$MANIFEST" "$GENERATOR" "$SPDX_TEXTS"; do
  [[ -f "$f" ]] || { echo "❌ missing $f" >&2; exit 1; }
done
[[ -f "$ASSETS" ]] || {
  echo "❌ no NuGet restore output at $ASSETS — run 'dotnet restore Excise.App'" >&2
  exit 1
}

python3 - "$MANIFEST" "$ASSETS" "$GENERATOR" "$SPDX_TEXTS" <<'PY'
import json, re, sys

manifest_path, assets_path, generator_path, spdx_path = sys.argv[1:5]

# ── The shipped closure, from NuGet's own restore output ─────────────────────
# An independent source: NuGet writes it, so the manifest is not permitted to
# vouch for its own completeness.
assets = json.load(open(assets_path))
closure = set()
for framework in assets["targets"].values():
    for name, entry in framework.items():
        if entry.get("type") == "package":
            closure.add(name.split("/")[0])

# ── Excluded packages, RE-DERIVED from the generator ─────────────────────────
gen = open(generator_path).read()
m = re.search(r"exclude\s*=\s*set\(\[(.*?)\]\)", gen, re.S)
if not m:
    print("❌ could not find the exclude set in the generator — its shape changed, "
          "and this check must not silently fall back to excluding nothing")
    sys.exit(1)
excluded = {s.lower() for s in re.findall(r'"([^"]+)"', m.group(1))}

# ── SPDX ids that carry a built-in body, RE-DERIVED from the C# ──────────────
spdx_src = open(spdx_path).read()
body = spdx_src[spdx_src.index("return spdx!.Trim() switch"):]
body = body[:body.index("};")]
spdx_with_text = set(re.findall(r'"([^"]+)"\s*=>', body))
if not spdx_with_text:
    print("❌ could not re-derive the SPDX text table from SpdxLicenseTexts.cs")
    sys.exit(1)

packages = json.load(open(manifest_path))["packages"]
if not packages:
    print("❌ the manifest is empty")
    sys.exit(1)

def resolved(p):
    """A licence the user can act on: an SPDX id, or a real name — not the
    generator's '(see licenseUrl)' placeholder. A bare URL is not attribution."""
    if (p.get("spdx") or "").strip():
        return True
    name = (p.get("licenseName") or "").strip()
    return bool(name) and name.lower() != "(see licensehttp)".lower() \
        and name.lower() != "(see licenseurl)"

def has_text(p):
    if (p.get("licenseText") or "").strip():
        return True
    return (p.get("spdx") or "").strip() in spdx_with_text

by_id = {}
for p in packages:
    if p.get("id"):
        by_id.setdefault(p["id"].lower(), p)

shipped = sorted(i for i in closure if i.lower() not in excluded)
failures = []

missing = [i for i in shipped if i.lower() not in by_id or not resolved(by_id[i.lower()])]
if missing:
    failures.append(
        "shipped packages with no resolved licence in the manifest:\n     "
        + "\n     ".join(sorted(missing))
        + "\n   Regenerate with scripts/generate-license-manifest.sh (add a "
          "LICENSE_OVERRIDES entry if it cannot be auto-detected).")

unresolved = [f'{p.get("id")} {p.get("version")}' for p in packages if not resolved(p)]
if unresolved:
    failures.append(
        "manifest entries with no resolved licence identity:\n     "
        + "\n     ".join(sorted(unresolved)))

textless = [i for i in shipped if i.lower() in by_id and not has_text(by_id[i.lower()])]
if textless:
    failures.append(
        "shipped packages showing only a link, not verbatim licence text:\n     "
        + "\n     ".join(sorted(textless))
        + "\n   Permissive licences require the notice to travel with the "
          "redistribution. Embed the file (LICENSE_OVERRIDES) or add its SPDX "
          "id to SpdxLicenseTexts.")

print(f"licence compliance: {len(shipped)} shipped packages, "
      f"{len(excluded)} excluded, {len(packages)} manifest entries, "
      f"{len(spdx_with_text)} SPDX bodies available")

if failures:
    for f in failures:
        print(f"❌ {f}", file=sys.stderr)
    sys.exit(1)

print("✅ every shipped package is attributed with a resolved licence and its text.")
PY

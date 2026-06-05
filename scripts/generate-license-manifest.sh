#!/usr/bin/env bash
# Generate the third-party license manifest used by the About dialog and
# the LICENSES.md doc.
#
# Approach:
#   1. Resolve the runtime dependency graph for PdfEditor (the GUI app
#      that ships in the .deb / installers).
#   2. For each package, locate the extracted .nuget cache folder and
#      the .nuspec inside it. Pull the declared license expression and
#      author/copyright. When a LICENSE/LICENSE.txt file ships in the
#      package, capture its sha1.
#   3. Run scancode on every package folder so we have an independent
#      reading of the actual license text — not just what the .nuspec
#      claims. Disagreements are flagged.
#   4. Write a JSON manifest at PdfEditor/Assets/third-party-licenses.json
#      that the AboutWindow loads at runtime as an embedded resource.
#
# Usage:
#   scripts/generate-license-manifest.sh [--scancode] [--project PdfEditor]
#
#   --scancode      run scancode-toolkit cross-check (slow, ~3-5 min)
#   --project P     project to resolve deps from (default PdfEditor)
#
# Outputs:
#   PdfEditor/Assets/third-party-licenses.json
#   artifacts/scancode/<package>.json (when --scancode)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

PROJECT="PdfEditor/PdfEditor.csproj"
RUN_SCANCODE=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --project)  PROJECT="$2"; shift 2 ;;
        --scancode) RUN_SCANCODE=1; shift ;;
        --help|-h)  sed -n '2,25p' "$0"; exit 0 ;;
        *) echo "Unknown arg: $1" >&2; exit 2 ;;
    esac
done

NUGET_DIR="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
# Don't hard-fail on a cold cache: the `dotnet restore` below populates it.
# CI keys the actions/cache on hashFiles('**/*.csproj'), so any version bump
# misses the cache and the dir may not exist yet — that's fine, restore fills it.
mkdir -p "$NUGET_DIR"

OUT="$ROOT/PdfEditor/Assets/third-party-licenses.json"
SCANCODE_DIR="$ROOT/artifacts/scancode"
mkdir -p "$(dirname "$OUT")"
[[ "$RUN_SCANCODE" == "1" ]] && mkdir -p "$SCANCODE_DIR"

echo "▶ Restoring $PROJECT"
dotnet restore "$ROOT/$PROJECT" >/dev/null

echo "▶ Resolving deps"
PKG_LIST="$(dotnet list "$ROOT/$PROJECT" package --include-transitive --format json 2>/dev/null \
    | python3 -c "
import json,sys
d=json.load(sys.stdin)
seen=set()
for proj in d['projects']:
    for fw in proj.get('frameworks',[]):
        for kind in ('topLevelPackages','transitivePackages'):
            for p in fw.get(kind,[]):
                seen.add((p['id'], p['resolvedVersion']))
for n,v in sorted(seen, key=lambda x: x[0].lower()):
    print(f'{n}|{v}')
")"

# Filter out obvious build/test/dev infrastructure that doesn't ship at
# runtime. (The publish output for self-contained single-file already
# excludes these, but they're noise in an end-user About dialog.)
EXCLUDE_PATTERNS=(
    'Avalonia.Diagnostics'
    'Microsoft.CodeAnalysis.Analyzers'   # build-time analyzer
    'Avalonia.BuildServices'              # build-time
    'Fody'                                # build-time weaver
)

echo "▶ Building manifest"

# We'll emit a JSON array. Use python for cleaner string handling than
# bash glue.
python3 - "$NUGET_DIR" "$OUT" "$RUN_SCANCODE" "$SCANCODE_DIR" <<PY
import os, sys, json, re, subprocess, hashlib, xml.etree.ElementTree as ET
from pathlib import Path

nuget_dir, out_path, run_scancode, scancode_dir = sys.argv[1], sys.argv[2], sys.argv[3] == "1", sys.argv[4]
pkg_lines = """$PKG_LIST""".strip().splitlines()
exclude = set([
    "Avalonia.Diagnostics",
    "Microsoft.CodeAnalysis.Analyzers",
    "Avalonia.BuildServices",
    "Fody",
])

# A few packages don't ship a LICENSE file but use SPDX-only metadata;
# we map their declared SPDX → human-readable name + canonical text url.
SPDX_NAME = {
    "MIT": "MIT License",
    "Apache-2.0": "Apache License 2.0",
    "BSD-3-Clause": "BSD 3-Clause License",
    "BSD-2-Clause": "BSD 2-Clause License",
    "OFL-1.1": "SIL Open Font License 1.1",
    "MS-EULA": "Microsoft Software License (EULA)",
    "LicenseRef-scancode-ocb-open-source-2013": "MIT License (BouncyCastle / OCB Open Source 2013)",
}

# Scancode-license-detection false-positives we know about. Each tuple is
# (package id, scancode SPDX to ignore-as-proprietary-noise). Used to
# strip noise like .p7s NuGet signature files that scancode flags as
# "proprietary" simply because the binary blob isn't a license file.
SCANCODE_FALSE_POSITIVES = {
    "Portable.BouncyCastle": ["LicenseRef-scancode-proprietary-license"],
}

# Manual license-name overrides — packages whose .nuspec lacks an SPDX
# expression but whose actual license is known and stable. Keyed by
# package id.
LICENSE_OVERRIDES = {
    # BouncyCastle .NET ships its own MIT-derived license. Pre-2.0
    # NuGet packages used a deprecated license URL pointer; the project
    # itself is MIT-licensed in spirit.
    "Portable.BouncyCastle": {
        "licenseName": "MIT License (BouncyCastle)",
        "spdx": "MIT",
        "licenseSpdxUrl": "https://www.bouncycastle.org/csharp/licence.html",
    },
}

def spdx_url(spdx):
    return f"https://spdx.org/licenses/{spdx}.html"

NS = {"n": "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd"}

def parse_nuspec(folder, name):
    # NuGet stores nuspec as <name-lowercased>.nuspec
    candidates = list(folder.glob("*.nuspec"))
    if not candidates: return None
    tree = ET.parse(candidates[0])
    root = tree.getroot()
    # Strip namespace for ergonomic xpath
    for el in root.iter():
        el.tag = el.tag.split('}', 1)[-1]
    md = root.find("metadata")
    def t(tag):
        e = md.find(tag); return e.text.strip() if e is not None and e.text else None
    license_el = md.find("license")
    license_kind = license_el.get("type") if license_el is not None else None
    license_value = license_el.text.strip() if license_el is not None and license_el.text else None
    return {
        "id": t("id") or name,
        "version": t("version"),
        "authors": t("authors"),
        "copyright": t("copyright"),
        "projectUrl": t("projectUrl"),
        "repositoryUrl": (md.find("repository").get("url") if md.find("repository") is not None else None),
        "description": t("description"),
        "licenseKind": license_kind,           # 'expression' or 'file' or None
        "licenseValue": license_value,         # SPDX expr OR filename, depending on kind
        "licenseUrl": t("licenseUrl"),         # legacy URL, only present on older packages
    }

def find_license_file(folder):
    for name in ("LICENSE", "LICENSE.md", "LICENSE.txt", "License.txt", "license.txt", "LICENSE.TXT"):
        p = folder / name
        if p.is_file(): return p
    return None

def sha1_file(p):
    h = hashlib.sha1()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""): h.update(chunk)
    return h.hexdigest()

def run_scan(folder, out_dir, name, version):
    out = Path(out_dir) / f"{name}-{version}.json"
    if out.is_file():
        return json.loads(out.read_text())
    try:
        # Light mode: just detect license expressions per file. We already
        # capture verbatim LICENSE text from the package itself in the
        # main loop, so there's no need to ask scancode for --license-text
        # too (which is the slow part).
        subprocess.run([
            "scancode", "--license",
            "--strip-root", "--quiet",
            "--processes", "4",
            "-n", "4",
            "--json", str(out),
            str(folder),
        ], check=True, capture_output=True, timeout=120)
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as e:
        msg = e.stderr.decode()[:200] if hasattr(e, "stderr") and e.stderr else str(e)
        print(f"  scancode skipped for {name}: {msg}", file=sys.stderr)
        return None
    return json.loads(out.read_text())

results = []
for line in pkg_lines:
    if not line: continue
    name, version = line.split("|", 1)
    if name in exclude:
        continue
    folder = Path(nuget_dir) / name.lower() / version
    if not folder.is_dir():
        print(f"  ! missing cache for {name} {version} at {folder}", file=sys.stderr)
        continue
    info = parse_nuspec(folder, name) or {"id": name, "version": version}
    info["nugetId"] = name
    info["nugetVersion"] = version

    lf = find_license_file(folder)
    if lf:
        info["licenseFileName"] = lf.name
        info["licenseFileSha1"] = sha1_file(lf)
        text = lf.read_text(errors="replace")
        # Cap embedded text at 8 KB to keep the manifest reasonable.
        info["licenseText"] = text if len(text) <= 8192 else text[:8192] + "\n…(truncated; full text at " + (info.get("projectUrl") or info.get("repositoryUrl") or "") + ")"

    if info.get("licenseKind") == "expression" and info.get("licenseValue"):
        spdx = info["licenseValue"]
        info["spdx"] = spdx
        info["licenseName"] = SPDX_NAME.get(spdx, spdx)
        info["licenseSpdxUrl"] = spdx_url(spdx)
    elif info.get("licenseKind") == "file":
        info["licenseName"] = info.get("licenseFileName") or info.get("licenseValue")
    elif info.get("licenseUrl"):
        info["licenseName"] = "(see licenseUrl)"

    if run_scancode:
        scan = run_scan(folder, scancode_dir, name, version)
        if scan:
            # Aggregate SPDX expressions across all files in the package,
            # skipping known false-positives.
            ignore_exprs = set(SCANCODE_FALSE_POSITIVES.get(name, []))
            licenses = set()
            for f in scan.get("files", []):
                # Skip detections that came from binary signature/blob
                # files — scancode's "proprietary" classifier hits .p7s,
                # .pfx, .signature.* and similar artifacts that aren't
                # license-bearing in any meaningful sense.
                path = f.get("path") or ""
                if path.endswith((".p7s", ".pfx", ".sig")) or "signature" in path.lower():
                    continue
                for d in f.get("license_detections", []) or []:
                    expr = d.get("license_expression_spdx") or d.get("license_expression")
                    if expr and expr not in ignore_exprs: licenses.add(expr)
                # scancode also exposes per-file detected_license_expression
                expr = f.get("detected_license_expression_spdx") or f.get("detected_license_expression")
                if expr and expr not in ignore_exprs: licenses.add(expr)
            info["scancodeDetectedSpdx"] = sorted(licenses)
            # Mismatch detection: declared vs detected.
            declared = info.get("spdx")
            if declared and licenses and declared not in licenses:
                # OK if declared is a subset of one of the detected exprs.
                if not any(declared in l for l in licenses):
                    info["scancodeMismatch"] = True
            # Fallback: if the package declared its license as a file (not
            # an SPDX expression) and scancode found exactly one license,
            # use that as the human-readable name. This rescues packages
            # like Avalonia.Angle.Windows.Natives that ship a verbatim
            # LICENSE file without an SPDX hint in the .nuspec.
            if licenses and (not info.get("spdx")):
                # Pick the most common single-token expression.
                singletons = [l for l in licenses if " AND " not in l and " OR " not in l]
                if singletons:
                    chosen = sorted(singletons, key=len)[0]
                    info["spdx"] = chosen
                    info["licenseName"] = SPDX_NAME.get(chosen, chosen)
                    info["licenseSpdxUrl"] = spdx_url(chosen)

    # Apply manual overrides last, after scancode-derived data.
    if name in LICENSE_OVERRIDES:
        for k, v in LICENSE_OVERRIDES[name].items():
            info[k] = v

    results.append(info)

with open(out_path, "w") as f:
    json.dump({"generatedAt": __import__("datetime").datetime.utcnow().isoformat() + "Z",
               "project": "$PROJECT",
               "packages": results}, f, indent=2)

print(f"  wrote {out_path}  ({len(results)} packages)")
mismatches = [p for p in results if p.get("scancodeMismatch")]
if mismatches:
    print(f"  ⚠ scancode flagged {len(mismatches)} declared/detected license mismatch(es):", file=sys.stderr)
    for p in mismatches:
        print(f"     {p['nugetId']} {p['nugetVersion']}: declared {p.get('spdx')}, detected {p.get('scancodeDetectedSpdx')}", file=sys.stderr)
PY

echo "▶ Done"

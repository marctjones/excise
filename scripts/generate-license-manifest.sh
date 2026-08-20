#!/usr/bin/env bash
# Generate the third-party license manifest used by the About dialog and
# the LICENSES.md doc.
#
# Approach:
#   1. Resolve the runtime dependency graph for Excise.App (the GUI app
#      that ships in the .deb / installers).
#   2. For each package, locate the extracted .nuget cache folder and
#      the .nuspec inside it. Pull the declared license expression and
#      author/copyright. When a LICENSE/LICENSE.txt file ships in the
#      package, capture its sha1.
#   3. Run scancode on every package folder so we have an independent
#      reading of the actual license text — not just what the .nuspec
#      claims. Disagreements are flagged.
#   4. Write a JSON manifest at Excise.App/Assets/third-party-licenses.json
#      that the AboutWindow loads at runtime as an embedded resource.
#
# Usage:
#   scripts/generate-license-manifest.sh [--scancode] [--project Excise.App]
#
#   --scancode      run scancode-toolkit cross-check (slow, ~3-5 min)
#   --project P     project to resolve deps from (default Excise.App)
#
# Outputs:
#   Excise.App/Assets/third-party-licenses.json
#   artifacts/scancode/<package>.json (when --scancode)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

PROJECT="Excise.App/Excise.App.csproj"
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

OUT="$ROOT/Excise.App/Assets/third-party-licenses.json"
SCANCODE_DIR="$ROOT/artifacts/scancode"
mkdir -p "$(dirname "$OUT")"
[[ "$RUN_SCANCODE" == "1" ]] && mkdir -p "$SCANCODE_DIR"

echo "▶ Restoring $PROJECT"
# Don't suppress output: a hidden `>/dev/null` masked an NU3012 cold-restore
# failure during the v2.4.0 release (#387). Let restore errors be visible.
dotnet restore "$ROOT/$PROJECT"

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
    'Microsoft.NETCore.Platforms'         # reference-only: ships runtime.json, no runtime DLL
    'NETStandard.Library'                 # reference-only meta-package, no runtime DLL
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
    # Reference-only packages that carry no runtime DLL and are therefore
    # not redistributed in the app. NETStandard.Library is a meta-package;
    # Microsoft.NETCore.Platforms ships only runtime.json. Excluding them
    # also sidesteps their legacy .NET Library EULA licenseUrl (LinkId=329770),
    # which is not an SPDX-resolvable license for attribution purposes.
    "Microsoft.NETCore.Platforms",
    "NETStandard.Library",
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

# CSJ2K's NuGet package ships no LICENSE file — only a licenseUrl pointing
# at the generic OSI BSD page. The wrapper is BSD-licensed and the JPEG 2000
# core (JJ2000) carries a specific copyright notice that BSD-style
# redistribution requires we reproduce. Both are embedded here verbatim so
# the About dialog can satisfy the attribution obligation offline.
CSJ2K_LICENSE_TEXT = '''CSJ2K is licensed under the BSD License.

Copyright (c) 1999-2000 JJ2000 Partners; original C# port (c) 2007-2012
Jason S. Clary; C# encoding and adaptation to Portable Class Library with
platform specific support (c) 2013-2018 Anders Gustafsson, Cureos AB.

Licensed and distributable under the terms of the BSD license:
http://www.opensource.org/licenses/bsd-license.php

----------------------------------------------------------------------
The JPEG 2000 core (JJ2000) carries the following copyright notice, which
must be included in all copies or derivative works of this software module:

COPYRIGHT:

This software module was originally developed by Raphaël Grosbois and
Diego Santa Cruz (Swiss Federal Institute of Technology-EPFL); Joel
Askelöf (Ericsson Radio Systems AB); and Bertrand Berthelot, David
Bouchard, Félix Henry, Gerard Mozelle and Patrice Onno (Canon Research
Centre France S.A) in the course of development of the JPEG2000 standard
as specified by ISO/IEC 15444 (JPEG 2000 Standard). This software module
is an implementation of a part of the JPEG 2000 Standard. Swiss Federal
Institute of Technology-EPFL, Ericsson Radio Systems AB and Canon Research
Centre France S.A (collectively JJ2000 Partners) agree not to assert
against ISO/IEC and users of the JPEG 2000 Standard (Users) any of their
rights under the copyright, not including other intellectual property
rights, for this software module with respect to the usage by ISO/IEC and
Users of this software module or modifications thereof for use in hardware
or software products claiming conformance to the JPEG 2000 Standard. Those
intending to use this software module in hardware or software products are
advised that their use may infringe existing patents. The original
developers of this software module, JJ2000 Partners and ISO/IEC assume no
liability for use of this software module or modifications thereof. No
license or right to this software module is granted for non JPEG 2000
Standard conforming products. JJ2000 Partners have full right to use this
software module for his/her own purpose, assign or donate this software
module to any third party and to inhibit third parties from using this
software module for non JPEG 2000 Standard conforming products. This
copyright notice must be included in all copies or derivative works of
this software module.

Copyright (c) 1999/2000 JJ2000 Partners.'''

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
    # BitMiracle.LibJpeg.NET bundles a verbatim 3-clause BSD license.txt
    # (captured as licenseText) but declares only a legacy licenseUrl, so
    # the SPDX id has to be set explicitly.
    "BitMiracle.LibJpeg.NET": {
        "licenseName": "BSD 3-Clause License",
        "spdx": "BSD-3-Clause",
        "licenseSpdxUrl": "https://spdx.org/licenses/BSD-3-Clause.html",
    },
    # CSJ2K ships no LICENSE file; embed the BSD notice + JJ2000 copyright.
    #
    # SPDX classified BSD-2-Clause by Marc (#1063). ⚠️ The evidence is WEAKER
    # than every other entry here and that is recorded on purpose: no licence
    # text ships in the package and none exists upstream. The .nuspec has no
    # <license> element, only licenseUrl -> the opensource.org 2-clause page,
    # and the upstream README says only "Licensed and distributable under the
    # terms of the BSD license" linking that same page. The determination rests
    # on two statements and no text.
    "CSJ2K": {
        "licenseName": "BSD License (CSJ2K / JJ2000)",
        "spdx": "BSD-2-Clause",
        "licenseSpdxUrl": "https://spdx.org/licenses/BSD-2-Clause.html",
        "licenseText": CSJ2K_LICENSE_TEXT,
    },
    # ANGLE ships its licence text in the package and it is the ANGLE Project
    # BSD with the third no-endorsement clause present, so this is a strong
    # determination -- read from the shipped file, not inferred (#1063).
    "Avalonia.Angle.Windows.Natives": {
        "licenseName": "BSD 3-Clause License (ANGLE Project)",
        "spdx": "BSD-3-Clause",
        "licenseSpdxUrl": "https://spdx.org/licenses/BSD-3-Clause.html",
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

# DETERMINISTIC OUTPUT (#1081). No wall-clock timestamp: this file used to
# carry "generatedAt", so every regeneration diffed even when no dependency had
# changed, and the signal was buried in noise every single time. A manifest
# nobody can review by diff is a manifest nobody reviews.
#
# It is also what makes the release able to VERIFY this file instead of
# overwriting it (#1082) -- with a timestamp in it, every release drifts by
# construction and the check could only ever fail.
#
# Packages are sorted by id so the ORDER cannot vary with dictionary or
# filesystem iteration either. sort_keys sorts each package's own fields.
results.sort(key=lambda p: (p.get("id") or "").lower())
with open(out_path, "w") as f:
    json.dump({"project": "$PROJECT",
               "packages": results}, f, indent=2, sort_keys=True)
    f.write("\n")

print(f"  wrote {out_path}  ({len(results)} packages)")
mismatches = [p for p in results if p.get("scancodeMismatch")]
if mismatches:
    print(f"  ⚠ scancode flagged {len(mismatches)} declared/detected license mismatch(es):", file=sys.stderr)
    for p in mismatches:
        print(f"     {p['nugetId']} {p['nugetVersion']}: declared {p.get('spdx')}, detected {p.get('scancodeDetectedSpdx')}", file=sys.stderr)
PY

echo "▶ Done"

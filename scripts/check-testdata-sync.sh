#!/usr/bin/env bash
#
# Fail fast when project-authored test data references code that no longer
# exists (#678).
#
# test-pdfs/ is (correctly) excluded from mechanical renames because it holds
# third-party PDFs — but that subtree ALSO holds project-authored manifests that
# reference our own source paths (e.g. an evidence "file" pointing at a test
# .cs). Those references are not compile-checked, so a rename or a moved file
# silently rots them until some unrelated test happens to trip over it. The
# v3.0 pdfe->Excise rename did exactly that: pdf20-operator-evidence.json still
# pointed at Pdfe.Core.Tests/... and only failed deep inside a conformance test.
#
# This gate resolves every "file" path reference in test-pdfs/manifests/*.json
# against the working tree, so drift fails immediately and points at the
# offending manifest.
#
# (Rendering-quality contract *vocabulary* drift — e.g. EXCISE_BETTER_THAN_REFS
# — is already validated at load time by RenderingQualityContractSet.Load and
# CorpusScanClassificationTests, so it is out of scope here.)
#
# Usage: scripts/check-testdata-sync.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

python3 - "$ROOT" <<'PY'
import json, os, sys, glob

root = sys.argv[1]
manifest_glob = os.path.join(root, "test-pdfs", "manifests", "*.json")
dangling = []
checked = 0

def looks_like_repo_path(v):
    # A repo-relative source path, not a URL / absolute path / bare filename.
    return (
        isinstance(v, str)
        and "/" in v
        and not v.startswith(("http://", "https://", "/"))
        and not v.startswith("..")
    )

def walk(obj, manifest):
    global checked
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k == "file" and looks_like_repo_path(v):
                checked += 1
                if not os.path.exists(os.path.join(root, v)):
                    dangling.append((os.path.relpath(manifest, root), v))
            else:
                walk(v, manifest)
    elif isinstance(obj, list):
        for x in obj:
            walk(x, manifest)

manifests = sorted(glob.glob(manifest_glob))
if not manifests:
    # Zero manifests is a FAILURE, not a pass. They are TRACKED files (5 of
    # them, and test-pdfs/manifests/ is NOT gitignored even though test-pdfs/
    # mostly is), so "none found" means the checkout is wrong or the directory
    # moved — and this gate would otherwise report green having checked nothing.
    # Same rule scripts/lib-runner.sh applies to a --filter matching no tests.
    print("FAIL: no manifests under test-pdfs/manifests/. They are tracked files,")
    print("      so finding none means this gate measured nothing — that is a")
    print("      vacuous pass, not a clean run.")
    sys.exit(1)

for m in manifests:
    try:
        with open(m) as fh:
            walk(json.load(fh), m)
    except json.JSONDecodeError as e:
        dangling.append((os.path.relpath(m, root), f"<invalid JSON: {e}>"))

if dangling:
    print("FAIL: project-authored test data references files that do not exist (#678):\n")
    from collections import Counter
    for (man, ref), n in sorted(Counter(dangling).items()):
        times = f"  (x{n})" if n > 1 else ""
        print(f"  {man}{times}\n      -> {ref}")
    print("\nUpdate the manifest to match the current code, or restore the file.")
    sys.exit(1)

print(f"OK: {checked} 'file' references across {len(manifests)} manifest(s) resolve.")
PY

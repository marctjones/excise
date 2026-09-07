#!/usr/bin/env python3
"""Fail when a registry verification.checks[].ref does not resolve to a
discovered test (#1344).

Before this gate, 161 of 179 refs were prose left by a migration ("legacy
matrix evidence for X") that the evidence join (build-pdf-evidence-
attribution.py::method) could never match, so their modes silently read
not-contracted forever and nobody noticed for weeks. This is what stops that
rotting back in: any new or edited check with a ref that isn't a real,
currently-discoverable test method fails the gate immediately, in the same
run that introduced it.

Resolves against the SOURCE-derived test inventory (generated/test-suite-
evidence-map.json + generated/renderer-test-evidence-map.json), not against
the test-outcomes.json TRX snapshot: the snapshot only updates on a full-tier
run, so resolving against it would fail t0 for a ref to a test written since
the last full run, purely because nobody has run the suite yet. Existence and
pass/fail credit are different questions -- #1346 is the second one.
"""
from __future__ import annotations

import glob
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REG = ROOT / "test-pdfs/manifests/pdf-spec-registry"


def method(name: str) -> str:
    name = name.rsplit("::", 1)[-1]
    m = re.search(r"\.([A-Za-z_][A-Za-z0-9_]*)\(", name)
    return m.group(1) if m else name.rsplit(".", 1)[-1]


def discovered_methods(gen: Path) -> set[str]:
    found: set[str] = set()
    for name in ("test-suite-evidence-map.json", "renderer-test-evidence-map.json"):
        path = gen / name
        if not path.is_file():
            continue
        for row in json.loads(path.read_text()).get("tests", []):
            if row.get("method"):
                found.add(row["method"])
    return found


def check(section_glob: str, gen: Path) -> list[str]:
    known = discovered_methods(gen)
    if not known:
        return ["no discovered tests found under generated/*-evidence-map.json; run the evidence-map builders first"]
    errors = []
    for path in sorted(glob.glob(section_glob)):
        data = json.loads(Path(path).read_text())
        for cap in data.get("capabilities", []):
            for chk in cap.get("verification", {}).get("checks", []):
                ref = chk.get("ref", "")
                if method(ref) not in known:
                    errors.append(f"{cap.get('id')} ({Path(path).name}): unresolved check ref {ref!r}")
    return errors


def self_test() -> int:
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        gen = tmp_path / "generated"
        gen.mkdir()
        (gen / "test-suite-evidence-map.json").write_text(json.dumps({
            "tests": [{"id": "Foo.cs::Foo.RealTest", "method": "RealTest"}]
        }))
        (gen / "renderer-test-evidence-map.json").write_text(json.dumps({"tests": []}))
        sections = tmp_path / "sections"
        sections.mkdir()
        good = {"capabilities": [{"id": "cap.good", "verification": {"checks": [
            {"ref": "Foo.cs::RealTest", "modes": ["parse"]}
        ]}}]}
        (sections / "good.json").write_text(json.dumps(good))
        errors = check(str(sections / "*.json"), gen)
        if errors:
            print(f"SELFTEST FAIL: a real, discoverable ref was rejected: {errors}", file=sys.stderr)
            return 1

        bad = {"capabilities": [{"id": "cap.bad", "verification": {"checks": [
            {"ref": "legacy matrix evidence for cap.bad", "modes": ["parse"]}
        ]}}]}
        (sections / "bad.json").write_text(json.dumps(bad))
        errors = check(str(sections / "*.json"), gen)
        if not any("cap.bad" in e for e in errors):
            print("SELFTEST FAIL: an unresolvable placeholder ref was NOT flagged", file=sys.stderr)
            return 1
    print("selftest passed: a real ref resolves, an unresolvable placeholder ref is rejected")
    return 0


def main() -> int:
    if "--self-test" in sys.argv:
        return self_test()
    errors = check(str(REG / "sections/*.json"), REG / "generated")
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        print(f"{len(errors)} verification.checks ref(s) do not resolve to a discovered test", file=sys.stderr)
        return 1
    print("all verification.checks refs resolve to a discovered test")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

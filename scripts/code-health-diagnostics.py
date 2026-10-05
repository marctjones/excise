#!/usr/bin/env python3
"""Capture a deterministic baseline for the selected Roslyn and whitespace rules."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import tempfile
from collections import Counter, defaultdict
from pathlib import Path


RULES = ["IDE0051", "IDE0052", "WHITESPACE"]
SDK_BAND = "10.0.400 feature band"
STYLE_COMMAND = [
    "dotnet", "format", "style", "excise.sln", "--no-restore",
    "--diagnostics", "IDE0051", "IDE0052", "--severity", "warn",
    "--verify-no-changes",
]
WHITESPACE_COMMAND = [
    "dotnet", "format", "whitespace", "excise.sln", "--no-restore",
    "--verify-no-changes",
]


def normalized_source(data: bytes) -> str:
    """Match File.ReadAllText's BOM-aware decoding, then the C# LF contract."""
    # Test UTF-32 before UTF-16: their little-endian BOMs share a prefix.
    for marker, encoding in (
        (b"\xff\xfe\x00\x00", "utf-32-le"),
        (b"\x00\x00\xfe\xff", "utf-32-be"),
        (b"\xef\xbb\xbf", "utf-8"),
        (b"\xff\xfe", "utf-16-le"),
        (b"\xfe\xff", "utf-16-be"),
    ):
        if data.startswith(marker):
            text = data[len(marker):].decode(encoding, errors="replace")
            break
    else:
        text = data.decode("utf-8", errors="replace")
    return text.replace("\r\n", "\n").replace("\r", "\n")


def run_report(root: Path, command: list[str], report: Path) -> tuple[list[dict], int, str]:
    full_command = command + ["--report", str(report), "--verbosity", "quiet"]
    result = subprocess.run(full_command, cwd=root, text=True, capture_output=True, check=False)
    if result.returncode not in (0, 2):
        raise RuntimeError(f"Command failed ({result.returncode}): {' '.join(full_command)}\n{result.stdout}{result.stderr}")
    if not report.is_file():
        raise RuntimeError(f"Formatter did not create report: {report}\n{result.stdout}{result.stderr}")
    return json.loads(report.read_text(encoding="utf-8")), result.returncode, result.stdout + result.stderr


def load_symbols(root: Path, inventory_path: Path | None = None) -> tuple[dict[str, list[dict]], dict[str, str], dict]:
    path = inventory_path or root / "tests/code-health/symbols.json"
    if not path.is_absolute():
        path = root / path
    if not path.is_file():
        raise RuntimeError(f"Missing symbol inventory: {path}. Generate it with the #1939 command first.")
    inventory_bytes = path.read_bytes()
    inventory = json.loads(inventory_bytes)
    if inventory.get("SchemaVersion") != 1 or not inventory.get("Inputs"):
        raise RuntimeError("Inventory must have schema 1 and nonempty tracked source inputs.")
    tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")
    tracked_cs = {p for p in tracked if p.endswith(".cs")}
    inventory_paths = [row["Path"] for row in inventory["Inputs"]]
    if len(inventory_paths) != len(set(inventory_paths)) or set(inventory_paths) != tracked_cs:
        raise RuntimeError("Stale inventory source set: tracked C# additions/removals differ; regenerate #1939 inventory.")
    for source in inventory["Inputs"]:
        source_path = (root / source["Path"]).resolve()
        if not source_path.is_relative_to(root.resolve()):
            raise RuntimeError("Inventory source escaped repository root.")
        normalized = normalized_source(source_path.read_bytes())
        if hashlib.sha256(normalized.encode("utf-8")).hexdigest() != source["SourceHash"]:
            raise RuntimeError(f"Stale inventory source hash: {source['Path']}; regenerate #1939 inventory.")
    scope = normalized_source((root / "architecture/repository-scope.json").read_bytes())
    if hashlib.sha256(scope.encode("utf-8")).hexdigest() != inventory["ScopeHash"]:
        raise RuntimeError("Stale inventory scope hash; regenerate #1939 inventory.")
    symbols: dict[str, list[dict]] = defaultdict(list)
    projects: dict[str, str] = {}
    for source in inventory["Inputs"]:
        if source.get("Project"):
            projects[source["Path"]] = source["Project"]
    for symbol in inventory["Symbols"]:
        symbols[symbol["Path"]].append(symbol)
    for rows in symbols.values():
        rows.sort(key=lambda row: (row["Line"], row["Key"]))
    inputs = sorted(inventory["Inputs"], key=lambda row: row["Path"])
    input_hashes = "excise-code-health-diagnostic-inputs-v1\n" + "".join(
        f"{row['Path']}\t{row['SourceHash']}\n" for row in inputs
    )
    snapshot = {
        "inventoryPath": "explicit-inventory" if inventory_path else "tests/code-health/symbols.json",
        "inventorySha256": hashlib.sha256(inventory_bytes).hexdigest(),
        "sourceInputsSha256": hashlib.sha256(input_hashes.encode("utf-8")).hexdigest(),
        "sourceInputCount": len(inputs),
        "inventoryToolVersion": inventory["ToolVersion"],
        "inventoryToolSourceHash": inventory["ToolSourceHash"],
        "inventoryRoslynVersion": inventory["RoslynVersion"],
        "scopeSha256": inventory["ScopeHash"],
    }
    return symbols, projects, snapshot


def relative_path(root: Path, absolute: str) -> str:
    path = Path(absolute)
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        raise RuntimeError(f"Diagnostic escaped repository root: {absolute}")


def owner_for(path: str, line: int, symbols: dict[str, list[dict]]) -> str:
    rows = symbols.get(path, [])
    preceding = [row for row in rows if row["Line"] <= line]
    if preceding:
        return preceding[-1]["Key"]
    if rows:
        return rows[0]["Key"]
    return f"file:{path}"


def collect(root: Path, inventory_path: Path | None = None) -> dict:
    symbols, projects, source_snapshot = load_symbols(root, inventory_path)
    with tempfile.TemporaryDirectory(prefix="pdfe-code-health-") as temporary:
        tmp = Path(temporary)
        style, style_exit, style_output = run_report(root, STYLE_COMMAND, tmp / "style.json")
        whitespace, whitespace_exit, whitespace_output = run_report(root, WHITESPACE_COMMAND, tmp / "whitespace.json")

    grouped: dict[tuple[str, str, str, str], list[int]] = defaultdict(list)
    for entry in style + whitespace:
        absolute = entry.get("FilePath")
        if not absolute:
            continue
        path = relative_path(root, absolute)
        for change in entry.get("FileChanges", []):
            rule = change.get("DiagnosticId")
            if rule not in RULES:
                continue
            line = int(change.get("LineNumber", 0))
            project = projects.get(path) or ""
            owner = owner_for(path, line, symbols)
            grouped[(rule, project, owner, path)].append(line)

    findings = []
    for (rule, project, symbol, path), lines in sorted(grouped.items()):
        findings.append({
            "rule": rule,
            "project": project,
            "symbol": symbol,
            "file": path,
            "count": len(lines),
            "lines": sorted(lines),
        })

    sdk = subprocess.run(["dotnet", "--version"], cwd=root, text=True, capture_output=True, check=True).stdout.strip()
    formatter = subprocess.run(["dotnet", "format", "--version"], cwd=root, text=True, capture_output=True, check=True).stdout.strip()
    version_parts = sdk.split(".")
    if len(version_parts) < 3 or version_parts[:2] != ["10", "0"] or not version_parts[2].startswith("4"):
        raise RuntimeError(f"Expected pinned .NET SDK {SDK_BAND}; selected {sdk}.")
    _, _, after_snapshot = load_symbols(root, inventory_path)
    if after_snapshot != source_snapshot:
        raise RuntimeError("Inventory or sources changed during diagnostics capture.")
    return {
        "schemaVersion": 1,
        "tool": "dotnet format from the pinned .NET SDK",
        "sdkFeatureBand": SDK_BAND,
        "sdkVersion": sdk,
        "formatterVersion": formatter,
        "rules": RULES,
        "ruleset": ".editorconfig IDE0051/IDE0052=warning; dotnet format whitespace defaults from pinned SDK",
        "commands": [STYLE_COMMAND, WHITESPACE_COMMAND],
        "symbolInventory": source_snapshot["inventoryPath"],
        "sourceSnapshot": source_snapshot,
        "symbolResolution": "nearest preceding declaration line in same file; file:<path> when no declaration exists",
        "findings": findings,
        "totalFindings": sum(row["count"] for row in findings),
        "formatterExitCodes": {
            "style": style_exit,
            "whitespace": whitespace_exit,
        },
    }


def counts(report: dict) -> Counter:
    result = Counter()
    for row in report["findings"]:
        result[(row["rule"], row["project"], row["symbol"], row["file"])] += row["count"]
    return result


def compare(baseline: dict, current: dict) -> list[tuple[tuple, int]]:
    old = counts(baseline)
    now = counts(current)
    return sorted((key, count - old[key]) for key, count in now.items() if count > old[key])


def self_test() -> None:
    text = "class Unicode { string Text = \"café😀\"; }\n"
    normalized_hash = hashlib.sha256(text.encode("utf-8")).hexdigest()
    for marker, encoding in (
        (b"", "utf-8"), (b"\xef\xbb\xbf", "utf-8"),
        (b"\xff\xfe", "utf-16-le"), (b"\xfe\xff", "utf-16-be"),
        (b"\xff\xfe\x00\x00", "utf-32-le"), (b"\x00\x00\xfe\xff", "utf-32-be"),
    ):
        encoded_source = marker + text.replace("\n", "\r\n").encode(encoding)
        if hashlib.sha256(normalized_source(encoded_source).encode("utf-8")).hexdigest() != normalized_hash:
            raise RuntimeError(f"Self-test failed: {encoding} BOM/line-ending normalization differs from UTF-8.")
    sample = {
        "Inputs": [
            {"Path": "B.cs", "SourceHash": "bb"},
            {"Path": "A.cs", "SourceHash": "aa"},
        ],
        "ToolVersion": "test/1",
        "ToolSourceHash": "tool-hash",
        "RoslynVersion": "test-roslyn",
        "ScopeHash": "scope-hash",
    }
    encoded = json.dumps(sample, sort_keys=True).encode("utf-8")
    inventory_sha = hashlib.sha256(encoded).hexdigest()
    input_rows = sorted(sample["Inputs"], key=lambda row: row["Path"])
    input_bytes = ("excise-code-health-diagnostic-inputs-v1\n" + "".join(
        f"{row['Path']}\t{row['SourceHash']}\n" for row in input_rows
    )).encode("utf-8")
    input_sha = hashlib.sha256(input_bytes).hexdigest()
    reordered = dict(sample, Inputs=list(reversed(sample["Inputs"])))
    reordered_bytes = json.dumps(reordered, sort_keys=True).encode("utf-8")
    if hashlib.sha256(reordered_bytes).hexdigest() == inventory_sha:
        raise RuntimeError("Self-test failed: inventory artifact fingerprint ignored serialized order.")
    if hashlib.sha256(("excise-code-health-diagnostic-inputs-v1\n" + "".join(
        f"{row['Path']}\t{row['SourceHash']}\n" for row in sorted(reordered["Inputs"], key=lambda row: row["Path"])
    )).encode("utf-8")).hexdigest() != input_sha:
        raise RuntimeError("Self-test failed: source input fingerprint depends on input ordering.")
    changed_input = [dict(row) for row in sample["Inputs"]]
    changed_input[0]["SourceHash"] = "changed"
    changed_bytes = ("excise-code-health-diagnostic-inputs-v1\n" + "".join(
        f"{row['Path']}\t{row['SourceHash']}\n" for row in sorted(changed_input, key=lambda row: row["Path"])
    )).encode("utf-8")
    if hashlib.sha256(changed_bytes).hexdigest() == input_sha:
        raise RuntimeError("Self-test failed: source input fingerprint ignored a source hash change.")

    with tempfile.TemporaryDirectory(prefix="pdfe-code-health-selftest-") as temporary:
        root = Path(temporary)
        subprocess.run(["git", "init", "--quiet"], cwd=root, check=True)
        (root / "architecture").mkdir()
        scope_path = root / "architecture/repository-scope.json"
        scope_path.write_text("{}\n", encoding="utf-8")
        project = root / "Probe.csproj"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
            '<Nullable>enable</Nullable></PropertyGroup></Project>\n', encoding="utf-8")
        (root / ".editorconfig").write_text(
            "root = true\n\n[*.cs]\nindent_style = space\nindent_size = 4\n"
            "dotnet_diagnostic.IDE0051.severity = warning\n", encoding="utf-8")
        (root / "Probe.cs").write_text(
            "namespace Probe;\npublic sealed class Example\n{\n  private int Unused;\n}\n", encoding="utf-8")
        subprocess.run(["git", "add", "Probe.cs"], cwd=root, check=True)
        inventory = dict(sample, SchemaVersion=1, Symbols=[], Inputs=[{
            "Path": "Probe.cs", "SourceHash": hashlib.sha256((root / "Probe.cs").read_bytes()).hexdigest()
        }], ScopeHash=hashlib.sha256(scope_path.read_bytes()).hexdigest())
        inventory_path = root / "fresh.json"
        inventory_path.write_text(json.dumps(inventory), encoding="utf-8")
        _, _, snapshot = load_symbols(root, inventory_path)
        if snapshot["inventorySha256"] != hashlib.sha256(inventory_path.read_bytes()).hexdigest():
            raise RuntimeError("Self-test failed: explicit inventory fingerprint changed.")
        def rejects_stale():
            try:
                load_symbols(root, inventory_path)
            except RuntimeError:
                return
            raise RuntimeError("Self-test failed: stale inventory was accepted.")
        probe_source = (root / "Probe.cs").read_bytes()
        (root / "Probe.cs").write_bytes(b"\xef\xbb\xbf" + probe_source)
        scope_path.write_bytes(b"\xef\xbb\xbf" + b"{}\r\n")
        load_symbols(root, inventory_path)
        scope_path.write_text("{}\n", encoding="utf-8")
        (root / "Probe.cs").write_bytes(probe_source + b"// changed\n")
        rejects_stale()
        (root / "Probe.cs").write_bytes(probe_source)
        (root / "Added.cs").write_text("class Added {}", encoding="utf-8")
        subprocess.run(["git", "add", "Added.cs"], cwd=root, check=True)
        rejects_stale()
        subprocess.run(["git", "rm", "--force", "Added.cs"], cwd=root, check=True, capture_output=True)
        scope_path.write_text("{\"changed\":true}\n", encoding="utf-8")
        rejects_stale()
        scope_path.write_text("{}\n", encoding="utf-8")
        with tempfile.TemporaryDirectory(prefix="pdfe-code-health-probe-reports-") as reports:
            report_root = Path(reports)
            style, _, _ = run_report(root, [
                "dotnet", "format", "style", str(project), "--diagnostics", "IDE0051",
                "--severity", "warn", "--verify-no-changes", "--no-restore",
            ], report_root / "style.json")
            whitespace, _, _ = run_report(root, [
                "dotnet", "format", "whitespace", str(project), "--verify-no-changes", "--no-restore",
            ], report_root / "whitespace.json")
        planted = {"findings": [
            {"rule": change["DiagnosticId"], "project": "Probe.csproj", "symbol": "file:Probe.cs",
             "file": "Probe.cs", "count": 1, "lines": [change["LineNumber"]]}
            for entry in style + whitespace for change in entry.get("FileChanges", [])
        ]}
        if not any(row["rule"] == "IDE0051" for row in planted["findings"]):
            raise RuntimeError("Self-test failed: planted unused private field was not diagnosed.")
        baseline = {"findings": []}
        if not compare(baseline, planted):
            raise RuntimeError("Self-test failed: baseline comparison accepted the planted warning.")
        row = planted["findings"][0]
        if not compare({"findings": [row]}, {"findings": [row, row]}):
            raise RuntimeError("Self-test failed: repeated findings were overwritten.")
        if not any(row["rule"] == "WHITESPACE" for row in planted["findings"]):
            raise RuntimeError("Self-test failed: planted whitespace violation was not diagnosed.")
    print("PASS: UTF-8/16/32 BOM-aware source/scope fingerprints; explicit inventory fingerprints; stale source/set/scope rejected; repeated counts preserved; order-stable source fingerprints; planted IDE0051 and whitespace violations rejected.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("capture", "check", "self-test"))
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--baseline", type=Path, default=Path("tests/code-health/diagnostics.json"))
    parser.add_argument("--inventory", type=Path, help="fresh #1939 inventory; relative paths resolve under --root")
    parser.add_argument("--write-baseline", action="store_true", help="replace baseline after human review")
    args = parser.parse_args()
    root = args.root.resolve()
    if args.mode == "self-test":
        self_test()
        return 0
    current = collect(root, args.inventory)
    baseline_path = args.baseline if args.baseline.is_absolute() else root / args.baseline
    if args.mode == "capture":
        output = json.dumps(current, indent=2, sort_keys=True) + "\n"
        if args.write_baseline:
            baseline_path.write_text(output, encoding="utf-8", newline="\n")
        else:
            sys.stdout.write(output)
        return 0
    baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    regressions = compare(baseline, current)
    if regressions:
        for key, increase in regressions:
            rule, project, symbol, path = key
            print(f"NEW {rule} +{increase}: {symbol} ({project}) at {path}", file=sys.stderr)
        return 1
    print(f"PASS: no analyzer or whitespace diagnostic exceeds the pinned legacy baseline ({current['totalFindings']} findings).")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, KeyError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(2)

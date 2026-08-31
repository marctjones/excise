#!/usr/bin/env python3
"""Probe every registered external PDF reference tool and record reproducible availability evidence.

This deliberately performs only each tool's version command.  Rendering,
extraction, and validation remain owned by named tests/benchmarks; discovery
must never feed an untrusted PDF to a tool merely to decide if it is installed.
"""
from __future__ import annotations

import json
import shlex
import shutil
import subprocess
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REG = ROOT / "test-pdfs/manifests/pdf-spec-registry"
OUT = REG / "generated/reference-tool-evidence.json"


def probe(tool: dict) -> dict:
    argv = shlex.split(tool["versionCommand"])
    executable = shutil.which(argv[0])
    base = {
        "id": tool["id"],
        "declaredAvailability": tool["availability"],
        "command": tool["command"],
        "versionCommand": tool["versionCommand"],
        "resolvedExecutable": executable,
        "observations": tool["observations"],
        "limitations": tool["limitations"],
    }
    if not executable:
        return {**base, "status": "not-installed", "version": None, "output": None}
    try:
        completed = subprocess.run(argv, cwd=ROOT, text=True, capture_output=True,
            timeout=tool["timeoutSeconds"], check=False)
    except subprocess.TimeoutExpired:
        return {**base, "status": "version-command-timeout", "version": None, "output": None}
    output = (completed.stdout + completed.stderr).strip()
    # MuPDF's `mutool --version` reports its valid version then exits non-zero
    # on some builds.  A discovered executable with a non-empty version banner
    # is available; preserve the unusual exit code for diagnosis.
    module_missing = "No module named" in output or "ModuleNotFoundError" in output
    available = completed.returncode == 0 or (bool(output) and tool["id"] == "mupdf-mutool")
    return {
        **base,
        "status": "not-installed" if module_missing else "available" if available else "version-command-failed",
        "exitCode": completed.returncode,
        "version": output.splitlines()[0] if output else None,
        "output": output[:2000],
    }


def main() -> None:
    manifest = json.loads((REG / "reference-tools.json").read_text(encoding="utf-8"))
    tools = [probe(tool) for tool in manifest["tools"]]
    result = {
        "schemaVersion": 1,
        "generatedBy": "scripts/collect-pdf-reference-tool-evidence.py",
        "policy": "This records executable/version availability only. It does not run PDFs, establish conformance, or turn a reference renderer into an oracle without a named fixture/test contract.",
        "recordedAt": datetime.now(timezone.utc).isoformat(),
        "tools": tools,
        "summary": {
            "registered": len(tools),
            "available": sum(tool["status"] == "available" for tool in tools),
            "notInstalled": sum(tool["status"] == "not-installed" for tool in tools),
            "unhealthy": sum(tool["status"] not in {"available", "not-installed"} for tool in tools),
        },
    }
    OUT.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {OUT} with {result['summary']['available']}/{len(tools)} available reference tools")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Extract a local ISO 32000-2:2020 EC3 skeleton for PDF 2.0 curation.

This script intentionally writes only metadata, clause headings, table labels,
and operator names. It must not commit or emit long copied ISO text.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_ISO = Path("/Users/marc/Downloads/ISO_32000-2_sponsored_EC3.pdf")
ANNEX_A_OPERATORS = [
    "q", "Q", "cm", "w", "J", "j", "M", "d", "ri", "i", "gs",
    "m", "l", "c", "v", "y", "h", "re", "S", "s", "f", "F", "f*",
    "B", "B*", "b", "b*", "n", "W", "W*", "CS", "cs", "SC", "SCN",
    "sc", "scn", "G", "g", "RG", "rg", "K", "k", "sh", "BI", "ID",
    "EI", "Do", "BT", "ET", "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts",
    "Td", "TD", "Tm", "T*", "Tj", "TJ", "'", "\"", "d0", "d1", "BMC",
    "BDC", "EMC", "MP", "DP", "BX", "EX",
]


def run_text_command(command: list[str]) -> str:
    completed = subprocess.run(command, check=True, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return completed.stdout


def extract_text(pdf: Path) -> str:
    with tempfile.TemporaryDirectory() as tmp:
        txt = Path(tmp) / "iso.txt"
        subprocess.run(["pdftotext", "-layout", str(pdf), str(txt)], check=True)
        return txt.read_text(encoding="utf-8", errors="ignore")


def clause_headings(text: str, limit: int) -> list[dict[str, str]]:
    pattern = re.compile(r"^(?P<clause>(?:[1-9]\d?)(?:\.\d+){0,5})\s+(?P<title>[A-Z][^\n]{2,120})$", re.MULTILINE)
    rows: list[dict[str, str]] = []
    seen: set[str] = set()
    for match in pattern.finditer(text):
        clause = match.group("clause")
        title = " ".join(match.group("title").split())
        if clause in seen:
            continue
        seen.add(clause)
        rows.append({"clause": clause, "heading": title[:140]})
        if len(rows) >= limit:
            break
    return rows


def table_labels(text: str, limit: int) -> list[dict[str, str]]:
    pattern = re.compile(r"^\s*(Table\s+\d+\s+[\u2012-\u2015-]\s+[^.\n]{3,120})", re.MULTILINE)
    rows: list[dict[str, str]] = []
    seen: set[str] = set()
    for match in pattern.finditer(text):
        label = " ".join(match.group(1).split())
        if label in seen:
            continue
        seen.add(label)
        rows.append({"label": label[:140]})
        if len(rows) >= limit:
            break
    return rows


def pdfinfo(pdf: Path) -> dict[str, str]:
    try:
        output = run_text_command(["pdfinfo", str(pdf)])
    except (OSError, subprocess.CalledProcessError):
        return {}
    result: dict[str, str] = {}
    for line in output.splitlines():
        if ":" not in line:
            continue
        key, value = line.split(":", 1)
        result[key.strip()] = value.strip()
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", default=str(DEFAULT_ISO), help="Local ISO 32000-2 sponsored EC3 PDF path.")
    parser.add_argument("--output", default="logs/pdf20/pdf20-generated-skeleton.json")
    parser.add_argument("--max-headings", type=int, default=240)
    parser.add_argument("--max-tables", type=int, default=120)
    args = parser.parse_args()

    iso = Path(args.iso).expanduser()
    if not iso.exists():
        raise SystemExit(f"ISO source not found: {iso}")

    text = extract_text(iso)
    skeleton: dict[str, Any] = {
        "schemaVersion": 1,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "source": {
            "id": "iso-32000-2-2020-ec3-local",
            "path": str(iso),
            "metadata": pdfinfo(iso),
            "copyrightPolicy": "No long ISO text is emitted; curation stores identifiers and paraphrases only.",
        },
        "clauseHeadings": clause_headings(text, args.max_headings),
        "tableLabels": table_labels(text, args.max_tables),
        "annexAOperators": ANNEX_A_OPERATORS,
    }

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(skeleton, indent=2), encoding="utf-8")
    print(f"wrote PDF 2.0 generated skeleton: {output}")
    print(f"clause headings: {len(skeleton['clauseHeadings'])}")
    print(f"table labels: {len(skeleton['tableLabels'])}")
    print(f"Annex A operators: {len(ANNEX_A_OPERATORS)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

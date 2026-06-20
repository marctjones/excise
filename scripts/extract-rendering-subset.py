#!/usr/bin/env python3
"""Extract a focused rendering-work subset from a corpus-scan JSON report."""

from __future__ import annotations

import argparse
import collections
import json
from pathlib import Path
from typing import Any


DEFAULT_STATUSES = (
    "DIFF",
    "MALFORMED_PDF",
    "TIMEOUT",
    "INVALID_PAGE_GEOMETRY",
    "PASSWORD_REQUIRED",
    "RESOURCE_LIMIT",
    "PASS_ONE",
)


def get(entry: dict[str, Any], key: str, default: Any = "") -> Any:
    if key in entry:
        return entry[key]
    pascal = key[:1].upper() + key[1:]
    return entry.get(pascal, default)


def fmt_float(value: Any) -> str:
    if value in (None, ""):
        return ""
    try:
        return f"{float(value):.6g}"
    except (TypeError, ValueError):
        return str(value)


def short(text: Any, limit: int = 180) -> str:
    value = "" if text is None else str(text)
    value = value.replace("\r", " ").replace("\n", " ").replace("\t", " ")
    value = " ".join(value.split())
    if len(value) <= limit:
        return value
    return value[: limit - 3] + "..."


def summarize(entry: dict[str, Any]) -> str:
    status = get(entry, "status")
    if status == "DIFF":
        return (
            "No compared oracle is within threshold; "
            f"best={get(entry, 'bestOracle', '-')}, "
            f"diff={fmt_float(get(entry, 'diffFraction'))}, "
            f"mae={fmt_float(get(entry, 'mae'))}, "
            f"compared={get(entry, 'comparedOracles', '-')}, "
            f"agreeing={get(entry, 'agreeingOracles', '-')}"
        )
    if status == "PASS_ONE":
        diagnostic = short(get(entry, "diagnostic"))
        suffix = f"; {diagnostic}" if diagnostic else ""
        return (
            "Partial oracle agreement; "
            f"best={get(entry, 'bestOracle', '-')}, "
            f"diff={fmt_float(get(entry, 'diffFraction'))}, "
            f"mae={fmt_float(get(entry, 'mae'))}, "
            f"compared={get(entry, 'comparedOracles', '-')}, "
            f"agreeing={get(entry, 'agreeingOracles', '-')}"
            f"{suffix}"
        )
    if status == "TIMEOUT":
        return (
            f"Timed out during {get(entry, 'errorPhase', '-')}; "
            f"{short(get(entry, 'errorMessage') or get(entry, 'diagnostic'))}"
        )
    if status == "PASSWORD_REQUIRED":
        return (
            "Encrypted PDF requires a non-empty user password in the no-password baseline; "
            f"{short(get(entry, 'errorMessage') or get(entry, 'diagnostic'))}"
        )
    if status == "RESOURCE_LIMIT":
        return (
            "Renderer refused an oversized page allocation; "
            f"{short(get(entry, 'errorMessage') or get(entry, 'diagnostic'))}"
        )
    if status == "INVALID_PAGE_GEOMETRY":
        return (
            "Invalid resolved page bitmap size; "
            f"{short(get(entry, 'errorMessage') or get(entry, 'diagnostic'))}"
        )
    if status == "MALFORMED_PDF":
        return (
            f"{get(entry, 'errorType', 'Open failure')} during {get(entry, 'errorPhase', 'open')}; "
            f"{short(get(entry, 'errorMessage') or get(entry, 'diagnostic'))}"
        )
    return short(get(entry, "diagnostic") or get(entry, "errorMessage"))


def markdown_escape(value: Any) -> str:
    return str(value).replace("|", "\\|")


def status_sort_key(entry: dict[str, Any]) -> tuple[str, str, int]:
    return (str(get(entry, "status")), str(get(entry, "path")), int(get(entry, "pageNumber", 0) or 0))


def write_manifest(path: Path, entries: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as out:
        out.write("path\tpageNumber\tstatus\tsummary\n")
        for entry in sorted(entries, key=status_sort_key):
            out.write(
                f"{get(entry, 'path')}\t"
                f"{get(entry, 'pageNumber', 0)}\t"
                f"{get(entry, 'status')}\t"
                f"{summarize(entry)}\n"
            )


def write_markdown(path: Path, report: dict[str, Any], entries: list[dict[str, Any]], manifest_path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    counts = collections.Counter(str(get(entry, "status")) for entry in entries)
    by_path = collections.Counter(str(get(entry, "path")) for entry in entries)
    source = Path(report.get("_source", "")).as_posix()
    corpus = report.get("corpus", "")
    generated = report.get("generatedUtc", "")

    lines: list[str] = []
    lines.append("# Focused Rendering Subset - 2026-06-20")
    lines.append("")
    lines.append(f"Source report: `{source}`")
    lines.append(f"Corpus: `{corpus}`")
    lines.append(f"Source generated UTC: `{generated}`")
    lines.append(f"Page manifest: `{manifest_path.as_posix()}`")
    lines.append("")
    lines.append("## How To Rerun")
    lines.append("")
    lines.append("```bash")
    lines.append("dotnet build -c Debug Pdfe.Cli/Pdfe.Cli.csproj")
    lines.append(
        "PDFE_PDFBOX_JAR=/private/tmp/pdfe-tools/pdfbox-app-3.0.7.jar "
        "Pdfe.Cli/bin/Debug/net10.0/pdfe corpus-scan test-pdfs "
        "--page-manifest test-pdfs/manifests/rendering-open-issues-2026-06-20.tsv "
        "--output Pdfe.Rendering.Tests/bin/Debug/net10.0/focused-rendering-open-issues-2026-06-20.json "
        "--page-mode first --extra-oracles all --parallel 1 --pdf-timeout-ms 120000"
    )
    lines.append("```")
    lines.append("")
    lines.append("`--page-manifest` overrides `--page-mode` for listed PDFs. Page `0` means an open-time failure; if a future parser fix opens that file successfully, the focused scan renders page 1 so the case can move to a normal rendering status.")
    lines.append("")
    lines.append("## Counts")
    lines.append("")
    lines.append("| Status | Entries |")
    lines.append("|---|---:|")
    for status, count in sorted(counts.items()):
        lines.append(f"| `{status}` | {count} |")
    lines.append("")
    lines.append("## Repeated Files")
    lines.append("")
    lines.append("| Entries | PDF |")
    lines.append("|---:|---|")
    for pdf, count in by_path.most_common(25):
        if count <= 1:
            continue
        lines.append(f"| {count} | `{markdown_escape(pdf)}` |")
    lines.append("")
    lines.append("## Status Guidance")
    lines.append("")
    lines.append("- `DIFF`: no compared reference renderer matched pdfe within thresholds. These are the main rendering-fidelity targets.")
    lines.append("- `PASS_ONE`: at least one reference renderer matched pdfe. Treat these as lower priority unless a visual inspection shows pdfe is the outlier.")
    lines.append("- `MALFORMED_PDF`: open-time parser/resilience failures. Some are intentionally invalid corpus files; fixes should be spec-grounded recovery, not format guesswork.")
    lines.append("- `TIMEOUT`: the timed-out phase identifies whether pdfe or a reference renderer consumed the budget. Reference timeouts are not automatically pdfe defects.")
    lines.append("- `INVALID_PAGE_GEOMETRY`: page boxes resolve to invalid bitmap dimensions. Decide whether to skip, clamp, or recover based on PDF box semantics.")
    lines.append("- `PASSWORD_REQUIRED`: encrypted files that require a non-empty user password. They belong in a separate password-aware lane, but are included here so the focused subset tracks them.")
    lines.append("- `RESOURCE_LIMIT`: pages whose resolved size would exceed the renderer pixel budget. Treat these as memory-safety outcomes unless we intentionally add lower-DPI fallback behavior.")
    lines.append("")
    lines.append("## Entries")
    lines.append("")
    lines.append("| Status | PDF | Page | Summary |")
    lines.append("|---|---|---:|---|")
    for entry in sorted(entries, key=status_sort_key):
        lines.append(
            f"| `{markdown_escape(get(entry, 'status'))}` "
            f"| `{markdown_escape(get(entry, 'path'))}` "
            f"| {get(entry, 'pageNumber', 0)} "
            f"| {markdown_escape(summarize(entry))} |"
        )
    lines.append("")
    path.write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--markdown", type=Path, required=True)
    parser.add_argument("--statuses", default=",".join(DEFAULT_STATUSES))
    args = parser.parse_args()

    report = json.loads(args.report.read_text(encoding="utf-8"))
    report["_source"] = str(args.report)
    statuses = {part.strip() for part in args.statuses.split(",") if part.strip()}
    entries = [
        entry for entry in report.get("entries", [])
        if str(get(entry, "status")) in statuses
    ]

    write_manifest(args.manifest, entries)
    write_markdown(args.markdown, report, entries, args.manifest)
    print(f"wrote {args.manifest} ({len(entries)} entries)")
    print(f"wrote {args.markdown}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

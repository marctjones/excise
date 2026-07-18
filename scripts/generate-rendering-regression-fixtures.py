#!/usr/bin/env python3
"""Generate excise-authored renderer regression PDFs.

These fixtures are intentionally small and synthetic. They capture hypotheses
from renderer debugging sessions so fixes can later be guarded by the regular
render-quality scanner without adding large external corpora.
"""

from __future__ import annotations

import argparse
from pathlib import Path


DEFAULT_OUTPUT = Path("test-pdfs/generated-regressions")


def pdf_bytes(objects: list[tuple[int, str]]) -> bytes:
    body = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets: dict[int, int] = {}
    for number, content in objects:
        offsets[number] = len(body)
        body.extend(f"{number} 0 obj\n".encode("ascii"))
        body.extend(content.encode("latin-1"))
        if not content.endswith("\n"):
            body.extend(b"\n")
        body.extend(b"endobj\n")

    xref = len(body)
    max_object = max(number for number, _ in objects)
    body.extend(f"xref\n0 {max_object + 1}\n".encode("ascii"))
    body.extend(b"0000000000 65535 f \n")
    for number in range(1, max_object + 1):
        body.extend(f"{offsets[number]:010d} 00000 n \n".encode("ascii"))
    body.extend(
        f"trailer << /Size {max_object + 1} /Root 1 0 R >>\n"
        f"startxref\n{xref}\n%%EOF\n".encode("ascii")
    )
    return bytes(body)


def stream_object(dictionary: str, data: str) -> str:
    encoded = data.encode("latin-1")
    return f"<< {dictionary} /Length {len(encoded)} >>\nstream\n{data}endstream\n"


def p7_background() -> str:
    content = (
        "q\n"
        "1 1 1 rg 0 0 160 120 re f\n"
        "0 0 0 .10 K 0 0 160 120 re f\n"
        "0 0 0 .70 K 2 w [2 3] 0 d\n"
    )
    for x in range(10, 160, 12):
        content += f"{x} 0 m {x} 120 l S\n"
    return content + "Q\n"


def p7_blend_shapes() -> str:
    return (
        "q\n"
        "/BMHard gs 0 .75 .35 0 K 16 20 45 80 re f\n"
        "/BMDark gs .8 0 .3 0 K 58 20 45 80 re f\n"
        "/BMExcl gs .2 .7 0 0 K 100 20 45 80 re f\n"
        "Q\n"
    )


def p7_form_shapes() -> str:
    return (
        "q\n"
        "/Normal gs 0 .75 .35 0 K 16 20 45 80 re f\n"
        ".8 0 .3 0 K 58 20 45 80 re f\n"
        ".2 .7 0 0 K 100 20 45 80 re f\n"
        "Q\n"
    )


def p7_pattern_text_group_pdf() -> bytes:
    page_content = (
        "q\n"
        "1 1 1 rg 0 0 160 120 re f\n"
        "/Fm1 Do\n"
        "Q\n"
    )
    form_content = (
        "q\n"
        "/Pattern cs /P1 scn\n"
        "BT /F1 42 Tf 8 42 Td (PDF) Tj ET\n"
        "Q\n"
    )
    pattern_content = (
        ".65 0 .95 rg 0 0 8 8 re f\n"
        ".15 .45 1 rg 8 0 8 8 re f\n"
        ".85 .2 .55 rg 0 8 8 8 re f\n"
        ".25 .05 .75 rg 8 8 8 8 re f\n"
    )

    objects = [
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        (
            3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 160 120] "
            "/Contents 4 0 R /Resources << /XObject << /Fm1 5 0 R >> >> >>",
        ),
        (4, stream_object("", page_content)),
        (
            5,
            stream_object(
                "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 160 120] "
                "/Resources << /ColorSpace << /CS1 [/Pattern] >> /Pattern << /P1 6 0 R >> "
                "/Font << /F1 7 0 R >> >> "
                "/Group << /S /Transparency /CS 8 0 R /I true /K false >>",
                form_content,
            ),
        ),
        (
            6,
            stream_object(
                "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 "
                "/BBox [0 0 16 16] /XStep 16 /YStep 16",
                pattern_content,
            ),
        ),
        (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        (8, "/DeviceCMYK"),
    ]
    return pdf_bytes(objects)


def p7_probe_pdf(page_content: str, *, form_content: str | None = None, group: str | None = None) -> bytes:
    invoke_form = form_content is not None
    resources = (
        "/ExtGState << "
        "/BMHard 6 0 R /BMDark 7 0 R /BMExcl 8 0 R /Normal 9 0 R "
        ">>"
    )
    if invoke_form:
        resources += " /XObject << /Fm1 5 0 R >>"

    objects = [
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        (
            3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 160 120] "
            f"/Contents 4 0 R /Resources << {resources} >> >>",
        ),
        (4, stream_object("", page_content)),
        (5, "<< /Unused true >>"),
        (6, "<< /Type /ExtGState /BM /HardLight /ca 1 /CA 1 /SMask /None >>"),
        (7, "<< /Type /ExtGState /BM /Darken /ca 1 /CA 1 /SMask /None >>"),
        (8, "<< /Type /ExtGState /BM /Exclusion /ca 1 /CA 1 /SMask /None >>"),
        (9, "<< /Type /ExtGState /BM /Normal /ca 1 /CA 1 /SMask /None >>"),
    ]
    if invoke_form:
        group_dict = f" /Group {group}" if group else ""
        objects[4] = (
            5,
            stream_object(
                "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 160 120] "
                f"/Resources << /ExtGState << /Normal 9 0 R >> >>{group_dict}",
                form_content,
            ),
        )

    return pdf_bytes(objects)


def fixtures() -> list[tuple[str, bytes]]:
    background = p7_background()
    return [
        (
            "altona-p7-direct-devicecmyk-blend-probe.pdf",
            p7_probe_pdf(background + p7_blend_shapes()),
        ),
        (
            "altona-p7-form-invocation-hardlight-probe.pdf",
            p7_probe_pdf(background + "/BMHard gs /Fm1 Do\n", form_content=p7_form_shapes()),
        ),
        (
            "altona-p7-rgb-transparency-group-hardlight-probe.pdf",
            p7_probe_pdf(
                background + "/BMHard gs /Fm1 Do\n",
                form_content=p7_form_shapes(),
                group="<< /S /Transparency /CS /DeviceRGB /I false /K false >>",
            ),
        ),
        (
            "altona-p7-cmyk-transparency-group-hardlight-probe.pdf",
            p7_probe_pdf(
                background + "/BMHard gs /Fm1 Do\n",
                form_content=p7_form_shapes(),
                group="<< /S /Transparency /CS /DeviceCMYK /I false /K false >>",
            ),
        ),
        (
            "altona-p7-isolated-cmyk-group-pattern-text-probe.pdf",
            p7_pattern_text_group_pdf(),
        ),
    ]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", default=str(DEFAULT_OUTPUT))
    args = parser.parse_args()

    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    for name, data in fixtures():
        path = output_dir / name
        path.write_bytes(data)
        print(f"wrote {path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

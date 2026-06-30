#!/usr/bin/env python3
"""Generate tiny PDF 2.0 image/filter fixtures for normative coverage.

The fixtures are deliberately minimal. They exist to give the image/filter
inventory concrete corpus evidence for features that are otherwise only covered
by unit tests or optional external corpora.
"""

from __future__ import annotations

import argparse
from pathlib import Path


DEFAULT_OUTPUT = Path("test-pdfs/pdf20")


def pdf_bytes(objects: list[tuple[int, str]]) -> bytes:
    body = bytearray(b"%PDF-2.0\n%\xE2\xE3\xCF\xD3\n")
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
    body.extend(f"trailer << /Size {max_object + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode("ascii"))
    return bytes(body)


def stream_object(dictionary: str, data: str) -> str:
    encoded = data.encode("latin-1")
    return f"<< {dictionary} /Length {len(encoded)} >>\nstream\n{data}\nendstream\n"


def fixture(
    name: str,
    image_dictionary: str,
    image_data: str,
    *,
    resources_extra: str = "",
    extra_objects: list[tuple[int, str]] | None = None,
) -> tuple[str, bytes]:
    content = "q 20 0 0 20 36 36 cm /Im1 Do Q\n"
    resources = f"/XObject << /Im1 5 0 R >> {resources_extra}".strip()
    objects = [
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        (3, f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 96 96] /Resources << {resources} >> /Contents 4 0 R >>"),
        (4, stream_object("", content)),
        (5, stream_object(f"/Type /XObject /Subtype /Image {image_dictionary}", image_data)),
    ]
    if extra_objects:
        objects.extend(extra_objects)
    return name, pdf_bytes(objects)


def fixtures() -> list[tuple[str, bytes]]:
    tint_transform = "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>"
    devicen_transform = "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>"
    dct_app14 = "\xff\xee\x00\x0eAdobe\x00\x00\x00\x00\x00\x02"
    return [
        fixture(
            "asciihex-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode",
            "FF0000>",
        ),
        fixture(
            "ascii85-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCII85Decode",
            "z~>",
        ),
        fixture(
            "lzw-earlychange-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /LZWDecode /DecodeParms << /EarlyChange 0 >>",
            "\x80\x0b\x60\x50\x22\x0c\x0c\x85\x01",
        ),
        fixture(
            "flate-predictor-tiff-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Predictor 2 /Colors 1 /Columns 1 /BitsPerComponent 8 >>",
            "\x78\x9c\x63\x00\x00\x00\x01\x00\x01",
        ),
        fixture(
            "flate-predictor-png-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors 1 /Columns 1 /BitsPerComponent 8 >>",
            "\x78\x9c\x63\x00\x00\x00\x01\x00\x01",
        ),
        fixture(
            "runlength-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /RunLengthDecode",
            "\x00\x7f\x80",
        ),
        fixture(
            "ccitt-image.pdf",
            "/Width 8 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns 8 /BlackIs1 true >>",
            "\x00",
        ),
        fixture(
            "dct-colotransform-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /DecodeParms << /ColorTransform 1 >>",
            dct_app14,
        ),
        fixture(
            "jpx-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode",
            "\xff\x4f\xff\x51",
        ),
        fixture(
            "jbig2-globals-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode /DecodeParms << /JBIG2Globals 6 0 R >>",
            "\x97\x4a\x42\x32\x0d\x0a\x1a\x0a",
            extra_objects=[(6, stream_object("", ""))],
        ),
        fixture(
            "crypt-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /Crypt",
            "\x00",
        ),
        fixture(
            "filter-array-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter [/ASCIIHexDecode /FlateDecode]",
            "00>",
        ),
        fixture(
            "image-mask.pdf",
            "/Width 8 /Height 1 /ImageMask true /BitsPerComponent 1 /Decode [1 0]",
            "\xff",
        ),
        fixture(
            "soft-mask-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R",
            "\xff\x00\x00",
            extra_objects=[
                (
                    6,
                    stream_object(
                        "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8",
                        "\xff",
                    ),
                )
            ],
        ),
        fixture(
            "explicit-mask-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask 6 0 R",
            "\xff\x00\x00",
            extra_objects=[
                (
                    6,
                    stream_object(
                        "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true /BitsPerComponent 1 /Decode [1 0]",
                        "\x80",
                    ),
                )
            ],
        ),
        fixture(
            "color-key-mask-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask [0 0 255 255 0 0]",
            "\xff\x00\x00",
        ),
        fixture(
            "interpolate-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Interpolate true",
            "\xff\x00\x00",
        ),
        fixture(
            "devicecmyk-image.pdf",
            "/Width 1 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8",
            "\x00\xff\xff\x00",
        ),
        fixture(
            "iccbased-image.pdf",
            "/Width 1 /Height 1 /ColorSpace [/ICCBased 6 0 R] /BitsPerComponent 8",
            "\x80\x80\x80",
            extra_objects=[
                (6, stream_object("/N 3 /Alternate /DeviceRGB", ""))
            ],
        ),
        fixture(
            "indexed-image.pdf",
            "/Width 1 /Height 1 /ColorSpace [/Indexed /DeviceRGB 0 <ff0000>] /BitsPerComponent 8",
            "\x00",
        ),
        fixture(
            "lab-image.pdf",
            "/Width 1 /Height 1 /ColorSpace [/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>] /BitsPerComponent 8",
            "\x80\x80\x80",
        ),
        fixture(
            "separation-image.pdf",
            f"/Width 1 /Height 1 /ColorSpace [/Separation /Spot /DeviceCMYK {tint_transform}] /BitsPerComponent 8",
            "\xff",
        ),
        fixture(
            "devicen-image.pdf",
            f"/Width 1 /Height 1 /ColorSpace [/DeviceN [/Cyan] /DeviceCMYK {devicen_transform}] /BitsPerComponent 8",
            "\xff",
        ),
        fixture(
            "huge-image-dimensions.pdf",
            "/Width 10000 /Height 10000 /ColorSpace /DeviceGray /BitsPerComponent 8",
            "\x00",
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

# Renderer coverage and PDF 2.0 conformance

How the renderer is validated, and where conformance stands.

The Skia renderer has been smoke-tested against a real-world corpus and is validated with a MuPDF-first differential harness. When MuPDF disagrees, the test suite escalates to Poppler and Ghostscript for second and third opinions. Known divergences are issue-linked allowlist entries; new unclassified divergences fail the differential slice.

For release-quality rendering work, excise also has an exploratory all-pages
corpus scanner for the pdf.js corpus. The report separates visual fidelity
results (`PASS`, `PASS_ONE`, `DIFF`) from semantic release-gate results
(`resultStatus`, `resultCategory`, and `resultReason`) and bounded non-fidelity
classifications such as malformed PDFs, unsupported encryption/compression,
decode failures, invalid page geometry, render resource limits, oracle refusal,
and timeouts. Expectation manifests keep raw scanner status stable while
allowing reviewed page-box, color-management, reference-refusal, and degenerate
fixture cases to be counted separately from real content-loss bugs. This keeps
quick-win rendering work focused on shared root causes rather than per-file
exceptions.

| PDF type | Notes |
|---|---|
| State-issued government forms (CT birth-cert, DS-82) | TJ kerning, Tw column alignment, raster backgrounds |
| SCOTUS opinions | Non-uniform Tm, Type1 PostScript subsets |
| IRS Form 1040 + Instructions | Type0/Identity-H, Acrobat-distilled, 180° footers |
| CDC VIS | Embedded TrueType, Wingdings dingbats |
| Pragmatic Bookshelf books (XEP) | 455-page multi-font CFF subsets, ZapfDingbats |
| Multilingual CJK | zh-Hans, zh-Hant, ja, ko via Noto Serif CJK |

See [`Excise.Rendering.Tests/Visual/`](../Excise.Rendering.Tests/Visual) and [`Excise.App.Tests/UI/baselines/`](../Excise.App.Tests/UI/baselines) for the regression baselines.

## PDF 2.0 conformance
All 15 conformance phases shipped:

| Phase | Feature |
|---|---|
| 3 | Standard 14 fonts via embedded core metrics |
| 4 | Image XObjects (DCT/Flate/CCITTFax) |
| 5 | Composite (Type0/CID) fonts with Identity-H/V |
| 6 | Inline images (BI/ID/EI) |
| 7 | Color spaces (DeviceRGB/CMYK/Gray, ICCBased, Indexed, CalRGB) |
| 8 | Embedded TrueType + raw-CFF/Type1C |
| 9 | CFF parser + MVP subsetter |
| 10 | Annotations (Text, Link, Highlight, Underline, StrikeOut, Squiggly, Stamp, Ink, Widget) |
| 11 | AcroForms — read, fill, flatten, author, auto-detect |
| 12 | Optional Content Groups (OCGs) + structure tree (read-only, redaction-aware) |
| 13 | Document-level embedded files / portfolios — read + scrub |
| 14 | Page labels (`/PageLabels`) and named destinations |
| 15 | Conformance harness — corpus parse + render + round-trip + redaction regression |

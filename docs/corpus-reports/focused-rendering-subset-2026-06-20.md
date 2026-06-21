# Focused Rendering Subset - 2026-06-20

Source report: `Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report-test-pdfs-all.json`
Corpus: `test-pdfs`
Source generated UTC: `2026-06-20T12:24:33.5126480Z`
Page manifest: `test-pdfs/manifests/rendering-open-issues-2026-06-20.tsv`

## How To Rerun

```bash
dotnet build -c Debug Pdfe.Cli/Pdfe.Cli.csproj
PDFE_PDFBOX_JAR=/private/tmp/pdfe-tools/pdfbox-app-3.0.7.jar Pdfe.Cli/bin/Debug/net10.0/pdfe corpus-scan test-pdfs --page-manifest test-pdfs/manifests/rendering-open-issues-2026-06-20.tsv --password-manifest test-pdfs/manifests/rendering-known-passwords-2026-06-20.tsv --output Pdfe.Rendering.Tests/bin/Debug/net10.0/focused-rendering-open-issues-2026-06-20.json --page-mode first --extra-oracles all --parallel 1 --pdf-timeout-ms 120000
```

`--page-manifest` overrides `--page-mode` for listed PDFs. Page `0` means an open-time failure; if a future parser fix opens that file successfully, the focused scan renders page 1 so the case can move to a normal rendering status.

## Counts

| Status | Entries |
|---|---:|
| `DIFF` | 20 |
| `INVALID_PAGE_GEOMETRY` | 2 |
| `MALFORMED_PDF` | 30 |
| `PASSWORD_REQUIRED` | 5 |
| `PASS_ONE` | 109 |
| `RESOURCE_LIMIT` | 2 |
| `TIMEOUT` | 2 |

## Repeated Files

| Entries | PDF |
|---:|---|
| 25 | `pdfjs/Brotli-Prototype-FileA.pdf` |
| 10 | `pdfjs/issue11878_reduced.pdf` |
| 6 | `pdfjs/calrgb.pdf` |
| 2 | `pdfjs/boundingBox_invalid.pdf` |

## Status Guidance

- `DIFF`: no compared reference renderer matched pdfe within thresholds. These are the main rendering-fidelity targets.
- `PASS_ONE`: at least one reference renderer matched pdfe. Treat these as lower priority unless a visual inspection shows pdfe is the outlier.
- `MALFORMED_PDF`: open-time parser/resilience failures. Some are intentionally invalid corpus files; fixes should be spec-grounded recovery, not format guesswork.
- `TIMEOUT`: the timed-out phase identifies whether pdfe or a reference renderer consumed the budget. Reference timeouts are not automatically pdfe defects.
- `INVALID_PAGE_GEOMETRY`: page boxes resolve to invalid bitmap dimensions. Decide whether to skip, clamp, or recover based on PDF box semantics.
- `PASSWORD_REQUIRED`: encrypted files that require a non-empty user password. They belong in a separate password-aware lane, but are included here so the focused subset tracks them.
- `RESOURCE_LIMIT`: pages whose resolved size would exceed the renderer pixel budget. Treat these as memory-safety outcomes unless we intentionally add lower-DPI fallback behavior.

## Entries

| Status | PDF | Page | Summary |
|---|---|---:|---|
| `DIFF` | `pdfjs/S2.pdf` | 1 | No compared oracle is within threshold; best=pdftocairo, diff=0.138292, mae=15.9755, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/bug1108301.pdf` | 1 | No compared oracle is within threshold; best=mutool, diff=0.185179, mae=40.8761, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/bug1252420.pdf` | 1 | No compared oracle is within threshold; best=pdftocairo, diff=0.111306, mae=23.6821, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/bug1308536.pdf` | 1 | No compared oracle is within threshold; best=pdfbox, diff=0.123555, mae=24.6145, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/bug1755507.pdf` | 1 | No compared oracle is within threshold; best=mutool, diff=0.183075, mae=10.4692, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/bug1799927.pdf` | 1 | No compared oracle is within threshold; best=ghostscript, diff=0.114335, mae=25.5696, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/bug1815476.pdf` | 1 | No compared oracle is within threshold; best=mutool, diff=0.891917, mae=224.419, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/bug920426.pdf` | 1 | No compared oracle is within threshold; best=mutool, diff=0.110814, mae=24.7562, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/chrome-text-selection-markedContent.pdf` | 1 | No compared oracle is within threshold; best=mutool, diff=0.100382, mae=17.9843, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/issue10519_reduced.pdf` | 1 | No compared oracle is within threshold; best=mutool, diff=0.146935, mae=36.3366, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/issue12798_page1_reduced.pdf` | 1 | No compared oracle is within threshold; best=pdftocairo, diff=0.121893, mae=4.28179, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/issue13372.pdf` | 1 | No compared oracle is within threshold; best=pdftocairo, diff=0.297134, mae=23.0003, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/issue13520.pdf` | 1 | No compared oracle is within threshold; best=pdfbox, diff=0.473478, mae=87.5725, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/issue16038.pdf` | 1 | No compared oracle is within threshold; best=mutool, diff=0.183804, mae=25.9308, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/issue16316.pdf` | 1 | No compared oracle is within threshold; best=mutool, diff=0.1036, mae=17.3508, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/issue17065.pdf` | 1 | No compared oracle is within threshold; best=pdftocairo, diff=0.276579, mae=21.1754, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/issue19326.pdf` | 1 | No compared oracle is within threshold; best=pdfbox, diff=0, mae=62.631, compared=4, agreeing=0 |
| `DIFF` | `pdfjs/issue1985.pdf` | 1 | No compared oracle is within threshold; best=pdftocairo, diff=0.985173, mae=250.19, compared=4, agreeing=0 |
| `DIFF` | `poppler/tests/jpeg.pdf` | 1 | No compared oracle is within threshold; best=pdfbox, diff=0.134896, mae=19.9183, compared=4, agreeing=0 |
| `DIFF` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.2 Graphics/6.2.4 Colour spaces/6.2.4.3 Uncalibrated -Device colour spaces/veraPDF test suite 6-2-4-3-t04-fail-r.pdf` | 1 | No compared oracle is within threshold; best=pdftocairo, diff=0.147148, mae=9.52354, compared=4, agreeing=0 |
| `INVALID_PAGE_GEOMETRY` | `pdfjs/boundingBox_invalid.pdf` | 1 | Invalid resolved page bitmap size; Page resolves to an invalid bitmap size: 0 x 0 pixels. |
| `INVALID_PAGE_GEOMETRY` | `pdfjs/boundingBox_invalid.pdf` | 2 | Invalid resolved page bitmap size; Page resolves to an invalid bitmap size: 0 x 0 pixels. |
| `MALFORMED_PDF` | `isartor/Isartor testsuite/PDFA-1b/6.1 File structure/6.1.2 File header/isartor-6-1-2-t01-fail-a.pdf` | 0 | PdfParseException during open; Invalid PDF header |
| `MALFORMED_PDF` | `pdfjs/GHOSTSCRIPT-698804-1-fuzzed.pdf` | 0 | OverflowException during open; Value was either too large or too small for an Int32. |
| `MALFORMED_PDF` | `pdfjs/PDFBOX-3148-2-fuzzed.pdf` | 0 | PdfParseException during open; Invalid character in ASCII85: ± |
| `MALFORMED_PDF` | `pdfjs/PDFBOX-4352-0.pdf` | 0 | PdfParseException during open; Unexpected keyword 'E' at position 557 |
| `MALFORMED_PDF` | `pdfjs/REDHAT-1531897-0.pdf` | 0 | OverflowException during open; Value was either too large or too small for an Int64. |
| `MALFORMED_PDF` | `pdfjs/bug1020226.pdf` | 0 | PdfParseException during open; Could not find startxref |
| `MALFORMED_PDF` | `pdfjs/bug1250079.pdf` | 0 | PdfParseException during open; Could not find startxref |
| `MALFORMED_PDF` | `pdfjs/bug1606566.pdf` | 0 | PdfParseException during open; Invalid PDF header |
| `MALFORMED_PDF` | `pdfjs/bug1795263.pdf` | 0 | PdfParseException during open; Invalid seek offset 451088 (stream length 355886) |
| `MALFORMED_PDF` | `pdfjs/bug1978317.pdf` | 0 | PdfParseException during open; Document has no Pages dictionary |
| `MALFORMED_PDF` | `pdfjs/bug1980958.pdf` | 0 | PdfParseException during open; Could not find startxref |
| `MALFORMED_PDF` | `pdfjs/close-path-bug.pdf` | 0 | PdfParseException during open; Invalid seek offset 1556 (stream length 1451) |
| `MALFORMED_PDF` | `pdfjs/empty_protected.pdf` | 0 | PdfParseException during open; /U must be exactly 48 bytes; got 127 |
| `MALFORMED_PDF` | `pdfjs/encrypted-attachment.pdf` | 0 | PdfParseException during open; Expected 'obj', got 'f' at position 2250 |
| `MALFORMED_PDF` | `pdfjs/helloworld-bad.pdf` | 0 | PdfParseException during open; Expected generation number, got Keyword at position 495 |
| `MALFORMED_PDF` | `pdfjs/issue10438_reduced.pdf` | 0 | PdfParseException during open; Expected 'xref' or xref stream at position 491, got dstream |
| `MALFORMED_PDF` | `pdfjs/issue15590.pdf` | 0 | PdfParseException during open; Could not find startxref |
| `MALFORMED_PDF` | `pdfjs/issue15893_reduced.pdf` | 0 | PdfParseException during open; Expected generation number, got Name at position 610 |
| `MALFORMED_PDF` | `pdfjs/issue17147.pdf` | 0 | PdfParseException during open; Expected 'xref' or xref stream at position 45022, got << |
| `MALFORMED_PDF` | `pdfjs/issue17554.pdf` | 0 | PdfParseException during open; Expected 'obj', got 'n' at position 497 |
| `MALFORMED_PDF` | `pdfjs/issue18986.pdf` | 0 | PdfParseException during open; Unexpected character '' (0x02) at position 819 |
| `MALFORMED_PDF` | `pdfjs/issue19484_1.pdf` | 0 | PdfParseException during open; Could not decode Flate stream |
| `MALFORMED_PDF` | `pdfjs/issue19484_2.pdf` | 0 | PdfParseException during open; Could not decode Flate stream |
| `MALFORMED_PDF` | `pdfjs/issue19800.pdf` | 0 | PdfParseException during open; Could not find startxref |
| `MALFORMED_PDF` | `poppler/unittestcases/PasswordEncryptedReconstructed.pdf` | 0 | PdfParseException during open; Expected generation number, got Name at position 610 |
| `MALFORMED_PDF` | `poppler/unittestcases/type3.pdf` | 0 | PdfParseException during open; Unexpected character 'ð' (0xF0) at position 223429 |
| `MALFORMED_PDF` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-1b/6.1 File structure/6.1.2 File header/veraPDF test suite 6-1-2-t01-fail-a.pdf` | 0 | PdfParseException during open; Invalid PDF header |
| `MALFORMED_PDF` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.1 File structure/6.1.2 File header/veraPDF test suite 6-1-2-t01-fail-b.pdf` | 0 | PdfParseException during open; Invalid PDF header |
| `MALFORMED_PDF` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.1 File structure/6.1.2 File header/veraPDF test suite 6-1-2-t01-fail-b.pdf` | 0 | PdfParseException during open; Invalid PDF header |
| `MALFORMED_PDF` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.1 File structure/6.1.3 File trailer/veraPDF test suite 6-1-3-t02-fail-a.pdf` | 0 | PdfParseException during open; /U must be exactly 48 bytes; got 127 |
| `PASSWORD_REQUIRED` | `pdfjs/bug1782186.pdf` | 0 | Encrypted PDF requires a non-empty user password in the no-password baseline; Password verification failed. The file requires a non-empty user password, which pdfe doesn't yet prompt for. Pass allowEncrypted: true to inspect the encryption dict, or open t... |
| `PASSWORD_REQUIRED` | `pdfjs/issue3371.pdf` | 0 | Encrypted PDF requires a non-empty user password in the no-password baseline; Password verification failed. The file requires a non-empty user password, which pdfe doesn't yet prompt for. Pass allowEncrypted: true to inspect the encryption dict, or open t... |
| `PASSWORD_REQUIRED` | `poppler/unittestcases/Gday garçon - open.pdf` | 0 | Encrypted PDF requires a non-empty user password in the no-password baseline; Password verification failed. The file requires a non-empty user password, which pdfe doesn't yet prompt for. Pass allowEncrypted: true to inspect the encryption dict, or open t... |
| `PASSWORD_REQUIRED` | `poppler/unittestcases/PasswordEncrypted.pdf` | 0 | Encrypted PDF requires a non-empty user password in the no-password baseline; Password verification failed. The file requires a non-empty user password, which pdfe doesn't yet prompt for. Pass allowEncrypted: true to inspect the encryption dict, or open t... |
| `PASSWORD_REQUIRED` | `poppler/unittestcases/encrypted-256.pdf` | 0 | Encrypted PDF requires a non-empty user password in the no-password baseline; AES-256 (V=5 R=6) password verification failed for the empty user password. Owner-password-only files and files requiring a non-empty user password are not yet supported (#324). |
| `PASS_ONE` | `isartor/Isartor testsuite/PDFA-1b/6.2 Graphics/6.2.10 Content Streams/isartor-6-2-10-t01-fail-a.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0, compared=3, agreeing=3 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0109236, mae=2.72984, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 2 | Partial oracle agreement; best=mutool, diff=0.0082208, mae=1.8403, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 3 | Partial oracle agreement; best=mutool, diff=0.0210346, mae=3.90781, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 4 | Partial oracle agreement; best=mutool, diff=0.00413714, mae=1.29734, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 5 | Partial oracle agreement; best=mutool, diff=0.00319073, mae=1.14488, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 6 | Partial oracle agreement; best=mutool, diff=0.00305597, mae=1.25556, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 7 | Partial oracle agreement; best=mutool, diff=0.00297445, mae=1.10764, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 8 | Partial oracle agreement; best=mutool, diff=0.00247487, mae=1.00014, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 9 | Partial oracle agreement; best=mutool, diff=0.00302579, mae=1.25975, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 10 | Partial oracle agreement; best=mutool, diff=0.00214118, mae=0.884142, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 11 | Partial oracle agreement; best=mutool, diff=0.00627689, mae=1.95429, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 12 | Partial oracle agreement; best=mutool, diff=0.0037022, mae=1.66009, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 13 | Partial oracle agreement; best=mutool, diff=0.00263316, mae=1.02038, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 14 | Partial oracle agreement; best=mutool, diff=0.00235413, mae=1.41056, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 15 | Partial oracle agreement; best=mutool, diff=0.00247178, mae=0.976186, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 16 | Partial oracle agreement; best=mutool, diff=0.00308996, mae=1.32603, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 17 | Partial oracle agreement; best=mutool, diff=0.00404611, mae=1.4049, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 18 | Partial oracle agreement; best=mutool, diff=0.0028126, mae=1.38955, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 19 | Partial oracle agreement; best=mutool, diff=0.00305264, mae=1.07126, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 20 | Partial oracle agreement; best=mutool, diff=0.00315152, mae=1.07348, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 21 | Partial oracle agreement; best=mutool, diff=0.00793939, mae=2.29874, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 22 | Partial oracle agreement; best=mutool, diff=0.0075893, mae=2.0597, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 23 | Partial oracle agreement; best=mutool, diff=0.00770315, mae=2.17717, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 24 | Partial oracle agreement; best=mutool, diff=0.00887035, mae=2.30062, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/Brotli-Prototype-FileA.pdf` | 25 | Partial oracle agreement; best=mutool, diff=0.0109704, mae=2.19159, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/ContentStreamCycleType3insideType3.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0129733, mae=1.70244, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/auth-event-ef-open.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.000295663, mae=0.0452169, compared=1, agreeing=1 |
| `PASS_ONE` | `pdfjs/bitmap-composite-and-xnor-refine.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.0036908, mae=0.521691, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-composite-or-xor-replace-refine.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-halftone-refine.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-refine-customat-tpgron.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-refine-customat.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-refine-lossless.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-refine-refine.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-refine-template1-tpgron.pdf` | 1 | Partial oracle agreement; best=ghostscript, diff=0.0694502, mae=17.4394, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/bitmap-refine-template1.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-refine-tpgron.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=2 |
| `PASS_ONE` | `pdfjs/bitmap-refine.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-symbol-context-reuse.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0112266, mae=2.68294, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/bitmap-symbol-symhuffrefine-textrefine.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.00382194, mae=0.553927, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-symbol-symhuffrefineseveral.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-symbol-texthuffrefine.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.0036908, mae=0.521691, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-symbol-texthuffrefineB15.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.0036908, mae=0.521691, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-symbol-texthuffrefinecustomdims.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.0036908, mae=0.521691, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-symbol-texthuffrefinecustompos.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.0036908, mae=0.521691, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-symbol-texthuffrefinecustomsize.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.0036908, mae=0.521691, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bitmap-trailing-7fff-stripped-harder-refine.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.00343386, mae=0.573393, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/bug1552113.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.0119733, mae=1.76292, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/bug1802506.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0676111, mae=16.8423, compared=4, agreeing=2 |
| `PASS_ONE` | `pdfjs/bug1922766.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0054902, mae=1.09935, compared=4, agreeing=2 |
| `PASS_ONE` | `pdfjs/bug_jpx.pdf` | 1 | Partial oracle agreement; best=ghostscript, diff=0.000316578, mae=0.0319539, compared=2, agreeing=2 |
| `PASS_ONE` | `pdfjs/calrgb.pdf` | 6 | Partial oracle agreement; best=mutool, diff=0.0816906, mae=7.34368, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/calrgb.pdf` | 8 | Partial oracle agreement; best=mutool, diff=0.0297697, mae=3.69829, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/calrgb.pdf` | 9 | Partial oracle agreement; best=mutool, diff=0.0816704, mae=7.34074, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/calrgb.pdf` | 10 | Partial oracle agreement; best=mutool, diff=0.0297495, mae=3.69535, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/calrgb.pdf` | 16 | Partial oracle agreement; best=mutool, diff=0.0399504, mae=8.61999, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/calrgb.pdf` | 17 | Partial oracle agreement; best=pdftocairo, diff=0.0753737, mae=17.7102, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/colorspace_atan.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.544269, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/colorspace_cos.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.615516, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/colorspace_sin.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.615516, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/copy_paste_ligatures.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.094519, mae=15.4367, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/endchar.pdf` | 1 | Partial oracle agreement; best=ghostscript, diff=0.000301367, mae=0.0615457, compared=4, agreeing=2 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 2 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 3 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 4 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 5 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 6 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 7 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 8 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 9 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue11878_reduced.pdf` | 10 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.163265, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue12418_reduced.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.0262574, mae=4.432, compared=4, agreeing=2 |
| `PASS_ONE` | `pdfjs/issue13916.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0520425, mae=10.205, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue15977_reduced.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.0352718, mae=6.43142, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue17871_top_right.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.067129, mae=17.1179, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/issue18529.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.00455182, mae=1.01221, compared=4, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue19517.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.000203915, mae=26.6319, compared=4, agreeing=2; Requested 150 DPI exceeded the render pixel cap; compared at 28 DPI. |
| `PASS_ONE` | `pdfjs/issue21068.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0151675, mae=2.42148, compared=4, agreeing=2 |
| `PASS_ONE` | `pdfjs/issue21436.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.176854, compared=3, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue2177.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0739631, mae=8.06424, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/issue2391-1.pdf` | 1 | Partial oracle agreement; best=ghostscript, diff=0.000100232, mae=0.0381428, compared=3, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue2884_reduced.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0373357, mae=7.87071, compared=4, agreeing=1 |
| `PASS_ONE` | `pdfjs/issue3694_reduced.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0408391, mae=6.72916, compared=4, agreeing=2 |
| `PASS_ONE` | `pdfjs/issue4402_reduced.pdf` | 1 | Partial oracle agreement; best=ghostscript, diff=0.0713158, mae=11.2153, compared=4, agreeing=2 |
| `PASS_ONE` | `pdfjs/issue4575.pdf` | 1 | Partial oracle agreement; best=pdfbox, diff=0.0128282, mae=2.19406, compared=3, agreeing=3 |
| `PASS_ONE` | `pdfjs/issue4722.pdf` | 1 | Partial oracle agreement; best=ghostscript, diff=0.0664776, mae=13.1033, compared=4, agreeing=3 |
| `PASS_ONE` | `poppler/tests/mask.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.0783696, mae=13.1211, compared=1, agreeing=1 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-1b/6.1 File structure/6.1.12 Implementation limits/veraPDF test suite 6-1-12-t02-pass-k.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.000341695, mae=0.0512819, compared=3, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.1 File structure/6.1.13 Implementation limits/veraPDF test suite 6-1-13-t09-fail-e.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0, mae=0, compared=2, agreeing=2 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.2 Graphics/6.2.10 Transparency/veraPDF test suite 6-2-10-t06-fail-b.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0413064, mae=7.98598, compared=4, agreeing=2 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.2 Graphics/6.2.10 Transparency/veraPDF test suite 6-2-10-t06-fail-c.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0903253, mae=15.7602, compared=4, agreeing=1 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.2 Graphics/6.2.2 Content streams/veraPDF test suite 6-2-2-t01-fail-a.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.00912355, compared=3, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.2 Graphics/6.2.2 Content streams/veraPDF test suite 6-2-2-t01-fail-b.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.00835904, compared=3, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.2 Graphics/6.2.4.3 Uncalibrated -Device colour spaces/veraPDF test suite 6-2-4-3-t02-pass-d.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=5.37469, compared=4, agreeing=1 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.3 Annotations/6.3.3 Annotation appearances/veraPDF test suite 6-3-3-t01-fail-m.pdf` | 1 | Partial oracle agreement; best=ghostscript, diff=0, mae=0, compared=4, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.1 File structure/6.1.6 Stream objects/6.1.6.2 Filters/veraPDF test suite 6-1-6-2-t01-fail-b.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0, compared=3, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.2 Graphics/6.2.2 Content streams/veraPDF test suite 6-2-2-t01-fail-a.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.00912355, compared=3, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.2 Graphics/6.2.2 Content streams/veraPDF test suite 6-2-2-t01-fail-b.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.00835904, compared=3, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.2 Graphics/6.2.2 Content streams/veraPDF test suite 6-2-2-t03-fail-a.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0.00955342, mae=1.69975, compared=3, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.2 Graphics/6.2.2 Content streams/veraPDF test suite 6-2-2-t03-fail-b.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0, compared=3, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.2 Graphics/6.2.4 Colour spaces/6.2.4.3 Uncalibrated -Device colour spaces/veraPDF test suite 6-2-4-3-t02-pass-d.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=5.37469, compared=4, agreeing=1 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.2 Graphics/6.2.9 Transparency/veraPDF test suite 6-2-9-t06-fail-b.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0413064, mae=7.98598, compared=4, agreeing=2 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.2 Graphics/6.2.9 Transparency/veraPDF test suite 6-2-9-t06-fail-c.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0903253, mae=15.7602, compared=4, agreeing=1 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.3 Annotations/6.3.3 Annotation appearances/veraPDF test suite 6-3-3-t01-fail-m.pdf` | 1 | Partial oracle agreement; best=ghostscript, diff=0, mae=0, compared=4, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/PDF_UA-1/7.20 XObjects/7.20-t02-fail-a.pdf` | 1 | Partial oracle agreement; best=mutool, diff=0.0197831, mae=2.83464, compared=4, agreeing=3 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/TWG test files/TWG test suite A019-pdfa2-pass-b.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0, compared=4, agreeing=2 |
| `PASS_ONE` | `verapdf-corpus/veraPDF-corpus-master/Undefined/veraPDF test suite 6-2-3-2-t01-undefined-a.pdf` | 1 | Partial oracle agreement; best=pdftocairo, diff=0, mae=0.0134744, compared=3, agreeing=3 |
| `RESOURCE_LIMIT` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.1 File structure/6.1.13 Implementation limits/veraPDF test suite 6-1-13-t09-fail-b.pdf` | 1 | Renderer refused an oversized page allocation; Page render would allocate 29811 x 29811 pixels (888,695,721), exceeding the configured limit of 268,435,456 pixels. |
| `RESOURCE_LIMIT` | `verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.1 File structure/6.1.13 Implementation limits/veraPDF test suite 6-1-13-t09-fail-f.pdf` | 1 | Renderer refused an oversized page allocation; Page render would allocate 29805 x 29805 pixels (888,338,025), exceeding the configured limit of 268,435,456 pixels. |
| `TIMEOUT` | `isartor/Isartor testsuite/PDFA-1b/6.1 File structure/6.1.12 Implementation Limits/isartor-6-1-12-t01-fail-a.pdf` | 1172 | Timed out during mutool; Per-PDF budget 240000ms exceeded during mutool: isartor/Isartor testsuite/PDFA-1b/6.1 File structure/6.1.12 Implementation Limits/isartor-6-1-12-t01-fail-a.pdf: mutool render pa... |
| `TIMEOUT` | `pdfjs/freeculture.pdf` | 129 | Timed out during pdfbox; Per-PDF budget 240000ms exceeded during pdfbox: pdfjs/freeculture.pdf: pdfbox render page 129/352 |

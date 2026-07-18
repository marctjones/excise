# Full Corpus Rendering Quality Snapshot - 2026-06-26

Source raw report:
`Excise.Rendering.Tests/bin/Debug/net10.0/exploratory-report-test-pdfs-all-20260622-passone-sharded-coverage.json`

Classification command:

```bash
dotnet run --project Excise.Cli/Excise.Cli.csproj -- \
  render-quality-classify \
  Excise.Rendering.Tests/bin/Debug/net10.0/exploratory-report-test-pdfs-all-20260622-passone-sharded-coverage.json \
  --contracts test-pdfs/rendering-contracts \
  --output /tmp/excise-full-corpus-quality.json \
  --strict-contracts
```

The full corpus now has a per-PDF JSON rendering contract for every PDF in the
raw all-pages report. Older focused contracts remain reviewed inputs; newly
covered pages are baseline classifications inferred from the raw all-pages scan
and should be promoted to reviewed contracts when triaged.

## Coverage

| Metric | Count |
|---|---:|
| PDFs classified | 3,685 |
| Pages classified | 14,936 |
| Contract files | 3,685 |
| Missing contracts | 0 |
| Release `PASS` pages | 14,934 |
| Release `BLOCKED` pages | 2 |

## PDF-Level Summary

| Question | PDFs |
|---|---:|
| All pages pixel-exact against the required reference set | 3,569 |
| Reference renderers disagree on at least one page | 80 |
| excise matches exactly one reference renderer on at least one page | 19 |
| excise is classified higher quality than the references on at least one page | 4 |
| excise has a rendering failure or raw `DIFF` on at least one page | 2 |
| excise is lower quality but acceptable on at least one page | 41 |

## Page-Level Quality

| Quality status | Pages |
|---|---:|
| `PIXEL_EXACT` | 14,503 |
| `MATCHES_ACCEPTED_REFERENCE` | 330 |
| `GOOD_ENOUGH` | 46 |
| `EXCISE_BETTER_THAN_REFS` | 28 |
| `NON_RENDERABLE_ACCEPTED` | 26 |
| `ACCEPTED_LIMITATION` | 1 |
| `FAIL` | 2 |

## Page-Level Pixel Agreement

| Pixel agreement | Pages |
|---|---:|
| `MATCHES_ALL_REQUIRED` | 14,517 |
| `MATCHES_SOME` | 338 |
| `MATCHES_ONE_REFERENCE` | 24 |
| `MATCHES_NONE` | 3 |
| `NOT_COMPARABLE` | 54 |

## Page-Level Reference Situation

| Reference situation | Pages |
|---|---:|
| `REFS_AGREE` | 14,517 |
| `REFS_DISAGREE` | 363 |
| `REFS_INCOMPLETE` | 28 |
| `NOT_APPLICABLE` | 28 |

## Blocking Pages

| PDF | Page | Raw status | Root cause |
|---|---:|---|---|
| `pdfjs/bug859204.pdf` | 1 | `DIFF` | `UNCLASSIFIED_RAW_DIFF` |
| `pdfjs/issue14999_reduced.pdf` | 1 | `DIFF` | `UNCLASSIFIED_RAW_DIFF` |

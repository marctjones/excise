# PDF 2.0 Conformance Validation Harness

## Overview

The conformance validation harness tests pdfe against industry-standard PDF test corpora to verify:

1. **Parsing robustness**: Every PDF in the corpus can be opened without exception
2. **Rendering viability**: Pages render to valid bitmaps without crashing
3. **Round-trip integrity**: Load → save → reload preserves document structure and content
4. **Redaction security**: Glyph-level redaction actually removes content, not just visual masking

This is the integration layer that proves the previous 14 phases (PDF parsing, rendering, redaction, etc.) work end-to-end against real-world PDFs.

## Test Classes

### 1. ConformanceTests.cs

**Purpose**: Verify all PDFs in the corpus parse and render without crashing.

**Test Methods**:
- `ConformanceTests_SmokeCorpus_ParsesAndRendersWithoutCrashing` — 8 real-world US government PDFs
- `ConformanceTests_VeraPdfCorpus_ParsesAndRendersWithoutCrashing` — 2,694 ISO 32000-1/2 and PDF/A test files

**Coverage**:
- Opens each PDF with `PdfDocument.Open()`
- Verifies page count > 0
- Renders first 3 pages (or all pages if ≤ 10) at 72 DPI using SkiaRenderer
- Asserts rendering completes and returns a bitmap. Visual completeness belongs to the differential rendering tests, not this conformance harness.

**Smoke Corpus** (8 files, ~10 seconds):
```
✓ cdc-vis-covid-19.pdf
✓ irs-1040-instructions.pdf (4.3 MB)
✓ irs-1040.pdf
✓ irs-pub509-2026.pdf
✓ irs-w9.pdf
✓ scotus-trump-v-anderson.pdf
✓ scotus-trump-v-us.pdf
✓ state-ds82-passport-renewal.pdf
```

**veraPDF Corpus** (2,694 files, ~20-30 minutes):
- PDF/A-1a/1b, PDF/A-2a/2b/2u, PDF/A-3b, PDF/A-4/4e/4f
- PDF/UA-1, PDF/UA-2
- ISO 32000-1 and ISO 32000-2 test suites
- Isartor PDF/A-1 conformance tests

**Environment Variables**:
- `SKIP_LARGE_CORPUS=1` — Skip veraPDF corpus (only run smoke tests)
- `SKIP_LARGE_CORPUS=0` — Force run veraPDF corpus (default)

### 2. RoundTripTests.cs

**Purpose**: Verify documents survive save/reload cycle with content preserved.

**Test Method**:
- `RoundTrip_LoadSaveReload_PreservesContent` — One test per smoke corpus PDF

**Coverage**:
1. Opens original PDF, extracts text from page 1
2. Saves to temporary file via `document.Save()`
3. Reopens saved file
4. Verifies page count unchanged
5. Verifies at least 30% of original text words are still extractable

**Skips** (via `SkipTestException`):
- PDFs that cannot be opened
- PDFs with no extractable text (scanned/OCR-only)
- Encrypted/password-protected PDFs

**Why this matters**:
- Detects save-cycle bugs that corrupt document structure
- Ensures content stream rebuilding doesn't lose data
- Verifies cross-reference tables remain consistent

### 3. RedactionRegressionTests.cs

**Purpose**: SECURITY-CRITICAL — Verify redaction actually removes content.

**Test Method**:
- `RedactionRegression_ExtractedTextCanBeRemoved` — One test per smoke corpus PDF

**Coverage**:
1. Opens PDF, extracts all text from page 1
2. Finds first substantial word (>2 characters)
3. Calculates bounding box for that word's glyphs
4. Applies area redaction via `page.RedactArea(rect)`
5. Saves and reopens redacted file
6. **Asserts** the word is no longer extractable (not just visually hidden)

**Skips**:
- PDFs with no text
- PDFs with no substantial words (e.g., all numbers/punctuation)

**Why this matters**:
- Detects glyph-removal bugs (issue #95 is a real example)
- Ensures black-box visual covering isn't confused with true redaction
- Verifies parser improvements don't break content stream filtering
- **This is a hard requirement**: redaction must remove from PDF structure, not just draw black

## Running the Tests

### Smoke Corpus Only (Recommended for Development)

```bash
# Run just the 8 smoke corpus files (~10 seconds)
dotnet test Pdfe.Rendering.Tests \
  --filter "FullyQualifiedName~ConformanceTests_SmokeCorpus"

# Or specific harness
dotnet test Pdfe.Rendering.Tests \
  --filter "FullyQualifiedName~RoundTrip_LoadSaveReload"

dotnet test Pdfe.Rendering.Tests \
  --filter "FullyQualifiedName~RedactionRegression"
```

### Full Conformance Suite (Nightly)

```bash
# All three test classes, smoke corpus only
dotnet test Pdfe.Rendering.Tests \
  --filter "FullyQualifiedName~ConformanceTests or FullyQualifiedName~RoundTrip or FullyQualifiedName~RedactionRegression"

# Include veraPDF corpus (requires download)
./scripts/download-test-pdfs.sh  # Downloads ~150 MB, ~2 min
SKIP_LARGE_CORPUS=0 dotnet test Pdfe.Rendering.Tests \
  --filter "FullyQualifiedName~VeraPdfCorpus" \
  --logger "console;verbosity=minimal"
```

### Via Script

```bash
# Run all conformance tests with logging
./scripts/run-conformance.sh

# Output shape:
# Smoke corpus:       pass/fail summary
# veraPDF corpus:     pass/fail summary when downloaded and enabled
# Round-trip:         pass/fail/skip summary
# Redaction:          pass/fail/skip summary
```

## Interpreting Results

### Smoke Corpus (All 8 Should Pass)

If a smoke corpus file fails:
1. Check the error message — is it a parsing error, rendering error, or assertion failure?
2. Open the PDF in a reference viewer (Adobe Reader, etc.) to verify it's readable
3. If readable externally, file a bug against pdfe: `Issue: ConformanceTest failed for <filename>`
4. Log the PDF's characteristics (page count, fonts, content stream size)

### veraPDF Corpus

**Expected result**: pass when the corpus is downloaded and enabled.

The veraPDF corpus includes both positive fixtures and intentionally invalid negative fixtures. `*-fail-*.pdf` files are treated as expected negatives by the harness and are not counted as product regressions.

**Investigating failures**:
```bash
# Find failed tests in log
grep "FAIL:" logs/corpus_test_*.log | head -20

# Extract PDF path and test class
# Check if it's in a known-unsupported category:
find test-pdfs/verapdf-corpus -name "*encrypted*" | wc -l
find test-pdfs/verapdf-corpus -name "*XFA*" -o -name "*xfa*" | wc -l
```

### Round-Trip

**Expected**: 8/8 pass (all smoke corpus files should survive save/reload)

**If a test fails**:
- Likely a save bug (content stream rebuilding, xref table generation)
- Check the error message for specifics
- File a bug: `Issue: Round-trip test failed for <filename>`

### Redaction Regression

**Expected**: 8/8 pass (all redactions should remove glyphs)

**If a test fails** (e.g., "Redaction failed! Target word still present"):
- **CRITICAL BUG** — redaction isn't removing content
- Check the redaction area coordinates in the output
- File a security bug: `Issue (security): Redaction regression in <filename>`

## Known Limitations

### Harness Scope

1. **ConformanceTests**
   - Checks parse/render viability, not pixel fidelity.
   - Accepts intentionally invalid `*-fail-*.pdf` corpus fixtures as expected negatives.

2. **Visual correctness**
   - Covered by the MuPDF-first differential rendering tests with Poppler/Ghostscript escalation.
   - Known divergences stay issue-linked in the differential allowlist.

3. **Strict standard validation**
   - For formal PDF/A or PDF/UA validation, use the official veraPDF validator.

### Future Enhancements (v2.2+)

1. **ISO 32000-2 Chapter Breakdown**
   - Map corpus files to PDF spec chapters (8.3 Graphics, 9.2 Text, etc.)
   - Report pass rate per chapter
   - Requires annotation database linking files to spec sections
   - Would show which features are well-supported vs weak

2. **Actual veraPDF Tool Integration**
   - Run `veravalidate` on each PDF to get spec-compliant pass/fail
   - Compare against pdfe's results
   - Identify false positives (pdfe parses, veraPDF validates)
   - Requires Java + veraPDF .jar (`~/.dotnet/tools/vera-pdf/`)

3. **Extended Redaction Testing**
   - Test substring matching (not just full words)
   - Test overlapping redactions on same page
   - Test sequential redactions (verify state accumulates)
   - Test with unusual fonts (CID, Type3, embedded subsets)

4. **Corpus Analysis Dashboard**
   - Generate HTML report with:
     - Pass rate by feature (graphics, text, fonts, etc.)
     - Failure reasons by category
     - Timeline of regression/improvement
   - Host on GitHub Pages

## Environment Setup

### Smoke Corpus (8 files, 8.3 MB)

These are included in the repo:
```
test-pdfs/smoke/
├── cdc-vis-covid-19.pdf
├── irs-1040-instructions.pdf
├── irs-1040.pdf
├── irs-pub509-2026.pdf
├── irs-w9.pdf
├── scotus-trump-v-anderson.pdf
├── scotus-trump-v-us.pdf
└── state-ds82-passport-renewal.pdf
```

### veraPDF Corpus (~150 MB, 2,694 files)

Download via:
```bash
./scripts/download-test-pdfs.sh
```

This:
1. Downloads veraPDF test suite (~150 MB)
2. Downloads Isartor test suite (~4 MB)
3. Extracts to `test-pdfs/verapdf-corpus/`

Takes ~2-5 minutes depending on network speed.

### Linux Dependencies

```bash
sudo apt-get install wget unzip
```

## Troubleshooting

### "No smoke corpus found"

The test is being run from a binary directory that can't find the repo root.

**Fix**:
```bash
# Ensure you're in the repo root
cd /home/marc/Projects/pdfe

# Run tests from there
dotnet test Pdfe.Rendering.Tests
```

### "No veraPDF corpus found"

```bash
./scripts/download-test-pdfs.sh
```

### "Test hangs on a specific PDF"

Likely a malformed PDF that triggers infinite loop in parser. Set timeout:

```bash
# Kill after 30 seconds
timeout 30 dotnet test Pdfe.Rendering.Tests \
  --filter "ConformanceTests_VeraPdfCorpus"
```

If it happens consistently:
1. Note the PDF filename
2. File a bug: `Issue: Parser timeout on <filename>`
3. Add to exclusion list if needed (modify `VeraPdfCorpusFiles()`)

### "ConformanceTests pass but veraPDF tool says it's invalid"

This is expected! pdfe is a parser, not a validator. The test suite verifies:
- ✅ Can parse without exception
- ✅ Can render without crashing
- ❌ Does NOT verify ISO 32000-2 compliance

For strict compliance testing, use the official veraPDF tool (`veravalidate`).

## References

- **PDF Association**: https://www.pdfa.org/
- **veraPDF**: https://github.com/veraPDF/veraPDF-corpus
- **ISO 32000-2:2020**: PDF 2.0 specification
- **ISO 32000-1:2008**: PDF 1.7 specification
- **PDF/A**: ISO 19005 (archive format)
- **PDF/UA**: ISO 14289 (accessibility)

## See Also

- `Pdfe.Rendering.Tests/Corpus/SmokeCorpusTests.cs` — Visual rendering tests
- `Pdfe.Rendering.Tests/Corpus/EncodingDifferencesTests.cs` — Character encoding tests
- `scripts/run-conformance.sh` — Automation script
- `REDACTION_AI_GUIDELINES.md` — Redaction implementation safety guidelines

#!/bin/bash
# Analyze veraPDF corpus test results
# Primary focus: TRUE redaction verification
# Secondary: PDF format support coverage

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
LOG_DIR="$PROJECT_ROOT/logs"
REPORT_DIR="$PROJECT_ROOT/logs/reports"
CORPUS_PATH="$PROJECT_ROOT/test-pdfs/verapdf-corpus/veraPDF-corpus-master"

mkdir -p "$REPORT_DIR"

TIMESTAMP=$(date +%Y%m%d_%H%M%S)
REPORT_FILE="$REPORT_DIR/corpus_analysis_$TIMESTAMP.md"

echo "================================================="
echo "veraPDF Corpus Analysis"
echo "================================================="
echo ""

# Check corpus exists
if [ ! -d "$CORPUS_PATH" ]; then
    echo "ERROR: veraPDF corpus not found at $CORPUS_PATH"
    echo "Run: ./scripts/download-test-pdfs.sh"
    exit 1
fi

# Count PDFs
TOTAL_PDFS=$(find "$CORPUS_PATH" -name "*.pdf" | wc -l)
FAIL_PDFS=$(find "$CORPUS_PATH" -name "*fail*.pdf" | wc -l)
PASS_PDFS=$((TOTAL_PDFS - FAIL_PDFS))

echo "Corpus Statistics:"
echo "  Total PDFs: $TOTAL_PDFS"
echo "  Pass cases: $PASS_PDFS"
echo "  Fail cases: $FAIL_PDFS (intentionally non-compliant)"
echo ""

# Generate report
cat > "$REPORT_FILE" << EOF
# veraPDF Corpus Analysis Report

Generated: $(date)

## Priority Focus

1. **PRIMARY**: TRUE content redaction with verification
2. **SECONDARY**: Broad PDF format support

## Corpus Overview

| Metric | Count |
|--------|-------|
| Total PDFs | $TOTAL_PDFS |
| Valid (pass) cases | $PASS_PDFS |
| Invalid (fail) cases | $FAIL_PDFS |

## PDF Standards Coverage

EOF

# Count by category
echo "## PDF Standards in Corpus" >> "$REPORT_FILE"
echo "" >> "$REPORT_FILE"
echo "| Standard | Files | Description |" >> "$REPORT_FILE"
echo "|----------|-------|-------------|" >> "$REPORT_FILE"

for dir in "$CORPUS_PATH"/*/; do
    if [ -d "$dir" ]; then
        dirname=$(basename "$dir")
        if [ "$dirname" != "README.md" ]; then
            count=$(find "$dir" -name "*.pdf" 2>/dev/null | wc -l)
            case "$dirname" in
                "PDF_A-1a") desc="Archival Level A (structure tags)" ;;
                "PDF_A-1b") desc="Archival Level B (basic)" ;;
                "PDF_A-2a") desc="Enhanced archival Level A" ;;
                "PDF_A-2b") desc="Enhanced archival Level B" ;;
                "PDF_A-2u") desc="Enhanced archival with Unicode" ;;
                "PDF_A-3b") desc="Archival with attachments" ;;
                "PDF_A-4") desc="Latest archival (PDF 2.0)" ;;
                "PDF_A-4e") desc="Engineering documents" ;;
                "PDF_A-4f") desc="Full feature set" ;;
                "PDF_UA-1") desc="Universal Accessibility v1" ;;
                "PDF_UA-2") desc="Universal Accessibility v2" ;;
                "ISO 32000-1") desc="PDF 1.7 reference" ;;
                "ISO 32000-2") desc="PDF 2.0 reference" ;;
                *) desc="$dirname" ;;
            esac
            echo "| $dirname | $count | $desc |" >> "$REPORT_FILE"
        fi
    fi
done

cat >> "$REPORT_FILE" << 'EOF'

## Redaction Verification Tests

These are the CRITICAL tests - TRUE content removal must be verified.

### Current Verification Methods

1. **Text extraction after redaction** - independent text extraction must not find redacted content
2. **External tool verification** - pdftotext, mutool draw confirm removal
3. **Content stream inspection** - Verify glyphs removed from PDF structure
4. **Byte-level search** - Confirm text not present anywhere in file

### Test Categories

| Test Type | Purpose | Priority |
|-----------|---------|----------|
| Single term redaction | Basic functionality | CRITICAL |
| Multi-term redaction | Batch processing | CRITICAL |
| Regex pattern redaction | SSN, dates, etc. | HIGH |
| Cross-page redaction | Multi-page documents | HIGH |
| Special characters | Unicode, symbols | MEDIUM |
| Complex layouts | Tables, columns | MEDIUM |

## Known Limitations (from CLAUDE.md)

These features are NOT yet fully supported:

| Feature | Impact on Redaction | Priority |
|---------|---------------------|----------|
| Font Metrics | May misalign redaction boxes | HIGH |
| Inline Images (BI/ID/EI) | Images not redacted | MEDIUM |
| Rotated Pages | Coordinate errors | MEDIUM |
| Clipping Paths | Visual artifacts | LOW |
| Form XObjects | Nested content missed | MEDIUM |
| CIDFonts (CJK) | Text extraction issues | MEDIUM |

## Recommended Test Strategy

### Phase 1: Redaction Verification (v1.3.0 focus)

Focus on ensuring TRUE content removal works reliably:

1. Run existing redaction tests against simple corpus PDFs
2. Verify with multiple extraction tools
3. Add regression tests for any failures

### Phase 2: Format Support Expansion (post-v1.3.0)

Gradually expand PDF format support:

1. Analyze corpus failures by category
2. Prioritize based on real-world usage
3. Add tests for each fixed limitation

## Running Tests

```bash
# Run all corpus tests (long-running)
./scripts/run-corpus-tests.sh

# Run only redaction verification tests
dotnet test --filter "FullyQualifiedName~Redaction"

# Run current redaction verification tests
dotnet test PdfEditor.Tests --filter "FullyQualifiedName~Redaction"
```

## Next Steps

1. [ ] Run baseline corpus tests
2. [ ] Identify which PDFs fail redaction verification
3. [ ] Create GitHub issues for failures
4. [ ] Prioritize based on v1.3.0 requirements
EOF

echo ""
echo "Report generated: $REPORT_FILE"
echo ""

# Quick summary of feature categories
echo "Feature Categories in Corpus:"
echo ""

for subdir in "$CORPUS_PATH/PDF_A-1b"/*/; do
    if [ -d "$subdir" ]; then
        subdirname=$(basename "$subdir")
        count=$(find "$subdir" -name "*.pdf" 2>/dev/null | wc -l)
        echo "  $subdirname: $count files"
    fi
done

echo ""
echo "To run corpus tests:"
echo "  ./scripts/run-corpus-tests.sh"
echo ""

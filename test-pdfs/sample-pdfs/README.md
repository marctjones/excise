# Sample PDFs for Redaction Testing

This directory contains real-world PDFs that demonstrate various text storage patterns and edge cases for redaction testing.

## PDF Inventory

### birth-certificate-request-scrambled.pdf

**Source**: Public government form
**Issues Demonstrated**:
- Letters encoded out of reading order in content stream
- CharacterMatcher shows scrambled candidates like `"R PLEEQAUSEES TPR FIONRT..."`
- Requires sorting by position to reconstruct reading order

**Test Cases**:
- Redact "FIRST" - single word
- Redact "MAKING" - word within larger phrase
- Redact "DO" - short word
- Redact "(optional)" - word with punctuation

**Known Behavior**:
- PdfPig text selection works correctly
- Current redaction has matching failures due to letter ordering

---

### acc-global-compensation-report.pdf

**Source**: ACC 2018 Global Compensation Report executive summary
**Issues Demonstrated**:
- InDesign-produced cover artwork with transparency, vector linework, image-like shading, and corporate logo/text composition
- Multi-page business report layout useful for renderer regression coverage

**Test Cases**:
- Render all 24 pages and compare against Poppler/MuPDF references
- Visually inspect page 1 cover for title band, ACC logo, globe artwork, shadow, and typography

**Known Behavior**:
- Current renderer scan reports raw PASS for all 24 pages
- Page 1 cover has only minor antialiasing/color differences versus references

---

## Adding New Test PDFs

When adding a new problematic PDF:

1. Copy the PDF to this directory with a descriptive name
2. Add an entry to this README documenting:
   - Source (anonymized if needed)
   - Issues demonstrated
   - Specific test cases
   - Known behavior with current implementation
3. Add corresponding test cases in `RealWorldPdfTests.cs`

## PDF Characteristics Checklist

For each PDF, document:

- [ ] Text operator types used (Tj, TJ, ', ")
- [ ] Positioning operators used (Tm, Td, TD, T*)
- [ ] Font encoding type
- [ ] Whether letters are in reading order
- [ ] Presence of Form XObjects with text
- [ ] Any other unusual characteristics

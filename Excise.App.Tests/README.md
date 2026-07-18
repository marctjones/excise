# Excise.App.Tests

Comprehensive test suite for the PDF Editor application.

## Test Structure

```
Excise.App.Tests/
├── Integration/              # End-to-end integration tests
│   └── RedactionIntegrationTests.cs
├── Unit/                     # Unit tests (to be added)
├── Utilities/                # Test helpers and utilities
│   ├── TestPdfGenerator.cs  # Creates test PDFs
│   └── PdfTestHelpers.cs    # PDF inspection utilities
└── TestData/                 # Test PDF files (generated)
```

## Running Tests

```bash
# Run all tests
dotnet test

# Run with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run specific test
dotnet test --filter "FullyQualifiedName~RedactSimpleText"

# Run with coverage
dotnet test /p:CollectCoverage=true
```

## Test Categories

### Integration Tests (Current)
- **RedactionIntegrationTests**: End-to-end redaction workflow tests
  - ✅ RedactSimpleText - Verifies basic text redaction
  - ✅ RedactMultipleTextBlocks - Selective redaction
  - ✅ RedactArea_WithNoContent - Empty area handling
  - ✅ RedactMultipleAreas - Multiple redactions on same page
  - ✅ RedactPage_ShouldMaintainPdfStructure - PDF integrity

### Unit Tests (Planned)
- Matrix mathematics
- State tracking
- Bounding box calculations
- Content stream parsing
- Content stream building

## Test Dependencies

- **xUnit** - Testing framework
- **FluentAssertions** - Readable assertions
- **Serilog** - Structured logging
- **PdfSharpCore** - PDF manipulation (same as main app)
- **PdfPig** - PDF text extraction for verification

## Test Utilities

### TestPdfGenerator
Creates test PDFs with known content:
- `CreateSimpleTextPdf()` - Single text block
- `CreateMultiTextPdf()` - Multiple text blocks at different positions
- `CreateTextWithGraphicsPdf()` - Mixed text and graphics
- `CreateTransformedTextPdf()` - Rotated/scaled text
- `CreateMultiPagePdf()` - Multi-page documents

### PdfTestHelpers
PDF inspection and assertion helpers:
- `ExtractAllText()` - Extract all text from PDF
- `ExtractTextFromPage()` - Extract from specific page
- `PdfContainsText()` - Text search
- `GetPageCount()` - Page count verification
- `IsValidPdf()` - PDF validity check

## Best Practices

1. **Each test is independent** - No shared state between tests
2. **Cleanup automatically** - Temp files cleaned in Dispose()
3. **Detailed logging** - Use ITestOutputHelper for diagnostics
4. **Descriptive names** - Test names describe what they verify
5. **Arrange-Act-Assert** - Clear test structure
6. **Fluent assertions** - Readable test expectations

## Example Test

```csharp
[Fact]
public void RedactSimpleText_ShouldRemoveTextFromPdf()
{
    // Arrange
    var testPdf = TestPdfGenerator.CreateSimpleTextPdf("test.pdf", "CONFIDENTIAL");
    var textBefore = PdfTestHelpers.ExtractAllText(testPdf);
    textBefore.Should().Contain("CONFIDENTIAL");

    // Act
    var document = PdfReader.Open(testPdf, PdfDocumentOpenMode.Modify);
    _redactionService.RedactArea(document.Pages[0], new Rect(90, 90, 150, 30));
    document.Save("redacted.pdf");

    // Assert
    var textAfter = PdfTestHelpers.ExtractAllText("redacted.pdf");
    textAfter.Should().NotContain("CONFIDENTIAL");
}
```

## Next Steps

- [ ] Add unit tests for PdfMatrix
- [ ] Add unit tests for state tracking
- [ ] Add unit tests for bounding box calculator
- [ ] Add unit tests for content stream parser
- [ ] Add unit tests for content stream builder
- [ ] Add performance benchmarks
- [ ] Add error handling tests
- [ ] Increase code coverage to 80%+

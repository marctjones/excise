# Redaction Implementation Guidelines for AI Coding Assistants

**CRITICAL: READ THIS ENTIRE DOCUMENT BEFORE MODIFYING ANY REDACTION CODE**

## ⚠️ ABSOLUTE REQUIREMENTS

### 1. GLYPH-LEVEL REMOVAL IS MANDATORY

This project implements **TRUE content-level redaction** that removes text glyphs from the PDF structure. This is NOT negotiable.

**NEVER:**
- Replace glyph removal with visual-only redaction (just drawing black boxes)
- Simplify the redaction by removing content stream parsing
- Skip rebuilding the content stream after filtering
- Assume visual covering is sufficient for security

**ALWAYS:**
- Parse the PDF content stream to identify text operations
- Filter out text operations that intersect with redaction areas
- Rebuild the content stream WITHOUT the removed operations
- THEN draw the visual black box as confirmation

### 2. DEFINITION OF CORRECT REDACTION

Correct redaction means:
```
1. Text is REMOVED from PDF content stream
2. Text extraction tools (pdftotext, PdfPig, copy-paste) return empty/missing text
3. Black box provides visual confirmation (secondary)
```

Incorrect redaction (NEVER DO THIS):
```
1. Draw black box over text
2. Text still exists in PDF structure
3. Text can be extracted/copied ← SECURITY VULNERABILITY
```

### 3. WHAT "VISIBLY BLOCKED" MEANS

Text should be redacted when it is **visibly blocked** by:
- A black redaction box drawn by the user
- Any opaque shape that covers the text
- Overlapping visual elements that obscure the text

The key principle: **If a human can't see the text due to visual blocking, remove it from the PDF structure.**

### 4. CONTENT TYPES THAT MUST BE REACHED

Removal applies to **every** way content can land in the redaction area, not just top-level page text:

| Content type | How it is handled | Source |
|--------------|-------------------|--------|
| Page text (`Tj`/`TJ`/`'`/`"`) | Glyph-level removal | `GlyphRemover` |
| Named image XObjects (`Do` → `/Image`) | Drop the `Do` | `ImageRedactor` |
| **Inline images (`BI…ID…EI`)** | Drop the whole operator incl. embedded bytes (#354) | `ImageRedactor` |
| **Form XObjects (`Do` → `/Form`)** | Flatten-then-redact (#355) | `FormXObjectFlattener` |

**Inline images (#354):** the parser now retains the binary data on
`ContentOperator.InlineImageData` and `ContentStreamWriter` re-emits valid
`BI <params> ID <data> EI`. Never re-break this round-trip — if the writer
drops the bytes, every redaction silently corrupts inline images.

**Form XObjects (#355) — flatten-then-redact contract:** `RedactArea` inlines
each overlapping Form XObject into the page content stream (wrapped
`q · Matrix cm · BBox re W n · …form ops… · Q`, with the form's resources
merged into the page and collision-renamed), then runs the text/image passes
on the now-flat stream. **Crucial:** after inlining it also *prunes* the
orphaned form objects (`FormXObjectFlattener.PruneInlinedForms`). The writer
serializes every in-use object with **no garbage collection**, so skipping the
prune would re-emit the form and leak the very text you just removed — the
prune is a security requirement, not an optimization. A form still referenced
by another page is left intact (its content legitimately remains there).

> ⚠️ If you add a new way for content to reach a page (a new XObject kind, a
> pattern, a soft mask), make sure the redaction passes reach into it too, or
> it becomes a cover-don't-remove leak.

Known limitation: a Form XObject stored inside a compressed object stream
(`/ObjStm`) is not pruned from the object stream's bytes (tracked separately);
forms as ordinary indirect objects — the overwhelmingly common case — are
fully handled.

---

## Critical Files - DO NOT SIMPLIFY

These files contain the core glyph removal logic. **DO NOT** replace their functionality with simpler visual-only solutions:

### 1. `Excise.App/Services/RedactionService.cs`

**Critical Method: `RemoveContentInArea()`** (lines 127-244)
```csharp
// Step 1: Parse content stream → List<PdfOperation>
var operations = _parser.ParseContentStream(page);

// Step 2: Filter operations intersecting redaction area
foreach (var operation in operations)
{
    if (operation.IntersectsWith(area))
    {
        continue; // REMOVE this operation (glyph removal)
    }
    filteredOperations.Add(operation);
}

// Step 3: Rebuild content stream WITHOUT removed operations
var newContentBytes = _builder.BuildContentStream(filteredOperations);

// Step 4: Replace page content
ReplacePageContent(page, newContentBytes);
```

**WHY THIS MATTERS:** This is the actual glyph removal. Without it, text is just hidden, not removed.

### 2. `Excise.App/Services/Redaction/ContentStreamParser.cs`

Parses PDF content streams into structured operations with bounding boxes. This enables accurate intersection detection for text operations.

**DO NOT** remove or simplify the text operation parsing.

### 3. `Excise.App/Services/Redaction/ContentStreamBuilder.cs`

Rebuilds PDF content streams from filtered operations. This creates the new content stream without removed text.

**DO NOT** remove or simplify - this is essential for glyph removal.

### 4. `Excise.App/Services/Redaction/TextBoundsCalculator.cs`

Calculates accurate bounding boxes for text operations. Enables precise intersection detection.

**DO NOT** simplify bounding box calculations - accuracy is critical.

---

## Required Tests for Glyph Removal

### MUST-PASS Tests

These tests verify actual glyph removal. If ANY of these fail, the redaction is broken:

#### Test 1: Text Extraction After Redaction
```csharp
[Fact]
public void RedactText_ShouldRemoveFromPdfStructure()
{
    // Create PDF with known text
    CreatePdfWithText("CONFIDENTIAL");

    // Verify text exists
    var textBefore = ExtractText(pdf);
    textBefore.Should().Contain("CONFIDENTIAL");

    // Apply redaction over text
    RedactArea(page, textBoundingBox);

    // CRITICAL: Text must be ABSENT after redaction
    var textAfter = ExtractText(pdf);
    textAfter.Should().NotContain("CONFIDENTIAL",
        "Glyph must be REMOVED from PDF structure, not just visually hidden");
}
```

#### Test 2: Selective Removal
```csharp
[Fact]
public void RedactText_ShouldPreserveUnredactedText()
{
    // Create PDF with multiple text items
    CreatePdfWithText("REDACT-ME", "KEEP-ME");

    // Redact only first item
    RedactArea(page, firstTextBox);

    // CRITICAL: Only targeted text removed
    var textAfter = ExtractText(pdf);
    textAfter.Should().NotContain("REDACT-ME");  // Removed
    textAfter.Should().Contain("KEEP-ME");       // Preserved
}
```

#### Test 3: Content Stream Modification
```csharp
[Fact]
public void RedactText_ShouldModifyContentStream()
{
    // Get content stream before
    var contentBefore = GetContentStream(page);

    // Apply redaction
    RedactArea(page, textArea);

    // Content stream MUST be different
    var contentAfter = GetContentStream(page);
    contentAfter.Should().NotEqual(contentBefore,
        "Content stream must be modified for glyph removal");
}
```

#### Test 4: Independent Verification
```csharp
[Fact]
public void RedactText_ShouldPassIndependentVerification()
{
    // Apply redaction
    RedactArea(page, textArea);
    SavePdf(path);

    // Use DIFFERENT library (PdfPig) to verify
    using var verifier = PdfDocument.Open(path);
    var text = verifier.GetPages().First().Text;

    text.Should().NotContain("REDACTED-TEXT",
        "Independent PDF library must confirm glyph removal");
}
```

### Test File Locations

```
Excise.App.Tests/Integration/
├── BlackBoxRedactionTests.cs      ← Primary glyph removal tests
├── ComprehensiveRedactionTests.cs ← Complex scenarios
├── RedactionIntegrationTests.cs   ← Full workflow tests
└── SpecializedRedactionTests.cs   ← Edge cases
```

### Running Critical Tests

```bash
# Run ALL redaction tests (must all pass)
dotnet test --filter "FullyQualifiedName~Redaction"

# Run with verbose output to see glyph removal logs
dotnet test --filter "FullyQualifiedName~Redaction" --logger "console;verbosity=detailed"
```

---

## Common Mistakes to AVOID

### ❌ Mistake 1: Visual-Only Redaction
```csharp
// WRONG - This just draws a black box, text is still extractable
public void RedactArea(PdfPage page, Rect area)
{
    using var gfx = XGraphics.FromPdfPage(page);
    gfx.DrawRectangle(XBrushes.Black, area);  // ← INSECURE
}
```

### ❌ Mistake 2: Removing Content Parsing
```csharp
// WRONG - Removing the parser breaks glyph removal
public void RedactArea(PdfPage page, Rect area)
{
    // "Simplified" version without parsing
    DrawBlackRectangle(page, area);  // ← INSECURE
}
```

### ❌ Mistake 3: Not Rebuilding Content Stream
```csharp
// WRONG - Must rebuild content stream after filtering
var filtered = operations.Where(op => !op.IntersectsWith(area));
// Missing: BuildContentStream(filtered) and ReplacePageContent()
DrawBlackRectangle(page, area);  // ← Glyphs still in PDF
```

### ❌ Mistake 4: Using Wrong Intersection Logic
```csharp
// WRONG - Must check actual bounding box intersection
if (operation.Position == area.TopLeft)  // ← Too restrictive
{
    // Miss operations that overlap but don't start at same point
}
```

### ✅ Correct Implementation
```csharp
public void RedactArea(PdfPage page, Rect area)
{
    // 1. Parse content stream
    var operations = _parser.ParseContentStream(page);

    // 2. Filter operations
    var filtered = operations.Where(op => !op.IntersectsWith(area)).ToList();

    // 3. Rebuild content stream
    var newContent = _builder.BuildContentStream(filtered);

    // 4. Replace page content (GLYPH REMOVAL HAPPENS HERE)
    ReplacePageContent(page, newContent);

    // 5. Draw visual confirmation
    DrawBlackRectangle(page, area);
}
```

---

## How to Verify Glyph Removal

### Method 1: Run the Test Suite
```bash
cd Excise.App.Tests
dotnet test --filter "FullyQualifiedName~Redaction"
```

All tests MUST pass. If any fail, glyph removal is broken.

### Method 2: Manual Text Extraction
```bash
# Using pdftotext (Linux)
pdftotext redacted.pdf output.txt
grep "REDACTED_TEXT" output.txt  # Should return nothing
```

### Method 3: PDF Editor Test
1. Open redacted PDF in Adobe Acrobat/other viewer
2. Try to select text under black box → Nothing selectable
3. Use Find (Ctrl+F) → Text not found
4. Try copy-paste → No text copied

### Method 4: Code Verification
```csharp
using var doc = PdfDocument.Open("redacted.pdf");
var text = doc.GetPages().First().Text;
Assert.DoesNotContain("REDACTED_TEXT", text);
```

---

## Regression Prevention Checklist

Before committing ANY changes to redaction code:

- [ ] **Run all redaction tests**: `dotnet test --filter "FullyQualifiedName~Redaction"`
- [ ] **Verify text extraction fails for redacted content**
- [ ] **Confirm content stream is modified (not just black box added)**
- [ ] **Check that unredacted content is preserved**
- [ ] **Test with independent PDF library (PdfPig)**

### Required Test Coverage

| Test | Purpose | Must Pass |
|------|---------|-----------|
| `GeneratePDF_ApplyBlackBox_VerifyContentRemoval` | Core glyph removal | ✅ YES |
| `RedactMappedContent_ShouldRemoveOnlyTargetedItems` | Selective removal | ✅ YES |
| `RedactRandomAreas_ShouldOnlyRemoveIntersectingContent` | Intersection accuracy | ✅ YES |
| `RedactComplexDocument_ShouldRemoveSensitiveDataOnly` | Real-world scenario | ✅ YES |
| `RedactText_ShouldModifyContentStream` | Structure modification | ✅ YES |

---

## When Modifying Redaction Code

### Acceptable Modifications
- Improving bounding box accuracy
- Adding support for more PDF operators
- Optimizing performance without changing behavior
- Adding more comprehensive tests
- Improving logging/debugging

### UNACCEPTABLE Modifications
- Removing content stream parsing
- Removing content stream rebuilding
- Replacing glyph removal with visual-only
- Simplifying intersection detection to be less accurate
- Removing or weakening tests

### Before/After Test Pattern

Always test before AND after modifications:

```csharp
// Before modification
var textBefore = ExtractText(pdf);
Assert.Contains("SECRET", textBefore);

// Apply redaction
RedactArea(page, area);

// After modification - this MUST change
var textAfter = ExtractText(pdf);
Assert.DoesNotContain("SECRET", textAfter);
```

---

## Summary for AI Coding Assistants

When working on this codebase:

1. **UNDERSTAND** that redaction means glyph removal from PDF structure
2. **NEVER** simplify redaction to visual-only (drawing black boxes)
3. **ALWAYS** maintain: parse → filter → rebuild → replace → draw
4. **RUN** all redaction tests before and after changes
5. **VERIFY** with independent tools (pdftotext, PdfPig)
6. **PRESERVE** working glyph removal code - do not "improve" by simplifying
7. **TEST** that redacted text cannot be extracted after redaction

**The goal is SECURITY, not simplicity. Visual-only redaction is a security vulnerability.**

---

## Quick Reference

### File Locations
```
Excise.App/Services/RedactionService.cs           ← Main entry point
Excise.App/Services/Redaction/ContentStreamParser.cs  ← Parses PDF operations
Excise.App/Services/Redaction/ContentStreamBuilder.cs ← Rebuilds PDF content
Excise.App/Services/Redaction/TextBoundsCalculator.cs ← Text positioning
```

### Test Locations
```
Excise.App.Tests/Integration/BlackBoxRedactionTests.cs
Excise.App.Tests/Integration/ComprehensiveRedactionTests.cs
Excise.App.Tests/Integration/RedactionIntegrationTests.cs
```

### Commands
```bash
# Build
dotnet build

# Run tests
dotnet test --filter "FullyQualifiedName~Redaction"

# Run demo
dotnet run --project Excise.App.Demo
```

### Key Assertion
```csharp
// This is the core assertion - if this fails, redaction is broken
var textAfter = PdfTestHelpers.ExtractAllText(redactedPdf);
textAfter.Should().NotContain("REDACTED_TEXT",
    "Text must be REMOVED from PDF structure, not just hidden");
```

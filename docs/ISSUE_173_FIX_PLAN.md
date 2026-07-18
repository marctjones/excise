# Issue #173 Fix Plan: 270° Sequential Redaction Failure

## Executive Summary

**Problem**: Sequential redactions on 270° rotated pages corrupt text.
**Root Cause**: `OperationReconstructor` uses visual coordinates (from PdfPig) in `Tm` operators, but `Tm` needs content stream coordinates.
**Fix**: Transform coordinates from visual space to content stream space before creating `Tm` operators.

---

## Part 1: Verification Tests BEFORE Fix

These tests prove the hypothesis and confirm what's broken vs working.

### Test 1.1: Verify 0° Baseline (Should Pass - Already Works)

```csharp
[Fact]
public void Verify_0Degree_CoordinatesPreserved_AfterRedaction()
{
    // Create 0° rotated PDF with text at known position
    // After redaction, remaining text should still be at approximately the same positions
    // This proves the baseline case works
}
```

**Expected Result**: PASS - 0° has no rotation, so visual == content stream coords.

### Test 1.2: Verify Source vs Redacted Coordinate Shift for 270°

```csharp
[Fact]
public void Verify_270Degree_CoordinatesShift_AfterRedaction()
{
    // Create 270° PDF, record letter positions
    // Perform one redaction
    // Record letter positions after
    // Assert: X coordinates should NOT have jumped from ~83 to ~682

    // This test should FAIL before fix, PASS after
}
```

**Expected Result**: FAIL (before fix) - Proves coordinates are being corrupted.

### Test 1.3: Verify Tm Operator Receives Visual Coordinates (Bug Confirmation)

```csharp
[Fact]
public void Verify_TmOperator_ReceivesWrongCoordinates_BeforeFix()
{
    // Create 270° PDF with 'N' at visual position ~(83, 108)
    // For 270° rotation:
    //   Content stream position should be: (visualY, mediaBoxHeight - visualX)
    //                                    = (108, 792 - 83) = (108, 709)
    //
    // Extract Tm from content stream after redaction
    // Assert: Tm uses ~(83, 108) NOT (108, 709)

    // This proves the bug: visual coords are used instead of content stream coords
}
```

**Expected Result**: Confirms Tm operator receives wrong coordinates.

### Test 1.4: Verify 90° Works Despite Wrong Coordinates

```csharp
[Fact]
public void Verify_90Degree_WorksDespiteNegativeY()
{
    // Create 90° PDF, perform sequential redactions
    // After redaction, Y coordinates become negative
    // But text matching STILL works because it's text-based not position-based

    // This explains why 90° works "accidentally"
}
```

**Expected Result**: PASS - Explains why 90° works despite coordinate issues.

---

## Part 2: Implementation Plan

### Step 2.1: Add Rotation Context to TextSegment

**File**: `Excise.App.Redaction/GlyphLevel/TextSegmenter.cs`

The `TextSegment` class currently stores visual coordinates. We need to either:
- **Option A**: Store both visual AND content stream coordinates
- **Option B**: Transform at creation time (requires rotation info)
- **Option C**: Transform later in OperationReconstructor (requires passing rotation info)

**Chosen**: Option C - Transform in OperationReconstructor because:
- Minimal API changes
- Clear separation of concerns
- TextSegmenter stays focused on segmentation

### Step 2.2: Pass Rotation Info Through the Pipeline

**File**: `Excise.App.Redaction/GlyphLevel/GlyphRemover.cs`

Current signature:
```csharp
public List<PdfOperation> ProcessOperations(
    List<PdfOperation> operations,
    IReadOnlyList<Letter> letters,
    PdfRectangle redactionArea)
```

New signature:
```csharp
public List<PdfOperation> ProcessOperations(
    List<PdfOperation> operations,
    IReadOnlyList<Letter> letters,
    PdfRectangle redactionArea,
    int pageRotation = 0,           // NEW
    double mediaBoxWidth = 612,      // NEW
    double mediaBoxHeight = 792)     // NEW
```

### Step 2.3: Pass to OperationReconstructor

**File**: `Excise.App.Redaction/GlyphLevel/OperationReconstructor.cs`

Current signature:
```csharp
public List<PdfOperation> ReconstructWithPositioning(
    List<TextSegment> segments,
    TextOperation originalOperation)
```

New signature:
```csharp
public List<PdfOperation> ReconstructWithPositioning(
    List<TextSegment> segments,
    TextOperation originalOperation,
    int pageRotation = 0,           // NEW
    double mediaBoxWidth = 612,      // NEW
    double mediaBoxHeight = 792)     // NEW
```

### Step 2.4: Transform Coordinates in CreatePositioningOperation

**File**: `Excise.App.Redaction/GlyphLevel/OperationReconstructor.cs`

Current code (lines 87-112):
```csharp
public TextStateOperation CreatePositioningOperation(TextSegment segment, double fontSize)
{
    return new TextStateOperation
    {
        Operator = "Tm",
        Operands = new List<object>
        {
            scale, 0.0, 0.0, scale,
            segment.StartX,  // ❌ VISUAL coordinate
            segment.StartY   // ❌ VISUAL coordinate
        },
        ...
    };
}
```

New code:
```csharp
public TextStateOperation CreatePositioningOperation(
    TextSegment segment,
    double fontSize,
    int pageRotation = 0,
    double mediaBoxWidth = 612,
    double mediaBoxHeight = 792)
{
    // Transform visual coordinates to content stream coordinates
    var (contentX, contentY) = RotationTransform.VisualToContentStream(
        segment.StartX,
        segment.StartY,
        pageRotation,
        mediaBoxWidth,
        mediaBoxHeight);

    return new TextStateOperation
    {
        Operator = "Tm",
        Operands = new List<object>
        {
            scale, 0.0, 0.0, scale,
            contentX,  // ✅ Content stream coordinate
            contentY   // ✅ Content stream coordinate
        },
        ...
    };
}
```

### Step 2.5: Update ContentStreamRedactor to Pass Rotation

**File**: `Excise.App.Redaction/ContentStream/ContentStreamRedactor.cs`

Current call (line 114):
```csharp
modifiedOps = _glyphRemover.ProcessOperations(modifiedOps, letters, letterArea);
```

The `ContentStreamRedactor` is called from `TextRedactor.RedactPageContent()` which already has:
- `rotation` from `GetPageRotation(page)`
- `mediaBox.Width` and `mediaBox.Height`

We need to pass these through. Add parameters to `RedactContentStream()`:

```csharp
public (byte[] modifiedContent, List<RedactionDetail> details) RedactContentStream(
    byte[] contentBytes,
    double pageHeight,
    List<PdfRectangle> redactionAreas,
    IReadOnlyList<Letter>? letters,
    RedactionOptions options,
    List<PdfRectangle>? visualRedactionAreas = null,
    int pageRotation = 0,           // NEW
    double mediaBoxWidth = 612,      // NEW
    double mediaBoxHeight = 792)     // NEW
```

### Step 2.6: Update TextRedactor to Pass Rotation

**File**: `Excise.App.Redaction/TextRedactor.cs`

Current call (lines 488-495):
```csharp
var (newContentBytes, details, formXObjectResults) = _contentStreamRedactor.RedactContentStreamWithFormXObjects(
    contentBytes,
    contentStreamPageHeight,
    contentStreamAreas,
    letters,
    options,
    resources,
    redactionAreas);
```

New call:
```csharp
var (newContentBytes, details, formXObjectResults) = _contentStreamRedactor.RedactContentStreamWithFormXObjects(
    contentBytes,
    contentStreamPageHeight,
    contentStreamAreas,
    letters,
    options,
    resources,
    redactionAreas,
    rotation,             // NEW - from GetPageRotation(page)
    mediaBox.Width,       // NEW
    mediaBox.Height);     // NEW
```

---

## Part 3: Verification Tests AFTER Fix

### Test 3.1: 270° Sequential Redaction Works

```csharp
[Theory]
[InlineData(270)]
public void RedactText_SequentialRedactions_OnRotatedPage_RemovesAllTargetedText(int rotationDegrees)
{
    // This is the existing test that currently fails for 270°
    // After fix, it should pass
}
```

**Expected Result**: PASS

### Test 3.2: Regression - 90° Still Works

```csharp
[Theory]
[InlineData(90)]
public void RedactText_SequentialRedactions_OnRotatedPage_90Degree_StillWorks(int rotationDegrees)
{
    // Same test as above but specifically for 90°
    // Must continue to work after fix
}
```

**Expected Result**: PASS

### Test 3.3: Regression - 0° and 180° Still Work

```csharp
[Theory]
[InlineData(0)]
[InlineData(180)]
public void RedactText_SequentialRedactions_OnRotatedPage_NoRegressions(int rotationDegrees)
{
    // Verify 0° and 180° still work
}
```

**Expected Result**: PASS

### Test 3.4: Coordinates Are Stable After Multiple Redactions

```csharp
[Theory]
[InlineData(0)]
[InlineData(90)]
[InlineData(180)]
[InlineData(270)]
public void Verify_CoordinatesStable_AfterMultipleRedactions(int rotation)
{
    // Create PDF with multiple lines
    // Record initial letter positions
    // Perform 3 sequential redactions
    // Assert: Non-redacted letters are still at approximately the same positions
    // (within tolerance for font metrics differences)
}
```

**Expected Result**: PASS for all rotations

### Test 3.5: Tm Operator Uses Content Stream Coordinates

```csharp
[Theory]
[InlineData(270, 83, 108, 108, 709)]  // Expected transform
[InlineData(90, 509, 204, 408, 509)]  // Expected transform
public void Verify_TmOperator_UsesContentStreamCoordinates(
    int rotation,
    double visualX, double visualY,
    double expectedContentX, double expectedContentY)
{
    // Create rotated PDF with text at known visual position
    // Perform redaction
    // Extract Tm operators from output content stream
    // Assert: Tm uses content stream coordinates, not visual
}
```

**Expected Result**: PASS

---

## Part 4: Risk Analysis

### Risk 4.1: Breaking Existing 0° Redactions

**Mitigation**:
- For `rotation=0`, `VisualToContentStream()` returns input unchanged
- Run full test suite before merging
- Test 3.3 specifically verifies this

### Risk 4.2: Breaking 90°/180° Rotations

**Mitigation**:
- These already work (90° accidentally, 180° correctly)
- Run dedicated tests for each rotation
- If 90° breaks, the transformation formulas may need adjustment

### Risk 4.3: Unusual Page Sizes

**Mitigation**:
- MediaBox dimensions are passed explicitly
- Test with non-standard page sizes (A4, legal, custom)

### Risk 4.4: API Breaking Changes

**Impact**: All new parameters have defaults, so existing callers won't break.

**Mitigation**:
- All new parameters are optional with sensible defaults
- For `rotation=0` (default), behavior is unchanged

### Risk 4.5: Performance Impact

**Impact**: Minimal - just two additional floating-point calculations per segment.

### Risk 4.6: Nested Form XObjects

**Question**: Do Form XObjects have their own rotation?

**Investigation Needed**: Check if Form XObjects can have independent rotation. If so, may need to handle separately.

---

## Part 5: Implementation Order

1. **Write pre-fix verification tests** (Part 1) - Confirm hypothesis
2. **Implement changes** in this order:
   a. Add parameters to `OperationReconstructor.CreatePositioningOperation()`
   b. Add parameters to `OperationReconstructor.ReconstructWithPositioning()`
   c. Add parameters to `GlyphRemover.ProcessOperations()`
   d. Add parameters to `ContentStreamRedactor.RedactContentStream()`
   e. Update `TextRedactor.RedactPageContent()` to pass rotation
3. **Run pre-fix tests** - Should now pass
4. **Enable 270° sequential test** - Should now pass
5. **Run full test suite** - Check for regressions
6. **Write post-fix verification tests** (Part 3)

---

## Part 6: Files to Modify (Summary)

| File | Change |
|------|--------|
| `GlyphLevel/OperationReconstructor.cs` | Add rotation params, transform coords |
| `GlyphLevel/GlyphRemover.cs` | Add rotation params, pass through |
| `ContentStream/ContentStreamRedactor.cs` | Add rotation params, pass through |
| `TextRedactor.cs` | Pass rotation/dimensions from page |
| `RotatedPageRedactionTests.cs` | Enable 270° test, add verification tests |

---

## Appendix: Transformation Formulas (from RotationTransform.cs)

```csharp
// 0°: No change
(visualX, visualY)

// 90° CW:
(visualHeight - visualY, visualX)

// 180°:
(mediaBoxWidth - visualX, mediaBoxHeight - visualY)

// 270° CCW:
(visualY, mediaBoxHeight - visualX)
```

For 270° with visual position (83, 108) and MediaBox 612x792:
- contentX = 108
- contentY = 792 - 83 = 709

This is what the Tm operator should contain, not (83, 108).

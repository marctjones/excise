# PDF 2.0 Content Stream Operator Testing Plan

This document provides a comprehensive testing strategy for all PDF 2.0 content stream operators
as defined in ISO 32000-2:2020 (PDF 2.0). The testing covers four key capabilities:
- **Reading**: Parsing operators from content streams
- **Writing**: Serializing operators back to content streams
- **Rendering**: Drawing operators to screen/image
- **Redaction**: Removing/modifying operators during redaction

## Table of Contents

1. [Operator Categories](#operator-categories)
2. [Current Implementation Status](#current-implementation-status)
3. [Testing Strategy](#testing-strategy)
4. [Test Implementation Plan](#test-implementation-plan)
5. [Test Data Requirements](#test-data-requirements)

---

## Operator Categories

### 1. Graphics State Operators (Table 56, Section 8.4.4)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `q` | - | Save graphics state | ✅ | ✅ | ✅ | ✅ |
| `Q` | - | Restore graphics state | ✅ | ✅ | ✅ | ✅ |
| `cm` | a b c d e f | Modify CTM (concat matrix) | ✅ | ✅ | ✅ | ✅ |
| `w` | lineWidth | Set line width | ❌ | ❌ | ✅ | ❌ |
| `J` | lineCap | Set line cap style | ❌ | ❌ | ❌ | ❌ |
| `j` | lineJoin | Set line join style | ❌ | ❌ | ❌ | ❌ |
| `M` | miterLimit | Set miter limit | ❌ | ❌ | ❌ | ❌ |
| `d` | dashArray dashPhase | Set line dash pattern | ❌ | ❌ | ❌ | ❌ |
| `ri` | intent | Set rendering intent | ❌ | ❌ | ❌ | ❌ |
| `i` | flatness | Set flatness tolerance | ❌ | ❌ | ❌ | ❌ |
| `gs` | dictName | Set parameters from ExtGState | ❌ | ❌ | ❌ | ❌ |

### 2. Special Graphics State Operators (Table 57)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `cm` | a b c d e f | Modify CTM | ✅ | ✅ | ✅ | ✅ |

### 3. Path Construction Operators (Table 58, Section 8.5.2)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `m` | x y | Move to (begin new subpath) | ✅ | ✅ | ✅ | ✅ |
| `l` | x y | Line to | ✅ | ✅ | ✅ | ✅ |
| `c` | x1 y1 x2 y2 x3 y3 | Cubic Bézier curve | ✅ | ✅ | ✅ | ✅ |
| `v` | x2 y2 x3 y3 | Cubic Bézier (current point as control) | ✅ | ✅ | ✅ | ✅ |
| `y` | x1 y1 x3 y3 | Cubic Bézier (endpoint as control) | ✅ | ✅ | ✅ | ✅ |
| `h` | - | Close subpath | ✅ | ✅ | ✅ | ✅ |
| `re` | x y width height | Rectangle | ✅ | ✅ | ✅ | ✅ |

### 4. Path Painting Operators (Table 59, Section 8.5.3)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `S` | - | Stroke path | ✅ | ✅ | ✅ | ✅ |
| `s` | - | Close and stroke | ✅ | ✅ | ✅ | ✅ |
| `f` | - | Fill path (nonzero winding) | ✅ | ✅ | ✅ | ✅ |
| `F` | - | Fill path (nonzero, obsolete) | ✅ | ✅ | ✅ | ✅ |
| `f*` | - | Fill path (even-odd rule) | ✅ | ✅ | ✅ | ✅ |
| `B` | - | Fill and stroke (nonzero) | ✅ | ✅ | ✅ | ✅ |
| `B*` | - | Fill and stroke (even-odd) | ✅ | ✅ | ✅ | ✅ |
| `b` | - | Close, fill, stroke (nonzero) | ✅ | ✅ | ✅ | ✅ |
| `b*` | - | Close, fill, stroke (even-odd) | ✅ | ✅ | ✅ | ✅ |
| `n` | - | End path without painting | ✅ | ✅ | ✅ | ✅ |

### 5. Clipping Path Operators (Table 60, Section 8.5.4)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `W` | - | Clip to path (nonzero) | ⚠️ | ❌ | ❌ | ⚠️ |
| `W*` | - | Clip to path (even-odd) | ⚠️ | ❌ | ❌ | ⚠️ |

### 6. Text Object Operators (Table 105, Section 9.4)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `BT` | - | Begin text object | ✅ | ✅ | ✅ | ✅ |
| `ET` | - | End text object | ✅ | ✅ | ✅ | ✅ |

### 7. Text State Operators (Table 103, Section 9.3)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `Tc` | charSpace | Set character spacing | ✅ | ✅ | ✅ | ✅ |
| `Tw` | wordSpace | Set word spacing | ✅ | ✅ | ✅ | ✅ |
| `Tz` | scale | Set horizontal scaling | ✅ | ✅ | ✅ | ✅ |
| `TL` | leading | Set text leading | ✅ | ✅ | ✅ | ✅ |
| `Tf` | font size | Set font and size | ✅ | ✅ | ✅ | ✅ |
| `Tr` | render | Set text rendering mode | ⚠️ | ❌ | ✅ | ❌ |
| `Ts` | rise | Set text rise | ⚠️ | ❌ | ✅ | ❌ |

### 8. Text Positioning Operators (Table 106, Section 9.4.2)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `Td` | tx ty | Move text position | ✅ | ✅ | ✅ | ✅ |
| `TD` | tx ty | Move and set leading | ✅ | ✅ | ✅ | ✅ |
| `Tm` | a b c d e f | Set text matrix | ✅ | ✅ | ✅ | ✅ |
| `T*` | - | Move to start of next line | ✅ | ✅ | ✅ | ✅ |

### 9. Text Showing Operators (Table 107, Section 9.4.3)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `Tj` | string | Show text string | ✅ | ✅ | ✅ | ✅ |
| `TJ` | array | Show text with positioning | ✅ | ✅ | ✅ | ✅ |
| `'` | string | Move to next line and show | ✅ | ⚠️ | ❌ | ⚠️ |
| `"` | aw ac string | Set spacing, move, show | ✅ | ⚠️ | ❌ | ⚠️ |

### 10. Type 3 Font Operators (Table 111, Section 9.6.5)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `d0` | wx wy | Set glyph width (Type 3) | ❌ | ❌ | ❌ | ❌ |
| `d1` | wx wy llx lly urx ury | Set glyph width and bbox | ❌ | ❌ | ❌ | ❌ |

### 11. Color Operators (Tables 72-73, Section 8.6)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `CS` | name | Set stroke color space | ⚠️ | ❌ | ❌ | ❌ |
| `cs` | name | Set fill color space | ⚠️ | ❌ | ❌ | ❌ |
| `SC` | c1...cn | Set stroke color | ⚠️ | ❌ | ❌ | ❌ |
| `SCN` | c1...cn [name] | Set stroke color (patterns) | ⚠️ | ❌ | ❌ | ❌ |
| `sc` | c1...cn | Set fill color | ⚠️ | ❌ | ❌ | ❌ |
| `scn` | c1...cn [name] | Set fill color (patterns) | ⚠️ | ❌ | ❌ | ❌ |
| `G` | gray | Set stroke gray | ⚠️ | ❌ | ✅ | ❌ |
| `g` | gray | Set fill gray | ⚠️ | ❌ | ✅ | ❌ |
| `RG` | r g b | Set stroke RGB | ⚠️ | ❌ | ✅ | ❌ |
| `rg` | r g b | Set fill RGB | ⚠️ | ❌ | ✅ | ❌ |
| `K` | c m y k | Set stroke CMYK | ⚠️ | ❌ | ❌ | ❌ |
| `k` | c m y k | Set fill CMYK | ⚠️ | ❌ | ❌ | ❌ |

### 12. Shading Operators (Table 76, Section 8.7.4.2)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `sh` | name | Paint shading pattern | ⚠️ | ❌ | ❌ | ❌ |

### 13. Inline Image Operators (Table 90, Section 8.9.7)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `BI` | - | Begin inline image | ⚠️ | ❌ | ❌ | ⚠️ |
| `ID` | - | Inline image data | ⚠️ | ❌ | ❌ | ⚠️ |
| `EI` | - | End inline image | ⚠️ | ❌ | ❌ | ⚠️ |

### 14. XObject Operators (Table 85, Section 8.8)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `Do` | name | Paint XObject | ✅ | ✅ | ❌ | ✅ |

### 15. Marked Content Operators (Table 352, Section 14.6)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `MP` | tag | Define marked-content point | ⚠️ | ❌ | ❌ | ❌ |
| `DP` | tag properties | Define marked-content point with properties | ⚠️ | ❌ | ❌ | ❌ |
| `BMC` | tag | Begin marked-content sequence | ⚠️ | ❌ | ❌ | ❌ |
| `BDC` | tag properties | Begin marked-content with properties | ⚠️ | ❌ | ❌ | ❌ |
| `EMC` | - | End marked-content sequence | ⚠️ | ❌ | ❌ | ❌ |

### 16. Compatibility Operators (Table 353, Section 14.11.6)

| Operator | Operands | Description | Read | Write | Render | Redact |
|----------|----------|-------------|------|-------|--------|--------|
| `BX` | - | Begin compatibility section | ⚠️ | ❌ | ❌ | ❌ |
| `EX` | - | End compatibility section | ⚠️ | ❌ | ❌ | ❌ |

---

## Legend

- ✅ = Fully implemented and tested
- ⚠️ = Partially implemented or parsed but not fully tested
- ❌ = Not implemented

---

## Current Implementation Status

### By Component

| Component | Operators Implemented | Operators Needed | Coverage |
|-----------|----------------------|------------------|----------|
| Excise.Core Parser | 35 | 73 | 48% |
| Excise.Rendering | 38 | 73 | 52% |
| Excise.App.Redaction | 32 | 73 | 44% |
| Content Stream Writer | 25 | 73 | 34% |

### Critical Gaps

1. **Graphics State**: `w`, `J`, `j`, `M`, `d`, `ri`, `i`, `gs` not tracked
2. **Clipping**: `W`, `W*` not rendered or respected in redaction
3. **Color**: Full color space support missing
4. **Type 3 Fonts**: `d0`, `d1` not supported
5. **Inline Images**: `BI`/`ID`/`EI` parsing incomplete
6. **Marked Content**: Not preserved during redaction

---

## Testing Strategy

### Test Categories

#### 1. Unit Tests (Per Operator)

Each operator needs tests for:
- **Parsing**: Parse operator with various operand configurations
- **Serialization**: Serialize operator back to PDF syntax
- **State Tracking**: Verify state changes (CTM, text state, graphics state)
- **Bounds Calculation**: Verify bounding box calculation

#### 2. Integration Tests (Operator Combinations)

- **Graphics state stack**: `q`/`Q` nested operations
- **Path operations**: Construction + painting sequences
- **Text operations**: `BT` → positioning → showing → `ET`
- **Color + painting**: Color operators + path painting
- **Transformations**: `cm` effects on subsequent operations

#### 3. Round-Trip Tests

- Parse content stream → Serialize → Parse again → Compare

#### 4. Rendering Tests

- Generate PDF with specific operators → Render → Compare to reference image

#### 5. Redaction Tests

- Create PDF with known content → Redact area → Verify removal
- Verify non-target content preserved

---

## Test Implementation Plan

### Phase 1: Core Operators (Priority: High)

**Target**: 100% coverage for operators used in 90% of PDFs

```
Graphics State: q, Q, cm
Path Construction: m, l, c, v, y, h, re
Path Painting: S, s, f, F, f*, B, B*, b, b*, n
Text Object: BT, ET
Text State: Tf, Tc, Tw, Tz, TL
Text Positioning: Td, TD, Tm, T*
Text Showing: Tj, TJ
XObjects: Do
Color: g, G, rg, RG
```

**Test Class Structure**:
```
Excise.Core.Tests/
  Content/
    Operators/
      GraphicsStateOperatorTests.cs      - q, Q, cm
      PathConstructionOperatorTests.cs   - m, l, c, v, y, h, re
      PathPaintingOperatorTests.cs       - S, s, f, F, f*, B, B*, b, b*, n
      TextObjectOperatorTests.cs         - BT, ET
      TextStateOperatorTests.cs          - Tf, Tc, Tw, Tz, TL, Tr, Ts
      TextPositioningOperatorTests.cs    - Td, TD, Tm, T*
      TextShowingOperatorTests.cs        - Tj, TJ, ', "
      ColorOperatorTests.cs              - g, G, rg, RG, k, K, cs, CS, sc, SC
      XObjectOperatorTests.cs            - Do
```

### Phase 2: Advanced Operators (Priority: Medium)

**Target**: Support for complex documents

```
Graphics State: w, J, j, M, d, gs
Clipping: W, W*
Color: K, k, CS, cs, SC, SCN, sc, scn
Shading: sh
Marked Content: BMC, BDC, EMC, MP, DP
```

### Phase 3: Specialized Operators (Priority: Low)

**Target**: Full PDF 2.0 compliance

```
Type 3 Fonts: d0, d1
Inline Images: BI, ID, EI
Compatibility: BX, EX
Rendering Intent: ri
Flatness: i
```

---

## Test Data Requirements

### Test PDF Generator Extensions

Current `TestPdfGenerator` creates simple PDFs. Extend for:

1. **Multi-operator sequences**
2. **Nested graphics states** (multiple `q`/`Q`)
3. **Complex paths** (Bézier curves, multiple subpaths)
4. **All text positioning operators**
5. **Color spaces** (DeviceGray, DeviceRGB, DeviceCMYK)
6. **Clipping paths**
7. **Inline images**

### Reference PDF Collection

Create reference PDFs for each operator:

```
test-pdfs/operators/
  graphics-state/
    q-Q-basic.pdf              - Basic state save/restore
    q-Q-nested.pdf             - Nested save/restore
    cm-translate.pdf           - Translation transform
    cm-rotate.pdf              - Rotation transform
    cm-scale.pdf               - Scale transform
    cm-combined.pdf            - Combined transforms
    w-line-width.pdf           - Line width variations

  paths/
    m-l-basic.pdf              - Basic lines
    c-bezier.pdf               - Cubic Bézier curves
    v-y-bezier.pdf             - Bézier variations
    re-rectangle.pdf           - Rectangles
    h-close-path.pdf           - Closed paths

  painting/
    S-stroke.pdf               - Stroke only
    f-fill.pdf                 - Fill only
    B-fill-stroke.pdf          - Fill and stroke
    f-star-even-odd.pdf        - Even-odd fill rule

  text/
    Tj-basic.pdf               - Basic text
    TJ-positioning.pdf         - Text with glyph positioning
    Td-move.pdf                - Text movement
    Tm-matrix.pdf              - Text matrix
    Tc-Tw-spacing.pdf          - Character/word spacing
    Tz-scaling.pdf             - Horizontal scaling

  color/
    g-G-gray.pdf               - Grayscale colors
    rg-RG-rgb.pdf              - RGB colors
    k-K-cmyk.pdf               - CMYK colors
    cs-CS-colorspace.pdf       - Color space operators

  images/
    Do-image-xobject.pdf       - XObject images
    BI-ID-EI-inline.pdf        - Inline images

  clipping/
    W-clip-nonzero.pdf         - Nonzero clipping
    W-star-clip-even-odd.pdf   - Even-odd clipping

  marked-content/
    BMC-EMC-basic.pdf          - Basic marked content
    BDC-properties.pdf         - Marked content with properties
```

---

## Test Implementation Examples

### Example 1: Graphics State Operator Test

```csharp
namespace Excise.Core.Tests.Content.Operators;

public class GraphicsStateOperatorTests
{
    [Fact]
    public void Parse_q_Q_SavesAndRestoresState()
    {
        // Arrange: Content stream with state save/restore
        var content = @"
            q
            1 0 0 1 100 100 cm
            Q
        ";

        // Act
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));
        var result = parser.Parse();

        // Assert
        result.Operators.Should().HaveCount(3);
        result.Operators[0].Name.Should().Be("q");
        result.Operators[1].Name.Should().Be("cm");
        result.Operators[2].Name.Should().Be("Q");
    }

    [Fact]
    public void Parse_cm_AppliesTransformationMatrix()
    {
        // Arrange
        var content = "1 0 0 1 100 200 cm";

        // Act
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));
        var result = parser.Parse();

        // Assert
        var op = result.Operators.Single();
        op.Name.Should().Be("cm");
        op.Operands.Should().HaveCount(6);
        // Verify operands: a=1, b=0, c=0, d=1, e=100, f=200
    }

    [Theory]
    [InlineData(1, 0, 0, 1, 50, 50)]    // Translation
    [InlineData(2, 0, 0, 2, 0, 0)]       // Scale
    [InlineData(0, 1, -1, 0, 0, 0)]      // 90° rotation
    public void Render_cm_TransformsSubsequentDrawing(
        double a, double b, double c, double d, double e, double f)
    {
        // Integration test with rendering
    }
}
```

### Example 2: Text Showing Operator Test

```csharp
public class TextShowingOperatorTests
{
    [Fact]
    public void Parse_Tj_ExtractsText()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET";

        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));
        var result = parser.Parse();

        var tjOp = result.Operators.First(o => o.Name == "Tj");
        tjOp.TextContent.Should().Be("Hello World");
    }

    [Fact]
    public void Parse_TJ_HandlesPositioningArray()
    {
        var content = "BT /F1 12 Tf [(H) -50 (ello)] TJ ET";

        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));
        var result = parser.Parse();

        var tjOp = result.Operators.First(o => o.Name == "TJ");
        tjOp.TextContent.Should().Be("Hello");
    }

    [Fact]
    public void Redact_Tj_RemovesTargetText()
    {
        // Create PDF with "Hello World"
        var pdf = TestPdfGenerator.CreatePdfWithText("Hello World", x: 100, y: 700);

        // Redact "World"
        var redactor = new TextRedactor();
        var result = redactor.RedactText(pdf, "World");

        // Verify "World" is removed but "Hello" remains
        var text = PdfTestHelpers.ExtractAllText(result);
        text.Should().NotContain("World");
        text.Should().Contain("Hello");
    }
}
```

### Example 3: Path Operator Round-Trip Test

```csharp
public class PathOperatorRoundTripTests
{
    [Fact]
    public void RoundTrip_Rectangle_PreservesCoordinates()
    {
        // Arrange
        var original = "100 200 50 75 re f";

        // Act: Parse
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(original));
        var ops = parser.Parse();

        // Act: Serialize
        var writer = new ContentStreamWriter();
        var serialized = writer.Write(ops);

        // Act: Parse again
        var parser2 = new ContentStreamParser(Encoding.UTF8.GetBytes(serialized));
        var ops2 = parser2.Parse();

        // Assert: Same operators
        ops2.Operators.Should().HaveCount(ops.Operators.Count);
        for (int i = 0; i < ops.Operators.Count; i++)
        {
            ops2.Operators[i].Name.Should().Be(ops.Operators[i].Name);
            // Compare operands
        }
    }

    [Theory]
    [InlineData("100 200 m 300 400 l S")]           // Line
    [InlineData("0 0 m 100 100 200 0 300 100 c S")] // Bézier
    [InlineData("50 50 100 100 re f")]               // Rectangle
    public void RoundTrip_Path_PreservesStructure(string content)
    {
        var parser1 = new ContentStreamParser(Encoding.UTF8.GetBytes(content));
        var ops1 = parser1.Parse();

        var writer = new ContentStreamWriter();
        var serialized = writer.Write(ops1);

        var parser2 = new ContentStreamParser(Encoding.UTF8.GetBytes(serialized));
        var ops2 = parser2.Parse();

        ops2.Operators.Select(o => o.Name)
            .Should().Equal(ops1.Operators.Select(o => o.Name));
    }
}
```

### Example 4: Rendering Comparison Test

```csharp
public class OperatorRenderingTests
{
    [Fact]
    public void Render_FilledRectangle_MatchesReference()
    {
        // Create PDF with filled rectangle
        var pdf = TestPdfGenerator.CreatePdfWithRectangle(
            x: 100, y: 100, width: 200, height: 150, fill: true);

        // Render to image
        using var renderer = new SkiaRenderer();
        var image = renderer.Render(pdf.Pages[0], scale: 1.0);

        // Compare to reference
        var reference = LoadReferenceImage("filled-rectangle.png");
        ImageComparer.Compare(image, reference).Should().BeGreaterThan(0.99);
    }

    [Theory]
    [InlineData("stroke-only.pdf", "S")]
    [InlineData("fill-only.pdf", "f")]
    [InlineData("fill-stroke.pdf", "B")]
    public void Render_PathPainting_MatchesExpected(string pdfFile, string operator)
    {
        // Load test PDF
        var pdf = PdfDocument.Open($"test-pdfs/operators/painting/{pdfFile}");

        // Render
        using var renderer = new SkiaRenderer();
        var image = renderer.Render(pdf.Pages[0], scale: 1.0);

        // Verify expected visual outcome
        // (implementation depends on image analysis approach)
    }
}
```

---

## Implementation Checklist

### Immediate Actions (This Sprint)

- [ ] Create `Excise.Core.Tests/Content/Operators/` test folder structure
- [ ] Implement `GraphicsStateOperatorTests.cs`
- [ ] Implement `PathOperatorTests.cs` (construction + painting)
- [ ] Implement `TextOperatorTests.cs` (all text operators)
- [ ] Create test PDF generator extensions for operator testing

### Next Sprint

- [ ] Add color operator support to parser
- [ ] Add color operator tests
- [ ] Implement clipping path handling
- [ ] Add inline image parsing

### Future

- [ ] Full color space support
- [ ] Marked content preservation
- [ ] Type 3 font support
- [ ] ExtGState support

---

## Related Issues

- #235 - v2.0 Migration (tracks overall progress)
- #277-281 - Glyph-level redaction features

---

## References

- ISO 32000-2:2020 (PDF 2.0 Specification)
- Adobe PDF Reference 1.7 (legacy, but still relevant)
- veraPDF corpus (test cases for PDF/A compliance)

# Excise Gap Analysis

This document identifies what exists in Excise.Core vs what needs to be implemented for a complete PDF solution.

## Current State Summary

| Component | Status | Notes |
|-----------|--------|-------|
| **Primitives** | ✅ Complete | All 8 PDF object types |
| **Parsing** | ✅ Complete | Lexer, parser, xref, decompression |
| **Document Model** | ✅ Complete | PdfDocument, PdfPage, rectangles |
| **Text Extraction** | ✅ Complete | Letters with positions |
| **Writing** | ✅ Complete | Full document writing |
| **Content Stream Parsing** | ⚠️ Embedded | In TextExtractor, needs extraction |
| **Content Stream Modification** | ❌ Missing | Need ContentOperator/Writer |
| **Rendering** | ⚠️ Partial | Paths only, no text |

---

## Detailed Gap Analysis

### 1. Content Stream Infrastructure (CRITICAL for Redaction)

**Current State:** TextExtractor contains content stream parsing logic but it's embedded within text extraction. There's no way to:
- Parse content stream into operator list
- Filter/modify operators
- Write modified operators back

**Gap:**
```
❌ ContentOperator class - represents a single PDF operator with operands
❌ ContentStream class - collection of operators
❌ ContentStreamParser - parse bytes → ContentStream
❌ ContentStreamWriter - serialize ContentStream → bytes
```

**Required for:** Redaction, any content modification

**Priority:** P0 (Blocking)

---

### 2. Graphics State Tracking (NEEDED for Accurate Operations)

**Current State:** TextExtractor tracks graphics state (CTM, text state) but it's private and tied to text extraction.

**Gap:**
```
⚠️ GraphicsState class exists in Rendering but not in Core
❌ State stack not exposed
❌ Clipping path tracking
❌ Blend mode tracking
❌ ExtGState handling incomplete
```

**Required for:** Accurate redaction, proper rendering

**Priority:** P1 (Important)

---

### 3. Bounding Box Calculation (NEEDED for Redaction)

**Current State:** TextExtractor calculates letter bounds. But there's no way to:
- Get bounds for arbitrary operators (paths, images)
- Track operator-to-position mapping

**Gap:**
```
❌ PathBoundsCalculator - bounds for path operators
❌ ImageBoundsCalculator - bounds for image XObjects
❌ Operator.GetBounds() method
```

**Required for:** Area-based redaction

**Priority:** P1 (Important)

---

### 4. XObject Support (NEEDED for Complete Redaction)

**Current State:** XObjects can be retrieved via `PdfPage.GetXObject()` but there's no:
- Form XObject content parsing
- Image XObject extraction
- XObject modification

**Gap:**
```
❌ FormXObject class - wraps Form XObject with content parsing
❌ ImageXObject class - wraps Image XObject with pixel data
❌ Recursive Form XObject content handling
```

**Required for:** Redacting content in Form XObjects

**Priority:** P2 (Needed)

---

### 5. Annotation Support (NICE TO HAVE)

**Current State:** No annotation handling

**Gap:**
```
❌ Annotation enumeration
❌ Annotation modification
❌ Annotation removal
```

**Required for:** Redacting annotations (links, comments)

**Priority:** P3 (Nice to have)

---

### 6. Font Enhancements (PARTIAL - May Be Sufficient)

**Current State:**
- ToUnicode CMap parsing works
- Standard encodings (WinAnsi, MacRoman) work
- Standard 14 font metrics exist

**Gap:**
```
⚠️ Embedded font metrics - use standard widths as fallback
⚠️ TrueType font parsing - not needed for text extraction
⚠️ Type1 font parsing - not needed for text extraction
❌ CID font width extraction (DW/W arrays) - needed for CJK
```

**Required for:** Accurate CJK text positioning

**Priority:** P2 (Needed for CJK)

---

### 7. Rendering Enhancements (PARTIAL)

**Current State:**
- Path rendering works
- Color handling works
- Text operators are skipped

**Gap:**
```
❌ Text rendering - draw actual text
❌ Image rendering - draw XObject images
❌ Shading/patterns - complex fills
❌ Transparency/blending
```

**Required for:** Full page rendering

**Priority:** P3 (Nice to have initially)

---

## Implementation Plan

### Phase 1: Content Stream Infrastructure (Week 1)

1. **Create `Excise.Core/Content/` namespace**

2. **ContentOperator.cs**
   ```csharp
   public class ContentOperator
   {
       public string Name { get; }              // "Tj", "cm", "re", etc.
       public IReadOnlyList<PdfObject> Operands { get; }
       public PdfRectangle? BoundingBox { get; }  // Calculated bounds

       // Factory methods for common operators
       public static ContentOperator MoveTo(double x, double y);
       public static ContentOperator LineTo(double x, double y);
       public static ContentOperator Rectangle(PdfRectangle rect);
       public static ContentOperator Text(string text);
       // etc.
   }
   ```

3. **ContentStream.cs**
   ```csharp
   public class ContentStream
   {
       public IReadOnlyList<ContentOperator> Operators { get; }

       public ContentStream Filter(Func<ContentOperator, bool> predicate);
       public ContentStream Append(ContentOperator op);
       public ContentStream Append(IEnumerable<ContentOperator> ops);
   }
   ```

4. **ContentStreamParser.cs**
   - Extract parsing logic from TextExtractor
   - Parse all operator types (not just text)
   - Track graphics state for bounds calculation

5. **ContentStreamWriter.cs**
   ```csharp
   public class ContentStreamWriter
   {
       public byte[] Write(ContentStream content);
   }
   ```

### Phase 2: Operator Bounds (Week 2)

1. **GraphicsState.cs** (in Core, not just Rendering)
   - CTM tracking
   - Text state tracking
   - Clipping path tracking

2. **BoundsCalculator.cs**
   - Calculate bounds for text operators
   - Calculate bounds for path operators
   - Transform through CTM

3. **Add BoundingBox property to ContentOperator**

### Phase 3: Excise.Operations.Redaction (Week 3)

1. **TextRedactor.cs**
   - Find text by content
   - Find text by area
   - Remove matching operators from content stream
   - Optionally draw visual marker

2. **AreaRedactor.cs**
   - Remove all operators in area (text, paths, images)
   - Handle Form XObjects

3. **RedactionResult.cs**
   - Report what was removed
   - Report affected pages

### Phase 4: Migration (Week 4)

1. Update CLI to use Excise.Operations
2. Update GUI to use Excise.Operations
3. Remove old Excise.App.Redaction
4. Remove PdfPig/PDFsharp dependencies

---

## Immediate Next Steps

1. **Create `Excise.Core/Content/ContentOperator.cs`**
2. **Create `Excise.Core/Content/ContentStream.cs`**
3. **Create `Excise.Core/Content/ContentStreamParser.cs`** (extract from TextExtractor)
4. **Create `Excise.Core/Content/ContentStreamWriter.cs`**
5. **Add tests for round-trip: parse → filter → write**

Once content stream infrastructure is in place, redaction becomes straightforward:

```csharp
var page = doc.GetPage(1);
var content = page.GetContentStream();

// Filter out operators in redaction area
var redacted = content.Filter(op => !op.IntersectsWith(redactionArea));

// Add black rectangle
redacted = redacted
    .Append(ContentOperator.SetFillColor(0, 0, 0))
    .Append(ContentOperator.Rectangle(redactionArea))
    .Append(ContentOperator.Fill());

page.SetContentStream(redacted);
doc.Save("output.pdf");
```

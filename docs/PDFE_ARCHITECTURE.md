# Pdfe Architecture Design

## Overview

Pdfe is a complete, native .NET PDF solution designed from first principles based on ISO 32000-2:2020 (PDF 2.0 specification). It is **not** influenced by existing libraries like PdfPig, PDFsharp, or iText - it is a clean-room implementation with its own design philosophy.

## Design Principles

1. **Specification-First**: All design decisions derive from ISO 32000 PDF specification
2. **Immutability Where Sensible**: Prefer immutable types for thread safety and predictability
3. **Explicit Over Implicit**: No hidden magic; operations do what they say
4. **Layered Architecture**: Clear separation between primitives, document model, and operations
5. **Minimal Dependencies**: Only SkiaSharp for rendering; everything else is native .NET
6. **Performance-Conscious**: Lazy loading, streaming where appropriate, minimal allocations

## Module Structure

```
Pdfe.Core           - PDF parsing, document model, modification, writing
Pdfe.Rendering      - Page rendering to images (SkiaSharp)
Pdfe.Core/Operations - High-level operations that are implemented in this repo
Pdfe.Cli            - Command-line tools
PdfEditor           - Desktop application (Avalonia)
```

---

## Pdfe.Core Architecture

### Layer 1: Primitives (`Pdfe.Core.Primitives`)

The eight basic PDF object types per ISO 32000-2 Section 7.3:

```
PdfObject (abstract base)
├── PdfNull           - null
├── PdfBoolean        - true/false
├── PdfInteger        - Integer number
├── PdfReal           - Floating-point number
├── PdfString         - Literal or hex string
├── PdfName           - Name object (/Name)
├── PdfArray          - Array [...]
├── PdfDictionary     - Dictionary <<...>>
├── PdfStream         - Dictionary + byte data
└── PdfReference      - Indirect object reference (n g R)
```

**Design Notes:**
- `PdfObject` is the base for all primitive types
- `PdfNumber` is NOT a common base for Integer/Real (unlike some libraries) - they are distinct types per spec
- `PdfStream` extends `PdfDictionary` (streams ARE dictionaries with data)
- References are first-class objects, not just integer pairs

### Layer 2: Parsing (`Pdfe.Core.Parsing`)

```
PdfLexer            - Tokenizes PDF byte stream
PdfParser           - Builds PdfObject tree from tokens
XRefParser          - Parses cross-reference tables/streams
StreamDecompressor  - Decodes filtered streams (FlateDecode, etc.)
```

**Parsing Philosophy:**
- Lexer produces tokens, Parser produces objects
- Objects are lazily resolved (indirect references resolved on demand)
- Stream data decoded on first access, then cached

### Layer 3: Document Model (`Pdfe.Core.Document`)

High-level document abstractions:

```
PdfDocument         - Root document container
├── Catalog         - Document catalog dictionary
├── Pages           - Page tree
├── Info            - Document metadata
└── Trailer         - Trailer dictionary

PdfPage             - Single page
├── MediaBox        - Page boundaries (required)
├── CropBox         - Visible area
├── Resources       - Fonts, XObjects, etc.
├── Contents        - Content stream(s)
├── Annotations     - Page annotations
└── Rotation        - Page rotation

PdfRectangle        - Rectangle (Left, Bottom, Right, Top)
```

**Document Model Philosophy:**
- Document is the entry point for all operations
- Pages are accessed by 1-based index (matches PDF spec)
- Inherited properties (MediaBox, Resources) are resolved automatically
- Content streams are accessed as raw bytes or parsed operators

### Layer 4: Content Stream (`Pdfe.Core.Content`)

PDF content stream operators and parsing:

```
ContentStream       - Parsed content stream
├── Operators       - List of ContentOperator
└── Resources       - Reference to page resources

ContentOperator     - Single PDF operator with operands
├── Name            - Operator name (Tj, cm, re, etc.)
└── Operands        - List of PdfObject operands

ContentStreamParser - Parses byte[] → ContentStream
ContentStreamWriter - Serializes ContentStream → byte[]
```

**Content Categories (per ISO 32000-2 Section 8):**

```
Graphics State      - q, Q, cm, w, J, j, M, d, ri, i, gs
Path Construction   - m, l, c, v, y, h, re
Path Painting       - S, s, f, F, f*, B, B*, b, b*, n
Clipping            - W, W*
Text State          - Tc, Tw, Tz, TL, Tf, Tr, Ts
Text Position       - Td, TD, Tm, T*
Text Showing        - Tj, TJ, ', "
Text Objects        - BT, ET
Color               - CS, cs, SC, SCN, sc, scn, G, g, RG, rg, K, k
Shading             - sh
Images              - BI, ID, EI, Do (XObject)
Marked Content      - MP, DP, BMC, BDC, EMC
```

### Layer 5: Text Extraction (`Pdfe.Core.Text`)

```
TextExtractor       - Extracts text and positions from page
Letter              - Single character with position/font info
TextBlock           - Group of letters forming logical unit
Font                - Font abstraction
├── Type1Font
├── TrueTypeFont
├── Type0Font (CID)
└── Type3Font

ToUnicodeMap        - Character code to Unicode mapping
EncodingMap         - Character code to glyph name mapping
```

**Text Extraction Philosophy:**
- Letters are the atomic unit (not glyphs, not words)
- Position is in PDF coordinates (origin bottom-left)
- Font decoding handled transparently (ToUnicode, Encoding, built-in)

### Layer 6: Fonts (`Pdfe.Core.Fonts`)

```
FontResolver        - Resolves font names to system fonts
FontMetrics         - Character widths, bounding boxes
CMapParser          - Parses CMap files
EncodingParser      - Parses encoding dictionaries
```

### Layer 7: Graphics (`Pdfe.Core.Graphics`)

```
PdfGraphics         - Drawing context for page modification
├── DrawRectangle   - Draw rectangle
├── DrawPath        - Draw arbitrary path
├── DrawText        - Draw text
├── DrawImage       - Draw image
└── SetColor/Stroke - Set graphics state

GraphicsState       - Current graphics state
├── CTM             - Current transformation matrix
├── FillColor       - Current fill color
├── StrokeColor     - Current stroke color
├── LineWidth       - Current line width
└── Font/Size       - Current font state

Matrix              - 2D transformation matrix
```

### Layer 8: Writing (`Pdfe.Core.Writing`)

```
PdfDocumentWriter   - Writes complete PDF document
PdfObjectWriter     - Serializes PdfObject to bytes
XRefWriter          - Writes cross-reference table
StreamCompressor    - Encodes streams (FlateDecode, etc.)
```

**Writing Philosophy:**
- Documents can be written fresh or incrementally updated
- Streams are compressed by default (configurable)
- XRef can be table or stream format

---

## Pdfe.Rendering Architecture

```
PdfRenderer         - Main rendering entry point
├── RenderPage      - Render page to SKBitmap
├── RenderToStream  - Render to image stream
└── Options         - DPI, format, anti-aliasing

RenderContext       - Per-page rendering state
├── Canvas          - SkiaSharp canvas
├── State           - Graphics state stack
└── Resources       - Font/image cache

TextRenderer        - Renders text operators
PathRenderer        - Renders path operators
ImageRenderer       - Renders image XObjects
ShadingRenderer     - Renders shading patterns
```

**Rendering Philosophy:**
- Uses SkiaSharp exclusively (cross-platform, hardware accelerated)
- Text rendering uses system fonts where possible
- Font fallback for embedded fonts
- Supports transparency, blending modes

---

## Operations Architecture

High-level operations are currently implemented inside `Pdfe.Core`, not as a
separate `Pdfe.Operations` assembly:

```
Pdfe.Core/Operations
├── PdfRedaction    - High-level redaction operation builder
├── TextRedactor    - Text redaction helper
└── RedactionResult - Detailed redaction results

Pdfe.Core/Text/Segmentation
├── GlyphRemover          - Glyph-level content removal orchestrator
├── LetterFinder          - Maps extracted letters back to content operations
├── OperationReconstructor - Rebuilds text operations after removal
├── ImageRedactor         - Removes intersecting image operations
└── PdfPage/PdfDocument redaction extension methods

PdfEditor/Services
├── RedactionService                   - GUI redaction orchestration
├── SignatureVerificationService       - ByteRange/CMS signature inspection using BouncyCastle
├── SignatureVerificationWorkflowService - Dialog-oriented signature workflow orchestration
├── SignatureVerificationSummaryFormatter - User-facing signature result text
└── IUserDialogService                 - UI/test boundary for message dialogs
```

PDF encryption reading is implemented in `Pdfe.Core/Security`. Encryption
authoring, PDF/A validation, and PDF signing are not implemented in the current
codebase.

---

## Key Design Decisions

### 1. No "Letter" Type from External Libraries

We define our own `Letter` type that represents exactly what we need:

```csharp
public readonly record struct Letter(
    string Value,           // Unicode character(s)
    PdfRectangle Bounds,    // Bounding box in PDF coords
    double FontSize,        // Point size
    string FontName,        // Font resource name
    int CharacterCode       // Raw character code
);
```

### 2. Content Streams are First-Class

Unlike libraries that hide content streams, we expose them directly:

```csharp
// Get parsed content stream
var content = page.GetContentStream();

// Modify operators
var filtered = content.Operators
    .Where(op => !ShouldRemove(op))
    .ToList();

// Write back
page.SetContentStream(new ContentStream(filtered, content.Resources));
```

### 3. Immutable Coordinates

`PdfRectangle` and similar geometric types are immutable:

```csharp
public readonly record struct PdfRectangle(
    double Left, double Bottom, double Right, double Top)
{
    public double Width => Math.Abs(Right - Left);
    public double Height => Math.Abs(Top - Bottom);
    public PdfRectangle Normalize() => new(
        Math.Min(Left, Right), Math.Min(Bottom, Top),
        Math.Max(Left, Right), Math.Max(Bottom, Top));
}
```

### 4. Explicit Document Modification

Documents are modified explicitly, not through hidden state:

```csharp
using var doc = PdfDocument.Open("input.pdf");

// Modify page content
var page = doc.GetPage(1);
var content = page.GetContentStream();
// ... modify content ...
page.SetContentStream(content);

// Save (required to persist changes)
doc.Save("output.pdf");
```

### 5. No Global State

No singletons, no static configuration. Everything is explicit:

```csharp
var renderer = new PdfRenderer(new RenderOptions { Dpi = 300 });
var redactor = new TextRedactor(new RedactionOptions { DrawMarker = true });
```

---

## API Examples

### Reading a PDF

```csharp
using var doc = PdfDocument.Open("document.pdf");

Console.WriteLine($"Pages: {doc.PageCount}");
Console.WriteLine($"Version: {doc.Version}");

foreach (var page in doc.Pages)
{
    Console.WriteLine($"Page {page.Number}: {page.Width}x{page.Height}");
    Console.WriteLine($"Text: {page.Text}");
}
```

### Extracting Text with Positions

```csharp
var page = doc.GetPage(1);
foreach (var letter in page.Letters)
{
    Console.WriteLine($"'{letter.Value}' at ({letter.Bounds.Left}, {letter.Bounds.Bottom})");
}
```

### Modifying Content

```csharp
var page = doc.GetPage(1);
var content = page.GetContentStream();

// Remove text operators in a region
var filtered = content.Operators
    .Where(op => !IsInRedactionArea(op, redactionArea))
    .ToList();

// Add visual marker
var marker = ContentOperator.Rectangle(redactionArea);
var fill = ContentOperator.SetFillColor(0, 0, 0);
filtered.Add(fill);
filtered.Add(marker);
filtered.Add(ContentOperator.Fill());

page.SetContentStream(new ContentStream(filtered));
doc.Save("redacted.pdf");
```

### Rendering

```csharp
var renderer = new PdfRenderer();
using var doc = PdfDocument.Open("document.pdf");
var page = doc.GetPage(1);

using var bitmap = renderer.RenderPage(page, new RenderOptions { Dpi = 150 });
using var stream = File.Create("page1.png");
bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
```

---

## Current State vs. Target

### What Exists (Pdfe.Core)

✅ Primitives (all 8 PDF object types)
✅ Parsing (lexer, parser, xref)
✅ Stream decompression (FlateDecode)
✅ Document model (PdfDocument, PdfPage)
✅ Text extraction (TextExtractor, Letter)
✅ ToUnicode CMap parsing
✅ Basic encoding support
✅ Document writing
✅ Content stream reading

### What Needs Enhancement

⚠️ Content stream parsing (exists in TextExtractor, needs extraction)
⚠️ Content stream modification (partial - SetContentStreamBytes exists)
⚠️ Font handling (basic - needs enhancement for embedded fonts)
⚠️ Graphics state tracking (exists in TextExtractor, needs extraction)

### What Needs Implementation

❌ ContentStream class (structured operator list)
❌ ContentStreamParser (separate from TextExtractor)
❌ ContentStreamWriter (serialize operators to bytes)
❌ Full graphics state (clipping, blend modes, etc.)
❌ Image extraction
❌ Annotation handling
❌ Form XObject support
❌ Encryption/decryption
❌ Incremental updates

---

## Migration Status

The v2.0 pure-.NET migration is complete for the current application stack:
redaction, rendering, text extraction, form handling, and CLI flows use the
pdfe-owned libraries in this repository. Legacy PdfPig/PDFsharp/PDFtoImage
dependencies have been removed. Remaining gaps should be tracked as GitHub
Issues rather than described here as a planned migration path.

---

## File Organization

```
Pdfe.Core/
├── Primitives/
│   ├── PdfObject.cs
│   ├── PdfNull.cs
│   ├── PdfBoolean.cs
│   ├── PdfInteger.cs
│   ├── PdfReal.cs
│   ├── PdfString.cs
│   ├── PdfName.cs
│   ├── PdfArray.cs
│   ├── PdfDictionary.cs
│   ├── PdfStream.cs
│   └── PdfReference.cs
├── Parsing/
│   ├── PdfLexer.cs
│   ├── PdfParser.cs
│   ├── XRefParser.cs
│   └── StreamDecompressor.cs
├── Document/
│   ├── PdfDocument.cs
│   ├── PdfPage.cs
│   └── PdfRectangle.cs
├── Content/
│   ├── ContentStream.cs
│   ├── ContentOperator.cs
│   ├── ContentStreamParser.cs
│   └── ContentStreamWriter.cs
├── Text/
│   ├── TextExtractor.cs
│   ├── Letter.cs
│   └── ToUnicodeCMapParser.cs
├── Fonts/
│   ├── Font.cs
│   ├── FontMetrics.cs
│   └── EncodingMap.cs
├── Graphics/
│   ├── PdfGraphics.cs
│   ├── GraphicsState.cs
│   ├── Matrix.cs
│   ├── PdfColor.cs
│   ├── PdfPen.cs
│   └── PdfBrush.cs
└── Writing/
    ├── PdfDocumentWriter.cs
    └── PdfObjectWriter.cs
```

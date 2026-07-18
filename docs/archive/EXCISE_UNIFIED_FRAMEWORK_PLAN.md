# excise Unified PDF Framework Plan

This document outlines the design for a from-scratch unified PDF framework that replaces PdfPig, PDFsharp, and PDFtoImage with excise-owned code.

## Vision

Create a complete, pure .NET PDF framework under the `Excise.Core` namespace that provides:
- **Reading**: Parse PDF structure, extract text with positions
- **Writing**: Modify and save PDFs
- **Rendering**: Render pages to images using SkiaSharp
- **Redaction**: Glyph-level content removal (existing `Excise.App.Redaction`)

All code will be excise-owned, with permissive licensing (MIT). External dependencies limited to:
- **SkiaSharp** - 2D graphics rendering
- **Clipper2** - Polygon boolean operations (for partial shape redaction)
- **System.Text.Encoding.CodePages** - Extended text encodings

## Current Dependencies to Replace

| Library | Version | Purpose | Replacement Module |
|---------|---------|---------|-------------------|
| PdfPig | 0.1.11 | Text extraction, letter positions | `Excise.Core.Reading` |
| PDFsharp | 6.2.2 | PDF modification, saving | `Excise.Core.Writing` |
| PDFtoImage | 4.0.2 | PDF rendering (wraps PDFium) | `Excise.Core.Rendering` |

## Proposed Library Structure

```
Excise.Core/                          # Core PDF library (new)
├── Primitives/                     # PDF primitive types
│   ├── PdfObject.cs               # Base object (number, string, name, array, dict, stream)
│   ├── PdfDictionary.cs           # Dictionary type
│   ├── PdfArray.cs                # Array type
│   ├── PdfStream.cs               # Stream with data
│   ├── PdfName.cs                 # /Name tokens
│   ├── PdfString.cs               # String types
│   └── PdfReference.cs            # Indirect object references
│
├── Parsing/                        # PDF file parsing
│   ├── PdfLexer.cs                # Tokenizer
│   ├── PdfParser.cs               # Object parser
│   ├── XRefParser.cs              # Cross-reference table
│   ├── StreamDecompressor.cs      # FlateDecode, etc.
│   └── PdfDocumentReader.cs       # High-level document reader
│
├── Document/                       # Document structure
│   ├── PdfDocument.cs             # Main document class
│   ├── PdfPage.cs                 # Page object
│   ├── PdfPageTree.cs             # Page tree traversal
│   ├── PdfResources.cs            # Page resources
│   └── PdfCatalog.cs              # Document catalog
│
├── Content/                        # Content stream handling
│   ├── ContentStreamReader.cs     # Parse operators
│   ├── ContentStreamWriter.cs     # Generate operators
│   ├── Operators/                 # Operator handlers
│   │   ├── TextOperators.cs       # Tj, TJ, ', "
│   │   ├── StateOperators.cs      # q, Q, cm, gs
│   │   ├── PathOperators.cs       # m, l, c, re, etc.
│   │   └── DrawingOperators.cs    # S, f, B, Do
│   └── GraphicsState.cs           # State machine
│
├── Text/                           # Text extraction
│   ├── TextExtractor.cs           # High-level text API
│   ├── LetterExtractor.cs         # Letter-level with positions
│   ├── WordBuilder.cs             # Word assembly
│   └── ReadingOrderSorter.cs      # Visual order sorting
│
├── Fonts/                          # Font handling
│   ├── FontResolver.cs            # Font lookup
│   ├── FontParser.cs              # Font dictionary parsing
│   ├── EncodingResolver.cs        # Character encoding
│   ├── CMapParser.cs              # CMap handling
│   ├── ToUnicodeParser.cs         # ToUnicode mappings
│   ├── Type1Parser.cs             # Type1 fonts
│   ├── TrueTypeParser.cs          # TrueType/OpenType
│   └── CidFontParser.cs           # CID fonts (CJK)
│
├── Writing/                        # PDF modification
│   ├── PdfDocumentWriter.cs       # Save documents
│   ├── ObjectWriter.cs            # Serialize objects
│   ├── StreamCompressor.cs        # Compress streams
│   ├── XRefWriter.cs              # Write xref table
│   └── IncrementalWriter.cs       # Incremental updates
│
├── Geometry/                       # Geometric types
│   ├── PdfRectangle.cs            # Rectangle (already exists)
│   ├── PdfPoint.cs                # Point
│   ├── PdfMatrix.cs               # 2D transformation matrix
│   └── CoordinateConverter.cs     # Coordinate system utils
│
└── Encryption/                     # PDF security
    ├── PdfDecryptor.cs            # Decrypt protected PDFs
    ├── PdfEncryptor.cs            # Encrypt PDFs
    ├── RC4Handler.cs              # RC4 encryption
    └── AESHandler.cs              # AES encryption

Excise.Rendering/                     # PDF rendering (new)
├── IPdfRenderer.cs                # Renderer interface
├── SkiaRenderer.cs                # SkiaSharp implementation
├── PageRenderer.cs                # Page to bitmap
├── Graphics/
│   ├── PathRenderer.cs            # Path painting
│   ├── TextRenderer.cs            # Text rendering
│   ├── ImageRenderer.cs           # Image rendering
│   └── ColorSpaceHandler.cs       # Color handling
├── Fonts/
│   ├── FontCache.cs               # Font caching
│   └── GlyphRenderer.cs           # Glyph rasterization
└── Output/
    ├── BitmapOutput.cs            # To SKBitmap
    └── SvgOutput.cs               # To SVG (optional)

Excise.App.Redaction/                # Existing redaction library (keep)
├── (existing code)                # Already excise-owned
└── (migrate to use Excise.Core)     # Update imports

Excise.App/                          # GUI application
└── (migrate to use Excise.*)        # Update imports
```

## API Design

### Unified Document API

```csharp
namespace Excise.Core;

// Main document class - combines read and write capabilities
public class PdfDocument : IDisposable
{
    // Opening documents
    public static PdfDocument Open(string path, PdfOpenOptions? options = null);
    public static PdfDocument Open(Stream stream, PdfOpenOptions? options = null);
    public static PdfDocument Create();

    // Properties
    public int PageCount { get; }
    public PdfVersion Version { get; set; }
    public PdfMetadata Metadata { get; }
    public bool IsEncrypted { get; }
    public bool IsModified { get; }

    // Page access
    public PdfPage GetPage(int pageNumber);  // 1-based
    public IEnumerable<PdfPage> Pages { get; }

    // Modification
    public PdfPage AddPage(PdfPageSize size = default);
    public void InsertPage(int index, PdfPage page);
    public void RemovePage(int pageNumber);
    public void MovePage(int from, int to);

    // Saving
    public void Save();
    public void Save(string path);
    public void Save(Stream stream);
    public byte[] SaveToBytes();
}

// Page class
public class PdfPage
{
    public int PageNumber { get; }
    public double Width { get; }
    public double Height { get; }
    public int Rotation { get; set; }
    public PdfRectangle MediaBox { get; }
    public PdfRectangle CropBox { get; }

    // Text extraction
    public string Text { get; }
    public IReadOnlyList<Letter> Letters { get; }
    public IReadOnlyList<Word> Words { get; }

    // Content stream access
    public byte[] GetContentStreamBytes();
    public void SetContentStreamBytes(byte[] content);
    public IReadOnlyList<PdfOperation> GetOperations();

    // Resources
    public PdfResources Resources { get; }

    // Drawing (for adding content)
    public PdfGraphics GetGraphics();
}

// Letter with position (replaces PdfPig.Content.Letter)
public readonly struct Letter
{
    public char Value { get; }
    public string GlyphName { get; }
    public PdfRectangle BoundingBox { get; }
    public PdfPoint Location { get; }
    public double Width { get; }
    public double FontSize { get; }
    public string FontName { get; }
    public int TextRenderingMode { get; }
}

// Graphics context (replaces XGraphics)
public class PdfGraphics : IDisposable
{
    // State
    public void SaveState();
    public void RestoreState();

    // Transformations
    public void SetMatrix(PdfMatrix matrix);
    public void Translate(double dx, double dy);
    public void Scale(double sx, double sy);
    public void Rotate(double degrees);

    // Drawing
    public void DrawRectangle(PdfRectangle rect, PdfPen? pen, PdfBrush? fill);
    public void DrawLine(PdfPoint p1, PdfPoint p2, PdfPen pen);
    public void DrawPath(PdfPath path, PdfPen? pen, PdfBrush? fill);
    public void DrawString(string text, PdfFont font, PdfBrush brush, PdfPoint location);
    public void DrawImage(PdfImage image, PdfRectangle bounds);

    // Clipping
    public void SetClip(PdfPath clipPath);
}
```

### Rendering API

```csharp
namespace Excise.Rendering;

public interface IPdfRenderer
{
    SKBitmap RenderPage(PdfPage page, RenderOptions options);
    Task<SKBitmap> RenderPageAsync(PdfPage page, RenderOptions options);
}

public record RenderOptions
{
    public int Dpi { get; init; } = 150;
    public SKColor BackgroundColor { get; init; } = SKColors.White;
    public bool AntiAlias { get; init; } = true;
    public PdfRectangle? ClipRect { get; init; }
}

public class SkiaRenderer : IPdfRenderer
{
    public SKBitmap RenderPage(PdfPage page, RenderOptions options);
}
```

### Redaction API (Updated)

```csharp
namespace Excise.App.Redaction;

public interface ITextRedactor
{
    // File-based API
    RedactionResult RedactText(string inputPath, string outputPath, string textToRedact);
    RedactionResult RedactAreas(string inputPath, string outputPath, IEnumerable<PageArea> areas);

    // Page-based API (uses Excise.Core.PdfPage)
    RedactionResult RedactPage(PdfPage page, IEnumerable<PdfRectangle> areas,
                               IReadOnlyList<Letter>? letters = null);
}
```

## Implementation Phases

### Phase 1: Core Primitives & Parsing (Foundation)
**Effort: Large | Priority: Critical**

Create the fundamental PDF object model and parser:
- PDF primitive types (PdfObject hierarchy)
- Lexer and parser for PDF syntax
- Cross-reference table handling
- Stream decompression (FlateDecode)
- Basic document structure (catalog, page tree)

**Deliverable**: Can open and read PDF structure
**Test**: Parse 100 PDFs from veraPDF corpus

### Phase 2: Text Extraction
**Effort: Large | Priority: Critical**

Implement text extraction with letter positions:
- Font dictionary parsing
- Character encoding (WinAnsi, MacRoman, Identity)
- CMap and ToUnicode parsing
- Letter position calculation
- Word building and reading order

**Deliverable**: `page.Letters` API with bounding boxes
**Test**: Match PdfPig output on test corpus

### Phase 3: Document Writing
**Effort: Large | Priority: Critical**

Implement PDF modification and saving:
- Object serialization
- Stream compression
- XRef table generation
- Incremental updates
- PDF version handling

**Deliverable**: Open → modify → save works
**Test**: Round-trip 100 PDFs, verify with veraPDF

### Phase 4: Graphics API
**Effort: Medium | Priority: High**

Implement drawing operations:
- PdfGraphics context
- Path construction
- Rectangle/line drawing
- Color handling
- State management (q/Q)

**Deliverable**: `page.GetGraphics()` API
**Test**: Draw redaction boxes, verify render

### Phase 5: Rendering Engine
**Effort: Large | Priority: High**

Implement SkiaSharp-based renderer:
- Path rendering
- Text rendering with fonts
- Image rendering
- Color space conversion
- Transparency handling

**Deliverable**: `renderer.RenderPage()` matches PDFium quality
**Test**: Visual comparison with PDFtoImage output

### Phase 6: Migration
**Effort: Medium | Priority: High**

Migrate existing code to use Excise.Core:
- Update Excise.App.Redaction imports
- Update Excise.App GUI imports
- Update CLI tool imports
- Remove old dependencies
- Update tests

**Deliverable**: Zero PdfPig/PDFsharp imports remaining
**Test**: All existing tests pass

### Phase 7: CJK & Advanced Fonts
**Effort: Medium | Priority: Medium**

Complete font support:
- CID fonts
- Type0 composite fonts
- Full CJK character support
- Embedded font extraction
- Font substitution

**Deliverable**: Full CJK document support
**Test**: CJK test cases from corpus

### Phase 8: Encryption
**Effort: Medium | Priority: Low**

PDF security:
- RC4 decryption (PDF 1.4)
- AES-128 decryption (PDF 1.5)
- AES-256 decryption (PDF 2.0)
- Password handling
- Permission enforcement

**Deliverable**: Open password-protected PDFs
**Test**: Encrypted PDF test cases

## Code Borrowing Strategy

### From PdfPig (Apache 2.0)
- **CMap parsing logic** - Complex, well-tested
- **ToUnicode parsing** - Character mapping
- **Font metric calculations** - Glyph positioning
- **Encoding tables** - WinAnsi, MacRoman, etc.

### From PDFsharp (MIT)
- **Object serialization format** - PDF syntax generation
- **XRef writing logic** - Cross-reference table format
- **Stream compression** - Deflate wrapper

### Original Implementation
- **Document model** - Clean, unified API design
- **Page content stream** - Already have parser in Redaction
- **Rendering engine** - SkiaSharp integration
- **Coordinate handling** - Simplified, consistent

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Font rendering quality | High | Extensive testing, fallback fonts |
| Complex PDF compatibility | High | Iterative testing against corpus |
| Performance regression | Medium | Benchmark against current implementation |
| CJK character handling | Medium | Borrow proven CMap code from PdfPig |
| Encrypted PDF edge cases | Low | Focus on common encryption types |

## Success Criteria

1. **100% test pass rate** - All existing tests pass with new libraries
2. **Visual fidelity** - Rendered output matches PDFium quality
3. **Text accuracy** - Letter positions match PdfPig within 0.5pt
4. **Performance** - No more than 20% slower than current
5. **Zero external PDF dependencies** - Only SkiaSharp, Clipper2

## Estimated Effort

| Phase | Effort | Complexity |
|-------|--------|------------|
| Phase 1: Core Primitives | 3-4 weeks | High |
| Phase 2: Text Extraction | 3-4 weeks | Very High |
| Phase 3: Document Writing | 2-3 weeks | High |
| Phase 4: Graphics API | 1-2 weeks | Medium |
| Phase 5: Rendering Engine | 4-6 weeks | Very High |
| Phase 6: Migration | 1-2 weeks | Medium |
| Phase 7: CJK Support | 2-3 weeks | High |
| Phase 8: Encryption | 1-2 weeks | Medium |

**Total: 17-26 weeks** (4-6 months)

## Alternative: Phased Replacement

Instead of building everything first, replace incrementally:

1. **Keep PdfPig for reading** temporarily
2. **Build Excise.Core.Writing** to replace PDFsharp
3. **Build Excise.Rendering** to replace PDFtoImage
4. **Build Excise.Core.Reading** last (most complex)

This allows shipping improvements earlier while maintaining stability.

---

*Created: 2026-01-01*
*Status: Planning*

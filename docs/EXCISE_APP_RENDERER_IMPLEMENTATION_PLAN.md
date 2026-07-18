# Excise.App.Renderer Implementation Plan

A comprehensive plan for developing a pure .NET PDF rendering library for excise, eliminating native dependencies.

## Executive Summary

**Goal**: Create `Excise.App.Renderer`, a new library that provides PDF page rendering using PdfPig.Rendering.Skia, replacing the current PDFium-based rendering.

**Key Benefits**:
1. **Unified coordinate system** - Same PDF parser for rendering AND text extraction
2. **No native dependencies** - Pure .NET, simpler deployment
3. **Coordinate consistency** - Eliminates mismatch between what users see and what gets redacted
4. **Debuggability** - All .NET code, can step through everything

**Primary Risk**: Rendering quality may be lower than PDFium for complex PDFs.

---

## Part 1: Current Architecture Analysis

### Current PDF Libraries in excise

| Library | Purpose | Type |
|---------|---------|------|
| **PDFium** (via PDFtoImage 4.0.2) | Renders PDF pages to images | Native C++ |
| **PdfPig** (0.1.11) | Text extraction, letter positions | Pure .NET |
| **PDFsharp** (6.2.2) | PDF structure modification | Pure .NET |

### Current Rendering Flow

```
PdfRenderService.cs
       │
       ▼
  PDFtoImage.Conversion.ToImage()
       │
       ▼
    PDFium (native)
       │
       ▼
   SKBitmap output
```

### Key Methods to Replace

From `Excise.App/Services/PdfRenderService.cs`:

```csharp
// Main rendering method - line 105
public async Task<SKBitmap?> RenderPageAsync(string pdfPath, int pageIndex, int dpi = 150)

// Stream-based rendering - line 156
public async Task<SKBitmap?> RenderPageFromStreamAsync(Stream pdfStream, int pageIndex, int dpi = 150)

// Thumbnails - line 201
public async Task<SKBitmap?> RenderThumbnailAsync(string pdfPath, int pageIndex, int width = 200)

// Dimensions - line 213
public (double Width, double Height) GetPageDimensions(string pdfPath, int pageIndex)
```

---

## Part 2: Library Architecture

### 2.1 Project Structure

```
Excise.App.Renderer/
├── Excise.App.Renderer.csproj
├── README.md
├── VERSION.md
│
├── Core/
│   ├── IPdfRenderer.cs              # Main abstraction
│   ├── RenderOptions.cs             # DPI, background color, etc.
│   ├── RenderResult.cs              # Result with metadata
│   └── PageDimensions.cs            # Width/height in points
│
├── Renderers/
│   ├── PdfPigRenderer.cs            # PdfPig.Rendering.Skia implementation
│   ├── PdfiumRenderer.cs            # Existing PDFtoImage wrapper (fallback)
│   └── HybridRenderer.cs            # Try PdfPig, fallback to PDFium
│
├── Caching/
│   ├── IRenderCache.cs              # Cache abstraction
│   ├── MemoryRenderCache.cs         # In-memory LRU cache
│   └── CacheStatistics.cs           # Hits, misses, memory usage
│
├── Diagnostics/
│   ├── RenderComparison.cs          # Compare two renderers
│   ├── QualityMetrics.cs            # SSIM, PSNR calculations
│   └── RenderBenchmark.cs           # Performance measurement
│
└── Extensions/
    └── PdfDocumentExtensions.cs     # Helper extensions
```

### 2.2 Core Interface Design

```csharp
namespace Excise.App.Renderer.Core;

/// <summary>
/// Abstraction for PDF rendering implementations.
/// </summary>
public interface IPdfRenderer : IDisposable
{
    /// <summary>
    /// Render a PDF page to an SKBitmap.
    /// </summary>
    Task<RenderResult> RenderPageAsync(
        string pdfPath,
        int pageIndex,
        RenderOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Render a PDF page from a stream.
    /// </summary>
    Task<RenderResult> RenderPageFromStreamAsync(
        Stream pdfStream,
        int pageIndex,
        RenderOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get page dimensions without full rendering.
    /// </summary>
    PageDimensions GetPageDimensions(string pdfPath, int pageIndex);

    /// <summary>
    /// Get page dimensions from a stream.
    /// </summary>
    PageDimensions GetPageDimensionsFromStream(Stream pdfStream, int pageIndex);

    /// <summary>
    /// Renderer name for diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether this renderer uses native code.
    /// </summary>
    bool IsNative { get; }
}

public record RenderOptions(
    int Dpi = 150,
    SKColor? BackgroundColor = null,
    bool UseAntialiasing = true,
    float Scale = 1.0f
);

public record RenderResult(
    SKBitmap? Bitmap,
    TimeSpan RenderTime,
    string RendererUsed,
    bool FromCache = false
);

public record PageDimensions(
    double WidthPoints,
    double HeightPoints,
    double WidthPixels,
    double HeightPixels,
    int Dpi
);
```

### 2.3 Project File (csproj)

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>0.1.0-alpha</Version>
    <Authors>Marc Jones</Authors>
    <Description>Pure .NET PDF rendering library using PdfPig.Rendering.Skia</Description>
    <PackageTags>pdf;rendering;skia;pdfpig</PackageTags>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Excise.App.Renderer.Tests" />
  </ItemGroup>

  <ItemGroup>
    <!-- Pure .NET PDF rendering -->
    <PackageReference Include="PdfPig" Version="0.1.11" />
    <PackageReference Include="PdfPig.Rendering.Skia" Version="0.1.12.2" />

    <!-- Graphics -->
    <PackageReference Include="SkiaSharp" Version="2.88.8" />

    <!-- Fallback renderer (optional) -->
    <PackageReference Include="PDFtoImage" Version="4.0.2" />

    <!-- Logging -->
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2" />
  </ItemGroup>

</Project>
```

---

## Part 3: Implementation Details

### 3.1 PdfPigRenderer Implementation

```csharp
namespace Excise.App.Renderer.Renderers;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;
using SkiaSharp;

public class PdfPigRenderer : IPdfRenderer
{
    private readonly ILogger<PdfPigRenderer> _logger;

    public string Name => "PdfPig.Rendering.Skia";
    public bool IsNative => false;

    public async Task<RenderResult> RenderPageAsync(
        string pdfPath,
        int pageIndex,
        RenderOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        return await Task.Run(() =>
        {
            try
            {
                using var document = PdfDocument.Open(pdfPath, SkiaRenderingParsingOptions.Instance);
                document.AddSkiaPageFactory();

                // PdfPig uses 1-based page numbers
                int pdfPigPageNumber = pageIndex + 1;

                // Calculate scale from DPI (72 = standard PDF points)
                float scale = options.Dpi / 72.0f * options.Scale;

                var backgroundColor = options.BackgroundColor ?? SKColors.White;

                var bitmap = document.GetPageAsSKBitmap(
                    pdfPigPageNumber,
                    scale,
                    new RGBColor(backgroundColor.Red, backgroundColor.Green, backgroundColor.Blue));

                stopwatch.Stop();

                return new RenderResult(
                    Bitmap: bitmap,
                    RenderTime: stopwatch.Elapsed,
                    RendererUsed: Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PdfPig rendering failed for page {Page}", pageIndex);
                stopwatch.Stop();
                return new RenderResult(null, stopwatch.Elapsed, Name);
            }
        }, cancellationToken);
    }

    public PageDimensions GetPageDimensions(string pdfPath, int pageIndex)
    {
        using var document = PdfDocument.Open(pdfPath);
        var page = document.GetPage(pageIndex + 1); // 1-based

        return new PageDimensions(
            WidthPoints: page.Width,
            HeightPoints: page.Height,
            WidthPixels: page.Width,  // At 72 DPI, points = pixels
            HeightPixels: page.Height,
            Dpi: 72);
    }

    public void Dispose() { /* No unmanaged resources */ }
}
```

### 3.2 HybridRenderer (Primary Strategy)

```csharp
namespace Excise.App.Renderer.Renderers;

/// <summary>
/// Tries PdfPig first, falls back to PDFium on failure.
/// Logs which renderer was used for monitoring adoption.
/// </summary>
public class HybridRenderer : IPdfRenderer
{
    private readonly PdfPigRenderer _pdfPigRenderer;
    private readonly PdfiumRenderer _pdfiumRenderer;
    private readonly ILogger<HybridRenderer> _logger;
    private readonly bool _preferPdfPig;

    // Statistics
    private long _pdfPigSuccesses;
    private long _pdfPigFailures;
    private long _pdfiumFallbacks;

    public string Name => "Hybrid (PdfPig + PDFium fallback)";
    public bool IsNative => true; // Has native fallback

    public async Task<RenderResult> RenderPageAsync(
        string pdfPath,
        int pageIndex,
        RenderOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_preferPdfPig)
        {
            var result = await _pdfPigRenderer.RenderPageAsync(pdfPath, pageIndex, options, cancellationToken);

            if (result.Bitmap != null)
            {
                Interlocked.Increment(ref _pdfPigSuccesses);
                return result;
            }

            // PdfPig failed, fall back to PDFium
            Interlocked.Increment(ref _pdfPigFailures);
            _logger.LogWarning("PdfPig failed for {File} page {Page}, falling back to PDFium",
                Path.GetFileName(pdfPath), pageIndex);
        }

        Interlocked.Increment(ref _pdfiumFallbacks);
        return await _pdfiumRenderer.RenderPageAsync(pdfPath, pageIndex, options, cancellationToken);
    }

    public RendererStatistics GetStatistics() => new(
        PdfPigSuccesses: _pdfPigSuccesses,
        PdfPigFailures: _pdfPigFailures,
        PdfiumFallbacks: _pdfiumFallbacks,
        PdfPigSuccessRate: _pdfPigSuccesses + _pdfPigFailures > 0
            ? (double)_pdfPigSuccesses / (_pdfPigSuccesses + _pdfPigFailures)
            : 0);
}
```

### 3.3 Cache Integration

The caching logic from current `PdfRenderService.cs` should be extracted to a separate `IRenderCache` implementation:

```csharp
namespace Excise.App.Renderer.Caching;

public interface IRenderCache
{
    bool TryGet(string key, out byte[]? pngData);
    void Add(string key, byte[] pngData);
    void Clear();
    CacheStatistics GetStatistics();
}

public class MemoryRenderCache : IRenderCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;

    // Migrate existing cache logic from PdfRenderService.cs
}
```

---

## Part 4: Feature Parity Analysis

### 4.1 PDFium Capabilities (Current)

| Feature | PDFium Status |
|---------|---------------|
| Basic text rendering | ✅ Excellent |
| Vector graphics | ✅ Excellent |
| Embedded images (JPEG, PNG) | ✅ Excellent |
| Transparency/blending | ✅ Excellent |
| Type1/TrueType/OpenType fonts | ✅ Excellent |
| CJK fonts | ✅ Excellent |
| Form XObjects | ✅ Excellent |
| Patterns (tiling, shading) | ✅ Excellent |
| Annotations | ✅ Most supported |
| PDF 2.0 features | ⚠️ Partial |

### 4.2 PdfPig.Rendering.Skia Capabilities

| Feature | PdfPig.Skia Status | Notes |
|---------|-------------------|-------|
| Basic text rendering | ✅ Good | Based on PDFBox port |
| Vector graphics | ✅ Good | SkiaSharp handles well |
| Embedded images | ⚠️ Partial | Some codecs may fail |
| Transparency/blending | ⚠️ Unknown | Needs testing |
| Standard fonts | ✅ Good | |
| CJK fonts | ⚠️ Unknown | Needs testing |
| Form XObjects | ⚠️ Unknown | Needs testing |
| Patterns | ⚠️ Unknown | Needs testing |
| Annotations | ❌ Limited | Not a focus |
| PDF 2.0 | ❌ Limited | Early version |

### 4.3 Gap Mitigation Strategy

1. **Hybrid Approach**: Use PdfPig for most PDFs, fall back to PDFium when quality is insufficient
2. **Quality Detection**: Implement automatic quality assessment (render both, compare SSIM)
3. **Manual Override**: Allow users to force PDFium for specific documents
4. **Progressive Rollout**: Start with PdfPig off by default, enable via configuration

---

## Part 5: Testing Strategy

### 5.1 Test Project Structure

```
Excise.App.Renderer.Tests/
├── Unit/
│   ├── RenderOptionsTests.cs
│   ├── CacheTests.cs
│   └── PageDimensionsTests.cs
│
├── Integration/
│   ├── PdfPigRendererTests.cs
│   ├── PdfiumRendererTests.cs
│   ├── HybridRendererTests.cs
│   └── RealWorldPdfTests.cs
│
├── Comparison/
│   ├── VisualComparisonTests.cs       # Side-by-side rendering
│   ├── CoordinateConsistencyTests.cs  # Critical for redaction
│   └── QualityMetricsTests.cs         # SSIM, PSNR scores
│
├── Performance/
│   ├── RenderBenchmarkTests.cs
│   ├── MemoryUsageTests.cs
│   └── ThroughputTests.cs
│
├── Corpus/
│   ├── VeraPdfCorpusRenderTests.cs    # Test against veraPDF corpus
│   └── RealWorldDocumentTests.cs       # Problematic PDFs from issues
│
└── TestUtilities/
    ├── PdfTestGenerator.cs
    ├── ImageComparison.cs
    └── TestPdfResources.cs
```

### 5.2 Critical Test: Coordinate Consistency

This test verifies the main benefit - unified coordinates:

```csharp
[Fact]
public void CoordinateConsistency_RenderedTextMatchesExtractedPositions()
{
    // Arrange
    var testPdf = TestPdfGenerator.CreateSimpleTextPdf("Hello World");
    var renderer = new PdfPigRenderer();

    // Act - Get letter positions from PdfPig
    using var doc = PdfDocument.Open(testPdf);
    var page = doc.GetPage(1);
    var letters = page.Letters.ToList();
    var hLetter = letters.First(l => l.Value == "H");

    // Render the page
    var result = await renderer.RenderPageAsync(testPdf, 0, new RenderOptions(Dpi: 150));

    // Calculate expected pixel position
    double scale = 150.0 / 72.0;
    int expectedX = (int)(hLetter.Location.X * scale);
    int expectedY = (int)((page.Height - hLetter.Location.Y) * scale); // Flip Y

    // Assert - Sample pixels at expected position should be text color (black)
    var pixel = result.Bitmap!.GetPixel(expectedX, expectedY);
    pixel.Red.Should().BeLessThan(100,
        "Letter 'H' should be rendered at extracted coordinates");
}
```

### 5.3 Visual Comparison Test

```csharp
[Fact]
public void VisualComparison_PdfPigVsPdfium_SimilarOutput()
{
    // Render same page with both renderers
    var pdfPigResult = await _pdfPigRenderer.RenderPageAsync(testPdf, 0, options);
    var pdfiumResult = await _pdfiumRenderer.RenderPageAsync(testPdf, 0, options);

    // Calculate SSIM (Structural Similarity Index)
    var ssim = ImageComparison.CalculateSSIM(pdfPigResult.Bitmap!, pdfiumResult.Bitmap!);

    // SSIM of 0.95+ indicates very similar images
    ssim.Should().BeGreaterThan(0.90,
        $"PdfPig rendering should be similar to PDFium (SSIM: {ssim:F3})");

    // Save comparison image for manual review
    SaveComparisonImage(pdfPigResult.Bitmap!, pdfiumResult.Bitmap!, "comparison.png");
}
```

### 5.4 Performance Benchmark

```csharp
[Fact]
public void Performance_PdfPigVsPdfium_Benchmark()
{
    var testPdfs = GetBenchmarkPdfs(); // Various sizes/complexities

    foreach (var pdf in testPdfs)
    {
        // Warm up
        await _pdfPigRenderer.RenderPageAsync(pdf, 0, options);
        await _pdfiumRenderer.RenderPageAsync(pdf, 0, options);

        // Measure
        var pdfPigTimes = new List<long>();
        var pdfiumTimes = new List<long>();

        for (int i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            await _pdfPigRenderer.RenderPageAsync(pdf, 0, options);
            pdfPigTimes.Add(sw.ElapsedMilliseconds);

            sw.Restart();
            await _pdfiumRenderer.RenderPageAsync(pdf, 0, options);
            pdfiumTimes.Add(sw.ElapsedMilliseconds);
        }

        _output.WriteLine($"{Path.GetFileName(pdf)}:");
        _output.WriteLine($"  PdfPig: {pdfPigTimes.Average():F1}ms avg");
        _output.WriteLine($"  PDFium: {pdfiumTimes.Average():F1}ms avg");
        _output.WriteLine($"  Ratio:  {pdfPigTimes.Average() / pdfiumTimes.Average():F2}x");
    }
}
```

---

## Part 6: Migration Path

### 6.1 Integration with Existing Code

Modify `Excise.App/Services/PdfRenderService.cs` to use the new library:

```csharp
public class PdfRenderService
{
    private readonly IPdfRenderer _renderer;
    private readonly IRenderCache _cache;

    public PdfRenderService(
        ILogger<PdfRenderService> logger,
        IPdfRenderer? renderer = null,
        IRenderCache? cache = null)
    {
        _logger = logger;
        _renderer = renderer ?? CreateDefaultRenderer();
        _cache = cache ?? new MemoryRenderCache();
    }

    private IPdfRenderer CreateDefaultRenderer()
    {
        // Configuration-driven renderer selection
        var usePdfPig = Environment.GetEnvironmentVariable("EXCISE_USE_PDFPIG") == "1";

        if (usePdfPig)
            return new HybridRenderer(_logger); // PdfPig + fallback
        else
            return new PdfiumRenderer(_logger); // Current behavior
    }

    public async Task<SKBitmap?> RenderPageAsync(string pdfPath, int pageIndex, int dpi = 150)
    {
        var cacheKey = BuildCacheKey(pdfPath, pageIndex, dpi);

        if (_cache.TryGet(cacheKey, out var cachedData))
        {
            using var stream = new MemoryStream(cachedData!);
            return SKBitmap.Decode(stream);
        }

        var result = await _renderer.RenderPageAsync(
            pdfPath,
            pageIndex,
            new RenderOptions(Dpi: dpi));

        if (result.Bitmap != null)
        {
            CacheResult(cacheKey, result.Bitmap);
        }

        return result.Bitmap;
    }
}
```

### 6.2 Configuration Options

```csharp
public class RenderConfiguration
{
    /// <summary>
    /// Which renderer to use: "pdfium", "pdfpig", "hybrid", or "auto"
    /// </summary>
    public string Renderer { get; set; } = "pdfium"; // Safe default

    /// <summary>
    /// For hybrid mode: prefer PdfPig when true, PDFium when false
    /// </summary>
    public bool PreferPdfPig { get; set; } = false;

    /// <summary>
    /// Minimum SSIM score to accept PdfPig rendering (0.0 - 1.0)
    /// Below this, fall back to PDFium
    /// </summary>
    public double MinimumQualityThreshold { get; set; } = 0.85;

    /// <summary>
    /// Log which renderer was used for each page
    /// </summary>
    public bool LogRendererUsage { get; set; } = true;
}
```

### 6.3 Rollout Phases

**Phase 1: Library Creation** (Issues #217-220)
- Create Excise.App.Renderer project
- Implement IPdfRenderer interface
- Implement PdfPigRenderer and PdfiumRenderer
- Add basic tests

**Phase 2: Integration** (Issues #221-223)
- Integrate with PdfRenderService
- Add configuration options
- Add HybridRenderer
- Maintain 100% backward compatibility

**Phase 3: Testing & Validation** (Issues #224-227)
- Visual comparison tests
- Coordinate consistency tests
- Performance benchmarks
- VeraPDF corpus testing

**Phase 4: Gradual Rollout** (Issues #228-230)
- Add "experimental renderer" option in UI
- Collect user feedback
- Monitor failure rates
- Expand usage based on quality metrics

---

## Part 7: Risk Assessment

### 7.1 Known Risks

| Risk | Severity | Likelihood | Mitigation |
|------|----------|------------|------------|
| Poor rendering quality for complex PDFs | High | Medium | Hybrid fallback to PDFium |
| CJK font rendering issues | Medium | Medium | Test with corpus, fallback if needed |
| Performance regression | Medium | High | Caching, lazy evaluation, benchmarks |
| Transparency/blend mode bugs | Medium | Medium | Visual comparison tests |
| Memory leaks in PdfPig.Rendering.Skia | Low | Low | Memory profiling tests |
| API changes in early-stage library | Medium | High | Pin version, wrap in abstraction |

### 7.2 PdfPig.Rendering.Skia Specific Concerns

From library documentation:
- "This is a very early version and the code is constantly evolving"
- Based on PDFBox Java port (may have translation artifacts)
- Limited documentation on edge cases

### 7.3 Fallback Strategy

```
┌─────────────────────────────────────────────────────────────┐
│                    Rendering Request                         │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
            ┌─────────────────────────────────────┐
            │  Configuration: Which renderer?     │
            └─────────────────────────────────────┘
                              │
        ┌─────────────────────┼──────────────────────┐
        │                     │                      │
        ▼                     ▼                      ▼
   "pdfium"              "hybrid"               "pdfpig"
        │                     │                      │
        ▼                     ▼                      ▼
   PDFium only         Try PdfPig first        PdfPig only
        │                     │                      │
        │              ┌──────┴──────┐               │
        │              ▼             ▼               │
        │          Success?      Failure?            │
        │              │             │               │
        │              ▼             ▼               │
        │          Return      Fallback to           │
        │          result        PDFium              │
        │              │             │               │
        └──────────────┴─────────────┴───────────────┘
                              │
                              ▼
                     Return SKBitmap
```

---

## Part 8: Implementation Schedule

### GitHub Issues to Create

| Issue # | Title | Effort | Dependencies |
|---------|-------|--------|--------------|
| #217 | Create Excise.App.Renderer project structure | Small | None |
| #218 | Implement IPdfRenderer interface and RenderOptions | Small | #217 |
| #219 | Implement PdfPigRenderer using PdfPig.Rendering.Skia | Medium | #218 |
| #220 | Implement PdfiumRenderer wrapper | Small | #218 |
| #221 | Implement HybridRenderer with fallback logic | Medium | #219, #220 |
| #222 | Extract cache logic to IRenderCache | Small | #217 |
| #223 | Integrate Excise.App.Renderer with PdfRenderService | Medium | #219, #222 |
| #224 | Add visual comparison tests (SSIM) | Medium | #219, #220 |
| #225 | Add coordinate consistency tests | Medium | #219 |
| #226 | Add performance benchmark tests | Small | #219, #220 |
| #227 | Test against veraPDF corpus | Large | #219 |
| #228 | Add renderer configuration options | Small | #223 |
| #229 | Add "experimental renderer" UI option | Small | #228 |
| #230 | Document Excise.App.Renderer library | Small | All |

### Recommended Order

1. **Foundation** (#217, #218): Project structure and interfaces
2. **Renderers** (#219, #220, #221): Implement all three renderer types
3. **Integration** (#222, #223): Connect to existing code
4. **Validation** (#224, #225, #226, #227): Ensure quality
5. **Release** (#228, #229, #230): Configuration and documentation

---

## Part 9: Success Criteria

### 9.1 Minimum Viable Product

- [ ] Excise.App.Renderer library compiles and runs
- [ ] PdfPigRenderer can render simple PDFs
- [ ] HybridRenderer falls back correctly
- [ ] All existing tests pass with hybrid renderer
- [ ] No regression in rendering speed (>50% slower = fail)

### 9.2 Quality Gates

- [ ] SSIM score ≥ 0.90 for 95% of veraPDF corpus PDFs
- [ ] Coordinate consistency test passes for all test PDFs
- [ ] Memory usage no more than 2x PDFium for same operations
- [ ] No crashes or hangs on any corpus PDF

### 9.3 Long-term Goals

- [ ] PdfPig success rate ≥ 99% (measured via HybridRenderer stats)
- [ ] Default renderer switched to HybridRenderer
- [ ] PDFium dependency made optional

---

## Sources

- [PdfPig.Rendering.Skia on NuGet](https://www.nuget.org/packages/PdfPig.Rendering.Skia)
- [PdfPig.Rendering.Skia on GitHub](https://github.com/BobLd/PdfPig.Rendering.Skia)
- [Melville.Pdf on GitHub](https://github.com/DrJohnMelville/Pdf)
- [PdfPig Main Repository](https://github.com/UglyToad/PdfPig)

---

*Created: 2026-01-01*
*Author: Claude Code (Opus 4.5)*

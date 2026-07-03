# Pdfe.Rendering

A **framework-neutral PDF render API** for .NET, built on
[`Pdfe.Core`](https://www.nuget.org/packages/Pdfe.Core) (parser) and
[SkiaSharp](https://github.com/mono/SkiaSharp) (rasterizer). No native PDFium, no
platform lock-in — runs anywhere SkiaSharp does (Windows/Linux/macOS), trim/AOT-friendlier,
permissively licensed (MIT). The default pipeline is managed; an optional
OpenJPEG command-line fallback can improve selected JPEG2000/JPX images when
available.

For an Avalonia UI control built on this, see
[`Pdfe.Avalonia`](https://www.nuget.org/packages/Pdfe.Avalonia). This package is the
engine; pair it with any UI (WPF, WinForms, MAUI, Blazor canvas, Uno) or a headless
service.

## Install

```
dotnet add package Pdfe.Rendering
```

## Render a page

```csharp
using Pdfe.Core.Document;
using Pdfe.Rendering;
using SkiaSharp;

using var doc = PdfDocument.Open("sample.pdf");
var renderer = new SkiaRenderer();

// -> SKBitmap (integrate into any Skia surface, or convert to your UI's image type)
using SKBitmap bmp = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 150 });

// -> PNG bytes / stream (framework-neutral)
using var fs = File.Create("page1.png");
renderer.RenderPageToPng(doc.GetPage(1), fs, new RenderOptions { Dpi = 200 });

// -> cancellable (checked between content-stream operators)
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
using var bmp2 = renderer.RenderPage(doc.GetPage(1), new RenderOptions(), cts.Token);
```

## API surface

| Member | Returns | Notes |
|--------|---------|-------|
| `SkiaRenderer.RenderPage(page)` | `SKBitmap` | 150 DPI default |
| `SkiaRenderer.RenderPage(page, RenderOptions)` | `SKBitmap` | DPI, background, anti-alias, clip |
| `SkiaRenderer.RenderPage(page, RenderOptions, CancellationToken)` | `SKBitmap` | cancellable (#346/#366) |
| `SkiaRenderer.RenderPageToPng(page, Stream, RenderOptions?, CancellationToken)` | `void` | PNG output for non-Skia consumers |

`RenderOptions`: `Dpi` (default 150), `BackgroundColor` (default white), `AntiAlias`
(default true), `ClipRect` (page points). The renderer honours the page `/Rotate` entry.

Page **metadata, text (`page.Letters`), links (`page.GetLinks()`), annotations
(`page.GetAnnotations()`)** come from `Pdfe.Core` — combine them with rendered bitmaps to
build selection, search, and link UIs (as `Pdfe.Avalonia` does).

For an `SKImage`/`SKPicture` path, call `RenderPage(...)` and use SkiaSharp directly on the
returned `SKBitmap`.

## Known engine gaps

Tracked in the [pdfe](https://github.com/marctjones/pdfe) issue tracker:
remaining JBIG2 edge cases, advanced shading precision, annotation appearance
fidelity, malformed/fuzzed PDFs, encrypted PDFs requiring non-empty passwords,
and large-file performance. Release-quality rendering checks use the pdf.js
all-pages corpus scanner against MuPDF plus Poppler, with optional
Ghostscript/GhostPDF, Apache PDFBox, and PDFium escalation oracles for
unsettled pages. Remaining `DIFF` cases are fixed, deferred, or documented
through the #491 quality dashboard.

For JPEG2000/JPX images, the renderer uses the managed CSJ2K decoder first. If
`opj_decompress` from OpenJPEG is available on `PATH` (or configured with
`PDFE_OPENJPEG_DECOMPRESS`), grayscale JP2 images with explicit PDF soft masks
can use OpenJPEG as a best-effort fallback for JP2 component-definition/opacity
profiles that CSJ2K cannot decode cleanly.

## Color preview boundary

`Pdfe.Core.ColorSpaces.PdfColorConverter` is the shared CMYK-to-RGB boundary used
by renderer-facing color spaces. Raw `/DeviceCMYK` uses pdfe's deterministic
process screen-preview conversion when a document does not provide a calibrated
CMYK preview. `/DefaultCMYK`, ICCBased CMYK, and PDF/X output intents are used
for screen-preview color where the document supplies usable profiles. The
remaining tracked color work is not a blanket "ICC missing" gap; it is limited
to specific reference-disagreement and prepress-fidelity cases tracked through
the #491 quality dashboard and focused renderer issues.

MIT licensed. Part of the [pdfe](https://github.com/marctjones/pdfe) project.

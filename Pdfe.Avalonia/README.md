# Pdfe.Avalonia

A reusable, **pure-managed** PDF viewer control for [Avalonia](https://avaloniaui.net/) — no native
PDFium, no webview. Rendering is done with [SkiaSharp](https://github.com/mono/SkiaSharp) via
[`Pdfe.Rendering`](https://www.nuget.org/packages/Pdfe.Rendering); parsing is
[`Pdfe.Core`](https://www.nuget.org/packages/Pdfe.Core). Cross-platform (Windows/Linux/macOS),
permissively licensed (MIT), and trim/AOT-friendlier than native-binary viewers.

## Install

```
dotnet add package Pdfe.Avalonia
```

Brings in `Pdfe.Core`, `Pdfe.Rendering`, `Avalonia`, and `SkiaSharp` transitively.

## Usage

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:pdf="using:Pdfe.Avalonia.Controls">
    <pdf:PdfViewerControl x:Name="Viewer"
                          Document="{Binding Document}"
                          CurrentPage="{Binding CurrentPage}"
                          ZoomLevel="{Binding ZoomLevel}" />
</Window>
```

```csharp
using Pdfe.Core.Document;
using Pdfe.Avalonia.Controls;

// Load a document and hand it to the control.
Viewer.Document = PdfDocument.Open("sample.pdf");
Viewer.CurrentPage = 1;          // 1-based
Viewer.ZoomLevel = 1.0;          // 100%
Viewer.NextPage();
Viewer.ZoomToActualSize();
```

## Public API (highlights)

| Member | Kind | Purpose |
|--------|------|---------|
| `Document` | property (`PdfDocument?`) | the document to display |
| `CurrentPage` | property (`int`, 1-based) | shown page |
| `ZoomLevel` | property (`double`, 1.0 = 100%) | zoom factor |
| `InteractionMode` | property (`InteractionMode`) | None / TextSelection / Pan / Redaction / FormAuthoring / Typewriter |
| `Annotations`, `FormFields`, `HiddenTextHighlights`, `TypewriterTextOperations` | properties | overlay inputs |
| `IsLoading` / `HasError` / `ErrorMessage` | read-only properties | render state |
| `NextPage()` / `PreviousPage()` | methods | navigation |
| `ZoomIn()` / `ZoomOut()` / `ZoomToActualSize()` | methods | zoom |
| `AddSearchHighlight()` / `ClearSearchHighlights()` | methods | search overlay |
| `PageChanged`, `TextSelected`, `LinkClicked`, `RedactionDrawn`, `FormFieldEdited`, `FormFieldRectDrawn`, `TypewriterTextCreated` | events | interaction callbacks |

## Sample

See `Pdfe.Avalonia.Sample` in the [repository](https://github.com/marctjones/pdfe) for a minimal
open-and-view app, and the `PdfEditor` app for a full-featured reference (redaction, search, forms).

## Notes

- Requires a project that allows the Avalonia runtime (this control is a `UserControl`).
- The control uses an `unsafe` pixel copy (`SKBitmap` → Avalonia `WriteableBitmap`) for speed.
- Rendering engine gaps are tracked in the pdfe issue tracker (e.g. JBIG2/JPX image filters).

MIT licensed. Part of the [pdfe](https://github.com/marctjones/pdfe) project.

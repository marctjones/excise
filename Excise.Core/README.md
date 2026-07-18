# Excise.Core

A **pure-managed PDF parser and document model** for .NET — no native dependencies. Open,
inspect, edit, and save PDF documents; extract text, links, annotations, and form fields;
create sticky-note/highlight annotations; true glyph-level redaction. Cross-platform,
trim/AOT-friendlier, MIT-licensed.

This is the engine layer. For rendering to bitmaps see
[`Excise.Rendering`](https://www.nuget.org/packages/Excise.Rendering); for an Avalonia viewer
control see [`Excise.Avalonia`](https://www.nuget.org/packages/Excise.Avalonia).

## Install

```
dotnet add package Excise.Core
```

## Use

```csharp
using Excise.Core.Document;

using var doc = PdfDocument.Open("sample.pdf");
int pages = doc.PageCount;
var page = doc.GetPage(1);

foreach (var letter in page.Letters ?? Enumerable.Empty<Excise.Core.Text.Letter>())
    Console.Write(letter.Value);

foreach (var link in page.GetLinks())   { /* internal-document links */ }
foreach (var annot in page.GetAnnotations()) { /* widgets, notes, … */ }

doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "Review this");
doc.AddHighlightAnnotation(1, new PdfRectangle(100, 650, 260, 670), "Important");

// Glyph-level redaction (removes content from the stream, not just a black box):
page.RedactArea(new PdfRectangle(/* … */));
doc.Save("redacted.pdf");
```

Capabilities include encryption (RC4 / AES-128/256), sticky-note/highlight
annotation authoring, AcroForms
(read/edit/author/flatten, including widget metadata for checkbox/radio/choice
workflows), Type0/CID (CJK) fonts with ToUnicode, optional content groups, XMP
metadata, embedded-file extraction, and a broad content-stream operator set.
Hostile input fails with typed `PdfParseException`s rather than CLR crashes
(fuzz-tested), with recursion-depth and cancellation guards.

MIT licensed. Part of the [excise](https://github.com/marctjones/pdfe) project.

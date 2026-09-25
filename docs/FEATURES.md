# Features

What excise does, by area. For the short version see the [README](../README.md).

## Desktop app
- Open, view, navigate PDFs with smooth Skia rendering
- Page organization (add/insert, extract, remove, reorder, rotate; current page or selected pages; 90°/180°/270°)
- Text selection and copy with letter-level positions
- Find with highlights and navigation
- Zoom modes: fit width, fit page, actual size, free zoom
- Page thumbnails sidebar
- **Typewriter text** — place editable text boxes on flat PDFs, then save them as normal page content instead of annotations
- **AcroForm editing** — click text, checkbox, radio, or dropdown widgets and edit inline; save filled forms as interactive copies or create a flattened form copy
- **AcroForm authoring** — drag-rect on a page to create new fields (Text / Checkbox / Choice / Signature); auto-detect underline placeholders and empty squares as fields
- **Annotation authoring** — 15 annotation types from the Annotate menu, all written as real PDF annotations: highlight selected text or mark it up with Underline, StrikeOut and Squiggly, sticky notes, shapes from a drag (Square, Circle, FreeText), rubber stamps (all 15 standard names, plus an image stamp from a picked file for signatures and letterheads), and drawn paths (freehand Ink, Line, Arrow, Polygon, PolyLine). Drawn paths use one capture mode: drag for ink and lines, click-per-vertex for polygons with double-click or Enter to finish, Escape to abandon, Backspace to take back a point
- Reveal Hidden Text — yellow highlights for structural detections (text covered by rectangles), orange for differential-OCR recoveries (text inside rasterized images)
- Digital signature inspection — checks ByteRange structure, verifies the detached CMS digest/signature over the signed bytes, validates the signer certificate chain against the OS trust store (distinguishing valid-and-trusted from valid-but-untrusted, modified, and unverifiable signatures), and clearly reports remaining OS trust-chain validation limitations (revocation is not checked)
- **Attachments pane** — a sidebar pane, shown by default, lists files embedded in the PDF (name, size, description, modified date, and the page for files attached to a page annotation); save one or all of them to a location you choose, or strip them (undoable). A warning banner appears on open when a document carries attachments, because they are invisible on the page and can hold a full copy of the document's data (ZUGFeRD/Factur-X); it stays until you close it or open another document, rather than disappearing on a timer (#1619). Hide the pane with View ▸ Show Attachments. excise never opens or runs an attachment
- Open a PDF by dragging it onto the window
- **Several documents at once** — each opens in its own window, with its own undo history and unsaved-changes state; on macOS the windows use native tabs when System Settings asks for them. Preferences ▸ Documents can instead open documents as tabs inside one window (close, reorder by dragging, move a tab to its own window, overflow list) or replace the current document as before
- Prompts before closing, quitting, or opening another file with unsaved changes — and saves a **copy**, never overwriting your original
- Bates numbering
- **Reduce File Size** — Document ▸ Reduce File Size… writes a smaller copy to a location you choose and shows the size before and after. *Lossless* recompresses and deduplicates data and drops page thumbnails and other applications' private data, so pages look exactly the same; *High* (300 dpi), *Standard* (150 dpi) and *Screen* (96 dpi) also downsample images well above that resolution. The original file is never changed, and a redacted document stays redacted
- CLI-first automation with stable JSON, batch workflows, progress NDJSON, and
  AppleScript/Shortcuts, PowerShell/Power Automate, and Linux/GNOME examples
- Roslyn-based GUI scripting for developer/test automation in Debug builds; Release builds exclude it by default unless `-p:EnableScripting=true` is set

## Glyph-level redaction
**Text is removed from the PDF structure, not just visually covered.**

- Glyph-level removal — individual glyphs are excised from content streams
- Image XObject redaction — image overlays that intersect a redaction area are removed, not just blacked out
- External tools (`pdftotext`, mutool, Acrobat copy-paste) cannot recover redacted content
- Mark-then-apply workflow with red dashed previews and a Clipboard History sidebar showing what was removed
- Original protection — defaults the save dialog to `filename_REDACTED.pdf`
- Safe-to-share save path — `RedactedCopySafetyService` scrubs Info metadata, XMP metadata, and embedded files/attachments by default, then reports content-removal, metadata, attachment, and hidden-text audit status without repeating removed text
- Archival documents stay archival — a redaction of a PDF/A file keeps the `pdfaid` identification PDF/A requires (and nothing else from the XMP packet), so the output still validates with veraPDF instead of silently ceasing to be PDF/A
- `PdfDocument.ScrubMetadata(scrubAttachments: true)` strips Info dict, XMP, and embedded files in one call — important when redacted documents may carry the data they were redacted of in attachments (ZUGFeRD, Factur-X)
- **Two redaction output profiles, Standard by default** — every redaction (GUI, `excise redact`, batch `redaction.apply`, scripting, and the `RedactText`/`RedactArea` library calls) also removes the hidden machinery that cutting a word out cannot make safe, and lists each removal in the report: all JavaScript; actions that open files, submit data or import data (page links and bookmarks still work); the producer's private `/PieceInfo` data; page thumbnails (a picture of the page *before* the redaction); the appearance of hidden fields and annotations; content on optional-content layers that are switched off; and the document properties and XMP metadata (a PDF/A or PDF/UA marker is kept, so a tagged, accessible PDF stays accessible — verified with veraPDF). Tooltips, alternate text, structure titles, field names, bookmark titles and link targets are **kept**, with the redacted word cut out. `--profile maximum` / Preferences ▸ Redaction ▸ Output Profile adds: the whole value of every kept item is dropped, and bookmarks, links, comments and field names are stripped and forms and annotations flattened — **the result is no longer accessible or interactive**, and the report says so
- **No attachments in redacted output, by default** — every redaction (GUI, `excise redact`, batch `redaction.apply`, scripting, and the `RedactText`/`RedactArea` library calls) removes every embedded file, including files attached to page annotations and embedded media, and lists each removed file with its size. To keep them, use `--keep-attachments`, batch `keepAttachments: true`, `RedactionOptions.KeepAttachments`, or Preferences ▸ Redaction: kept text attachments (txt, csv, xml, html, json, md) have the term cut out, attached PDFs are redacted too, and any other attachment is reported as not checked. A PDF portfolio is refused unless attachments are kept
- **XFA forms** — redacting a document with an XFA form removes the XFA packet (the AcroForm fields stay), because its form data repeats the field values and Acrobat would put them back on the page
- OCG-aware — `RedactText` defaults to `includeHiddenLayers=true` so hidden optional content groups don't slip past
- Verified against real-world fixtures (CT birth certificate, government forms) at the pixel and content-stream level

## AcroForm editing & authoring
- **Dynamic XFA forms are displayed** — a dynamic XFA form (other readers show only a "Please wait…" placeholder, Preview included) is laid out from its XFA template and data when it opens, and the app says so in a banner. Its FormCalc `initialize` and `calculate` scripts run in excise's own interpreter (no JavaScript engine, no file or network functions, bounded in steps, depth and time; a failing script is undone and reported); JavaScript, `validate` and click scripts never run. Display only: these fields cannot be filled yet, and images, barcodes and some styling are not drawn (#1547, #1824, #1825). Static XFA forms, like the IRS forms, fill through their ordinary AcroForm fields.
- `PdfField.SetValue(string?)` mutates `/V`, sets `/NeedAppearances`, updates `/AS` for buttons, and throws on read-only and signature fields
- `PdfField` exposes effective `/Ff` flags plus widget metadata/export values so callers can distinguish checkboxes, radio groups, combo boxes, and push buttons
- `PdfDocument.FlattenAcroForm()` bakes values into static page content, clips/wraps text to widget bounds, draws only the selected radio widget, and strips widget annotations
- `AcroFormAuthoring` extension methods: `AddTextField`, `AddCheckBox`, `AddChoiceField`, `AddSignatureField` — auto-create the AcroForm dict and `/DR/Font/Helv` on first call
- `PdfFormAutoDetector` heuristically suggests fields where the page has horizontal underlines or empty checkbox-sized outlines (Acrobat-style "Prepare Form")

## Page and annotation authoring
- Page organization is supported in the desktop app and service layer: append/insert pages from another PDF, extract the current page or selected pages, remove current or selected pages, move current or selected pages earlier/later, and rotate pages. Page-owned streams/resources/annotations are cloned into copied pages; the app warns when document-level structures such as outlines, named destinations, or AcroForm metadata may need review.
- The desktop app authors 15 annotation types from the Annotate menu (see the feature list above). `PdfAnnotationAuthoring` extension methods expose the same workflows in code: `AddHighlightAnnotation` / `AddUnderlineAnnotation` / `AddStrikeOutAnnotation` / `AddSquigglyAnnotation` for text markup, `AddTextAnnotation` for sticky notes, `AddSquareAnnotation` / `AddCircleAnnotation` / `AddFreeTextAnnotation` for shapes, `AddStampAnnotation` / `AddImageStampAnnotation` for stamps, and `AddInkAnnotation` / `AddLineAnnotation` / `AddArrowAnnotation` / `AddPolygonAnnotation` / `AddPolyLineAnnotation` for drawn paths.
- Two of those 15 are not distinct PDF subtypes, which matters if you are inspecting output: an **Arrow** is a `/Line` carrying `/LE [None ClosedArrow]`, and an **ImageStamp** is a `/Stamp` whose appearance stream is an embedded image. `PdfAnnotation.LineEndings` exposes the former so the difference is readable, not just writable.

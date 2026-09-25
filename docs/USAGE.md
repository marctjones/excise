# Using the desktop app

Common workflows and keyboard shortcuts.

## Desktop redaction (mark-then-apply)

1. **Enable redaction mode** — toolbar button or press `R`
2. **Mark areas** — click and drag (red dashed outline = pending)
3. **Review pending marks** — sidebar shows preview text
4. **Apply** — toolbar button or `Enter` (permanent removal)
5. **Verify** — Clipboard History panel shows exactly what came out
6. **Save** — defaults to `filename_REDACTED.pdf`

Multiple areas across multiple pages can be marked and applied as a single batch.

## Typewriter text on flat PDFs

1. Click **✎ Type** in the toolbar.
2. Click or drag on the page to place a text box.
3. Type, move, resize, or delete the pending box before saving.
4. Save to flatten the text into the PDF page content. When the open file is still the original, excise routes the save through **Save a Copy** so the original is preserved.

## Form fill (existing AcroForm)

1. Open a PDF with form fields. Each field becomes an inline editor on the page: text fields use text boxes, choice/radio fields use selectors, and checkboxes use checkboxes.
2. Edit a value. Single-line text commits on Enter or focus loss; multiline text commits on Ctrl+Enter or focus loss; Escape restores the last committed value.
3. Use **Save Filled Copy** / **Save As** to preserve interactive form fields and values.
4. Use **Flatten Form** to create a copy where form values are baked into static page content and widget annotations are removed.

## Form authoring (create new fields)

1. Click **📋 Add Field** in the toolbar (or call `doc.AddTextField(...)` etc. from code).
2. Pick a field type from the combo (Text / Checkbox / Choice / Signature).
3. Drag a rect on the page — the new field appears immediately and is editable.
4. **🪄 Auto-detect** scans the current document for likely field positions — long horizontal strokes (text-field underlines) and small square outlines (checkboxes) — and creates them in one click.

## Authoring PDFs from scratch (high-level)

`Excise.Core.Authoring.PdfDocumentBuilder` is a friendly, flow-layout writer that
handles word-wrap, pagination, and field placement so you never touch raw
coordinates. It sits on top of the low-level `PdfGraphics` / `AcroFormAuthoring`
API (drop down to those any time via `.Custom(...)` or `.Build()`).

```csharp
using Excise.Core.Authoring;

byte[] pdf = PdfDocumentBuilder.Create()           // US Letter, 1-inch margins
    .Heading("Membership Application")
    .Paragraph("Please complete all required fields.")
    .HorizontalRule()
    .KeyValue("Date", "2026-06-05")
    .TextField("Full name", "fullName", required: true)
    .CheckBox("I agree to the terms", "agree")
    .Dropdown("Tier", new[] { "Basic", "Standard", "Premium" }, "tier", "Standard")
    .TextField("Comments", "comments", multiline: true, lines: 4)
    .Table(new[]
    {
        new[] { "Item", "Qty", "Price" },
        new[] { "Widget", "3", "$9.00" },
    }, columnWeights: new[] { 2.0, 1.0, 1.0 }, headerRow: true)
    .SaveToBytes();                                 // or .Save("form.pdf")
```

The result is a real, fillable AcroForm PDF: text is extractable, content
flows onto new pages automatically, and the form fields are live in any viewer.
Styling is via the immutable `TextStyle` record (family/size/bold/italic/color/
alignment); page size/margins via `PageSize` and `PageMargins`. See issue #383.

## Reveal Hidden Text

`Tools → Reveal Hidden Text` finds text that's been visually hidden by overlays:

- **Yellow boxes** — structural detections from `Excise.Core.Text.Segmentation.HiddenTextDetector` (text covered by later filled rectangles, the classic bad-redaction pattern)
- **Orange boxes** — differential-OCR recoveries (`Excise.Ocr.DifferentialOcrAuditor`) for text hidden inside rasterized images by an opaque overlay

Useful for auditing third-party redactions before relying on them.

## Keyboard shortcuts

Press **F1** to view all in-app.

| Category | Action | Shortcut |
|---|---|---|
| File | Open / Save / Save As / Close | `Ctrl+O` / `Ctrl+S` / `Ctrl+Shift+S` / `Ctrl+W` |
| Edit | Find / Find Next / Find Previous | `Ctrl+F` / `F3` / `Shift+F3` |
| View | Zoom In / Out / Actual / Fit Width / Fit Page | `Ctrl+Plus/Minus/0/1/2` |
| Navigation | Next/Previous/First/Last Page | `Page Down/Up`, `Home`, `End` |
| Modes | Redaction / Text Selection / Apply | `R` / `T` / `Enter` |
| Pages | Rotate Left / Right | `Ctrl+L` / `Ctrl+R` |
| Tabs | Next / Previous document tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` (or `Ctrl+PgDn` / `Ctrl+PgUp`; on macOS also Window ▸ Show Next/Previous Tab) |

# Dynamic XFA display (#1547 phase 2)

A dynamic XFA form (the XML Forms Architecture, deprecated in PDF 2.0) keeps its
real form in `/AcroForm /XFA`. Its PDF pages hold only a "Please wait..."
placeholder that an XFA engine is expected to replace. This document describes
how excise replaces that placeholder with the form's initial layout, and the
rules that keep the feature from becoming a redaction leak.

Status, evidence and gaps belong to the issue tracker (#1547, and the follow-ups
named below), not to this page.

## Decisions (defaults Marc can veto)

1. **Synthesise ordinary PDF pages; do not add a renderer.** The layout engine
   produces page content streams (base-14 fonts, paths, text) that the existing
   Excise.Rendering renderer, text extractor, search and redaction consume
   unchanged. There is no second renderer and no second content-stream state
   machine ("One walk, many sinks", CLAUDE.md).
2. **Replace the placeholder pages in the live document, in memory, at open.**
   The app shares one `PdfDocument` between viewing and saving (#917), and
   thumbnails, search, the text index, printing and redaction all read it.
   A separate "view document" would fork every one of those paths. The layout
   runs in `PdfDocumentService.LoadDocument`, before the view model activates
   the document, so nothing else ever sees the placeholder. The document is not
   marked modified.
3. **Keep `/XFA` and `/NeedsRendering` on save.** Acrobat and Firefox still
   regenerate the form from the XFA on a saved file; every other viewer now
   shows excise's rendition instead of "Please wait".
4. **Generated pages carry a marker.** Each generated page has
   `/PieceInfo << /Excise << /LastModified (date) /Private << /XfaLayout true >> >> >>`
   (ISO 32000-1 §14.5, the standard place for application-private page data).
   When a document is opened and any page carries the marker, excise does not
   lay the form out again. The pages are already a rendition, possibly with the
   user's annotations, redactions or added pages, and a second layout would
   replace them. A tool that rewrites all the pages, Acrobat included, drops
   the marker, so its output is laid out afresh.
5. **Any redaction of a document with an XFA form removes `/XFA` and
   `/NeedsRendering` whole.** For a form excise laid out, the generated pages
   ARE the form, so the XFA packet is a positionless copy of everything on
   them. For a static XFA form (#1574 — the IRS W-4/W-9/1040 shape), the
   `datasets` packet repeats each field value and Acrobat merges it back onto
   the page when the file opens. Area redaction never scrubbed XFA (it has no
   term), and a term-strip cannot see every way a value reaches the page.
   Without this rule, a redacted-and-saved file would be re-rendered from the
   untouched datasets by Acrobat, pdf.js, or excise itself. A static form
   keeps its AcroForm fields, which every non-XFA viewer already uses; what is
   lost is the XFA behaviour in Acrobat. This deliberately overrides the XFA
   carrier's "RemoveWhole is not defined" refusal, which still applies to a
   direct `PdfDocumentSanitizer.ScrubTerms` call. The removal is reported as an
   `/XFA` carrier row (`PdfXfaLayout.RemoveXfaFormForRedaction`) and, for area
   redaction, on the redacted-copy report.
6. **Only FormCalc `initialize` and `calculate` run (#1570); JavaScript never does.** The
   interpreter is a tree-walker over our own AST under `Excise.Core/Xfa/FormCalc/`
   (no reflection, no dynamic code, no `Get`/`Post`/`Put` and no host functions: absent, not
   stubbed). A script reaches the form only through `IFcHost`/`IFcObject`, and can write two
   things: a field's value and any object's `presence` (kept per instance in
   `XfaFormNode.PresenceOverride`, never in the template element that repeated instances
   share). Steps, call depth, string length, list size, `Eval` nesting and wall time are
   bounded; a nested `Eval` spends its parent's remaining budget. Each script is a
   transaction: a failure undoes its writes, is reported in `XfaLayoutResult.ScriptFailures`,
   and the rest carry on. `calculate` repeats until the values settle, capped at ten passes.
   Scripts run in `ApplyXfaLayout` at open and nowhere else: redaction, save, print-copy
   creation and the command line never execute one (`FormCalcContainmentTests` reads the
   sources and fails on a new caller). `XfaLayoutOptions.RunFormCalc` turns it off.
   Not run: JavaScript (#1571), `validate`, `click`, `docReady` and every other event.
7. **Display only.** Values are drawn as page content, not as AcroForm widgets.
   Filling and writing the datasets back is #1547 phase 3. Converting to
   AcroForm is #1569. A flattened static copy (layout applied, `/XFA` removed)
   falls out of this work and is exposed through the library API.
8. **Values are drawn raw.** Picture clauses (`<format><picture>`) are not
   applied, so the text on the page is the text in the datasets. A formatted
   rendition ("1,234" for "1234") would let a term-redaction of the displayed
   text miss the stored value. Rule 5 covers that case anyway, but raw values
   keep search and redaction matching the stored data.
   A value a script WRITES is derived data of the same kind: a term redaction cannot see
   `Concat(A, B)` as the two strings that made it. Rule 5 covers it (redaction removes the
   whole `/XFA` packet, script text included, and the page text is ordinary page content), and
   `XfaFormCalcRedactionTests` pins both a secret written literally by a script and one that
   only a calculation produces. `XfaLayoutResult.FieldsWrittenByScripts` names the fields.
9. **Password fields never show their value.** A `passwordEdit` draws its
   `passwordChar` once per character.

## Zero cost for non-XFA documents

Nothing in `Excise.Core/Xfa` runs unless `PdfDocument.DetectXfaForm()` returns
`Dynamic` and the catalog sets `/NeedsRendering true`. The second condition is
the one pdf.js uses. A document that is "dynamic" only because it has no
AcroForm widgets can have real page content (PDFium's
`rectangles_multi_page_xfa.pdf` has five drawn pages), and replacing those
pages with a layout would lose them. That check reads two catalog keys. It walks the AcroForm field tree
only when an `/XFA` entry exists. The service calls the layout only on that
result, so no XML is parsed, no allocation happens, and no type is loaded for
any other document.

## Pipeline

```
/AcroForm /XFA ──► XfaPackets        safe XML, packet selection by namespace
                     │
                     ▼
                  XfaTemplate        proto/use resolution, measurement parsing
                     │
datasets ────────►  XfaMerge         form DOM: occur instances, data binding
                     │
                     ▼
                  XfaLayout          virtual (unbounded) box tree, then
                     │               pagination into pageArea/contentArea
                     ▼
                  XfaPdfWriter       boxes → content streams on new pages
```

All code lives in `Excise.Core/Xfa` (namespace `Excise.Core.Xfa`). It is
internal except the entry point `PdfDocument.ApplyXfaLayout(...)`, its result
type, and `PdfDocument.HasXfaLayoutPages()`.

### Packets

`/XFA` is one XDP stream or a `(name, stream)` array whose pieces concatenate
into one XDP document. Parsing uses the settings of `XfaXmlCarrier` (the
redaction carrier), shared through one helper: DTDs are prohibited, there is no
resolver (so no external entities and no network), and the character count is
capped. Packets are chosen by namespace (`xfa-template`, `xfa-data`, ...), not
by local name, because the config packet has its own `<template>` element.

### Template

Measurements accept `in`, `cm`, `mm`, `pt`, `mp` and `px`, and default to
inches (XFA 3.3 "Measurements"). `use="#id"` and `usehref="#id"` /
`usehref=".#som"` prototypes merge with the referencing element. Attributes and
single-occurrence property children that the element does not set come from
the prototype. Resolution is depth- and count-bounded and detects cycles.
Prototypes in other documents (`usehref="file.xdp#..."`) are never fetched.

### Merge

The form DOM is a copy of the template with repeated subforms expanded:

- `occur`: without data, `initial` (default `min`, default 1) instances. With
  data, one instance per matching data group, clamped to `[min, max]`, where
  `max="-1"` is capped by a hard limit.
- Normal binding: a named subform consumes the next same-named data group in
  the current scope. A named field consumes the next same-named data value.
  Unnamed subforms are transparent.
- `bind match="none"` binds nothing. `match="global"` finds the first same-named
  data value anywhere. `match="dataRef"` follows a simple SOM path (`$.a.b`,
  `$record.a[2]`, `$data..a`, `[*]` for repeats).
- `exclGroup`: the group binds to one value, and the member whose on-value
  matches is drawn on.
- A field with no bound data shows its template `<value>`.

### Layout

Phase one builds an unbounded box tree:

- `position`: children at `x`/`y`, shifted by `anchorType`, inside the parent's
  margins.
- `tb`: stacked vertically.
- `lr-tb` / `rl-tb`: filled into rows that wrap at the available width.
- `table` / `row`: `columnWidths` (with `-1` auto columns), cell `colSpan`, and
  cells stretched to the row height.
- Sizes: `w`/`h` fixed; otherwise grown to content within `minW`/`maxW` and
  `minH`/`maxH`.
- `presence`: `hidden` and `inactive` take no space. `invisible` takes space
  and draws nothing. `relevant` (print/screen) is ignored for display.
- Fields: `caption` (placement, reserve, own font/para), `ui` border and
  margin, and `margin` insets.

Phase two paginates: the root subform's flowed content is poured into the
contentAreas of the current pageArea, in order. A box that does not fit moves
to the next contentArea. A flowed (`tb`, `table`) subform that does not fit
splits between its children, unless `keep intact` forbids it. A box taller
than a whole contentArea is placed anyway and clipped. `breakBefore` and
`breakAfter` (`targetType="pageArea"` or `"contentArea"`, `target`,
`startNew`), `<break>`, and pageArea `occur max` select the next area.
Fixed content on a pageArea (its own draws and fields) is drawn on every page
that uses it. There is always at least one page, and page count is capped.

### Emission

Each page becomes `Pages.AddBlank(medium)` plus one content stream:

- Fills and borders: edge count, thickness, colour, `presence`, `hand`,
  dashed/dotted strokes, and corner radius. Every draw and field is clipped to
  its own box.
- Text: `typeface` maps to a base-14 family (serif, mono, symbol and dingbat
  names, else Helvetica), with `weight` and `posture`, `size`, and fill
  colour. `hAlign`/`vAlign` are honoured, with word wrap for multi-line and
  `draw` text, and `comb` cells.
- Widgets are drawn as their static appearance. `checkButton`: box or circle,
  with a check/circle/cross mark when on. `choiceList`: the selected display
  text (dropdown) or the item list (list box). `button`: its caption.
  `passwordEdit`: masked. `signature`: an empty box.
- Draw content: `rectangle`, `line` and `arc` values are drawn as shapes.

After the new pages are written, the placeholder pages are removed and the new
pages are marked.

### What is reported, not drawn

The result lists what the rendition leaves out, and the banner summarises it:

- scripts present (event names counted), per #1570/#1571;
- images and `imageEdit` content (#1575);
- barcodes (#1576);
- text outside WinAnsi, which base-14 fonts cannot draw (#1577);
- gradient and pattern fills, drawn as their base colour (#1578);
- `keep`/`overflow` leaders and trailers;
- `usehref` into another file.

Base-14 substitution changes text widths. Myriad Pro, Designer's default
face, is drawn as Helvetica at 90% horizontal scale (`Tz 90`), because pdf.js,
which carries Myriad metrics, measured the corpus strings at a median 0.904 of
Helvetica's width.

## Security bounds

XFA is untrusted input:

- XML: the carrier's limits, with DTD and resolver disabled.
- Tree: element count, nesting depth, prototype chain length, total `occur`
  expansion, box count and page count are all capped.
- Time: a wall-clock budget plus the caller's `CancellationToken`, checked in
  every loop.
- Failure: any breach, malformed packet or missing template returns `Failed`
  with a reason and leaves the document untouched. The GUI then keeps the
  phase-1 warning banner.

## Verification

- **Oracle: pdf.js** (`pdfjs-dist`, `enableXfa`). Its XFA HTML is rendered in a
  real browser and the element boxes are read back. excise's generated PDF is
  read by **mutool** (text and positions) and rendered to PNG. excise never
  checks its own layout.
- Where pdf.js cannot open a file, the corpus row says "no oracle", not "pass".
- The redaction rule (decision 5) is checked by planting the defect: without
  the drop, pdf.js reads the redacted value back out of the saved file.
- The synthetic fixtures (datasets binding, repeats, tables, pagination) are
  checked into `Excise.Core.Tests`, because no corpus file carries datasets.
- The corpus comparison with pdf.js is not a gate yet (#1579). pdf.js departs
  from the spec in places: it draws captions whose `presence` hides them, and
  it honours breaks inside hidden subforms. So "agrees with pdf.js" needs
  per-file judgement.
- Area redaction of a static XFA form removes the packet too (#1574):
  `XfaLayoutRedactionTests.StaticXfaForm_AreaRedaction_RemovesTheDatasets_AndKeepsTheOtherField`
  checks it with mutool (catalog and text), Poppler (text and raster) and the
  saved bytes.

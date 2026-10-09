# XFA forms (#1547)

A dynamic XFA form (the XML Forms Architecture, deprecated in PDF 2.0) keeps its
real form in `/AcroForm /XFA`. Its PDF pages hold only a "Please wait..."
placeholder that an XFA engine is expected to replace. This document describes
how excise replaces that placeholder with the form's initial layout, how filled
values reach the XFA `datasets` packet, and the rules that keep both from
becoming a redaction leak.

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
3. **Keep `/XFA` and `/NeedsRendering` on save.** Acrobat is expected to regenerate the form
   from the XFA on a saved file (unverified since #2028; an Acrobat hand-check is on #2028);
   every other viewer shows excise's rendition instead of "Please wait". Firefox (pdf.js) used
   the XFA until #2028: it does not once the file carries the generated AcroForm fields of
   decision 11, and shows excise's rendition instead (#2036).
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
   A static form's filled value also sits in its widgets' `/V`, `/AP`, `/AS` and `/Opt`, and
   under decision 11 a dynamic form's will too. Removing `/XFA` does not touch those; the
   `form-fields` carrier does, for the widgets a redaction reaches. Generated widgets of a
   dynamic form add hidden and duplicate (`match="global"`) copies that an area redaction does
   not reach; decision 17 flattens them before the redaction. The `form` packet
   (decision 14) is removed with `/XFA`.
6. **Only FormCalc `initialize` and `calculate` run (#1570); JavaScript never does.** The
   interpreter is a tree-walker over our own AST under `Excise.Core/Xfa/FormCalc/`
   (no reflection, no dynamic code, no `Get`/`Post`/`Put` and no host functions: absent, not
   stubbed). A script reaches the form only through `IFcHost`/`IFcObject`, and can write two
   things: a field's value and any object's `presence` (kept per instance in
   `XfaFormNode.PresenceOverride`, never in the template element that repeated instances
   share). Steps, call depth, string length, list size, `Eval` nesting and wall time are
   bounded; a nested `Eval` spends its parent's remaining budget. Each script is a
   transaction: a failure undoes its writes, is reported in `XfaLayoutResult.ScriptFailures`,
   and the rest carry on. `initialize` runs first; then `calculate` repeats until the values
   settle, capped at ten passes. XFA 3.3 p407 (Rule 3) orders a merge's calculations before its
   initialize events, repeating calculations whose inputs change; no fixture pins the difference.
   Scripts run in `ApplyXfaLayout` at open and nowhere else: redaction, save, print-copy
   creation and the command line never execute one (`FormCalcContainmentTests` reads the
   sources and fails on a new caller). `XfaLayoutOptions.RunFormCalc` turns it off; in the app,
   Preferences > Forms sets it (`WindowSettings.RunFormCalc`, `PdfDocumentService.XfaLayoutOptionsForOpen`).
   Not run: JavaScript (#1571), `validate`, `click`, `docReady` and every other event.
7. **A laid-out dynamic form is display only.** Its values are drawn by AcroForm
   widgets the layout generates (decision 11, #2028), and every generated field is
   read-only (`/Ff` bit 1) until the datasets write-back (#2029) exists: an edit to `/V`
   alone would break K.2. The GUI form overlay leaves generated fields out
   (`MainWindowViewModel.OverlayFields`). Static XFA forms are fillable: their AcroForm fields are edited as usual and
   `PdfField.SetValue` writes the same value into the datasets
   (`XfaStaticDataSync`, #2013). Filling a dynamic form is #1547 phase 3
   (decision 11; not built). Converting to AcroForm (#1569) is closed. A
   flattened static copy (layout applied, `/XFA` removed) comes from
   `ApplyXfaLayout` + `RemoveXfaForm` through the library API.
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
   Against XFA 3.3: the Data and Form DOMs hold canonical values (p152) and a `format` picture
   changes only what is displayed (p164 Rule 3); without one, date, time and decimal fields are
   still displayed in the locale's default format (p165 Rule 4). So raw values are correct
   storage and a departure in display (Ohio's `0.00000000` where Acrobat and pdf.js show `0`).
   The merge also copies data text without applying a `bind` picture (p176), which real forms
   use (IMM 5257e, Ohio). A formatted rendition is an owner decision on #1547 (plan revision 2).
9. **Password fields never show their value.** A `passwordEdit` draws its
   `passwordChar` once per character.
10. **Fields look the way pdf.js shows them on screen (#1825).** The field tint
    (rgba(0, 54, 255, 0.13) on text-like edits and choice lists, not on
    `readOnly`/`protected`/`nonInteractive` edits), the 1.5pt red outline of a
    field with `validate nullTest="error"`, and a closed drop-down's arrow are
    pdf.js viewer chrome, not template content: pdf.js drops the tint and the
    arrow when printing. They stay in the page content when the value moves into a
    generated widget (decision 11), and a
    saved or printed rendition carries them. A `checkButton` inside an `exclGroup` with no `shape` is a circle, as
    pdf.js renders it (a radio button); an explicit `shape` is honoured.
11. **AcroForm widgets and the datasets are kept consistent (ISO 32000-2 Annex K.2).** A
    writer that creates or modifies a PDF with `/XFA` provides an AcroForm field for each XFA
    field, named by its XFA-SOM path (XFA 3.3 p72-74), with `/V` consistent with the XFA value
    and no `/A` or `/AA` on widgets whose actions the XFA specifies. A datasets-only design does not
    conform. Static forms: done (`XfaStaticDataSync`, called from `PdfField.SetValue`, the
    setter behind `fill-form`, Save Filled Copy and the GUI overlay). Dynamic forms (#2028):
    `ApplyXfaLayout` generates one field per field-map entry (`XfaWidgetWriter`): `/FT` by ui
    kind (text, numeric, date, password and barcode `Tx`; `choiceList` `Ch` with `/Opt [save
    display]`; `checkButton` a checkbox and a named `exclGroup` a radio field whose kids are the
    members, appearance states named by the on values; `signature` an unsigned `Sig`; `button`
    and `imageEdit` push buttons), `/V` the form value (none for a password, ISO 32000-2 Table
    231), `/MaxLen`, `Comb` and `Multiline` from the template, `/DA /Helv 0 Tf 0 g`, never
    `/NeedAppearances`. The value the layout drew moves from the page into the widget's `/AP`,
    a form XObject whose `/BBox` equals `/Rect`, so it draws exactly where the page did. A field
    not drawn gets one hidden zero-size widget on the first page, so the #2012 cut keeps it.
    Each page's `/PieceInfo /Excise /Private` records its generated widgets (`/XfaWidgets`), the
    page ordinal, the layout-engine version and the datasets hash. The datasets stay the stored
    value; S2 (#2029) writes through them. Widget `/Rect` serves
    non-XFA viewers only: an XFA processor places a field by the template (p74).
    **Names (#2035).** The full name is the field's SOM expression in the normal syntax: each
    named object `name[i]`, indexed among the same-named objects its SOM parent sees (p73-74);
    transparent objects are not written, namely nameless subforms and exclusion groups, areas even
    when named (p95) and `scope="none"` subforms (p849), so their children count as the parent's
    (p95). An object the normal syntax cannot reach (a nameless field, a name containing a dot,
    which XML allows, p75) is written by class, `#field[n]`, `n` counting every true sibling of
    that class, named or not (p96 note, p119-120), with its transparent ancestors up to the nearest
    written one by class too. No leading `form.`. Each segment is one partial name of a
    hierarchical field, so no partial name holds a dot (ISO 32000-2 §12.7.4.2); p73 would allow a
    dotted global name for an XFA pointer field, so dot-free partial names are a project rule. The
    static write-back resolves another producer's names leniently: a `#class[n]` index read both
    among all siblings (p96) and among the nameless ones (excise's count before #2035; Designer's
    count is unverified, no corpus file has a `#` segment) is accepted when both readings agree or
    only one resolves, and refused when they name different objects.
12. **A filled save strips `/Perms /DocMDP` and `/Perms /UR3` with their signatures, and
    reports it.** A full rewrite re-serialises the XFA stream, which can void a PDF signature over
    it (XFA 3.3 p557-558), and excise has no incremental writer. Keeping a certification valid
    needs an incremental fill-in save, which is a separate issue and is never used in a session
    that contains a redaction. Not built: the GUI asks before a save that would invalidate a
    signed `/Sig` field (#1415), but no save removes `/Perms` or reports it.
13. **User annotations survive a re-layout by page index.** A re-layout replaces the
    generated content and widgets of each page and carries every other `/Annots` entry on the
    same page index when the page count is unchanged. If the page count would change while user
    annotations exist, or the user added, deleted or reordered pages since the layout, the fill
    is refused with a reason. The XFA specification is silent here; this is a project rule.
    Not built (dynamic fill).
14. **The `form` packet is reset when excise writes the datasets.** XFA 3.3 defines no syntax
    for the saved Form DOM (p81-82); Adobe products re-merge template and data on open and then
    apply the saved Form DOM content (p1263), so a stale non-empty `form` packet could override
    the new data. A non-empty `form` packet is replaced by an empty element and reported; an
    empty one is left byte-identical (`XfaStaticDataSync`). Acrobat-side state it held
    (calculation overrides, validation overrides) is dropped with it.
15. **The XFA 3.3 specification is local only.** Adobe's `xfa_spec_3_3.pdf` (1584 pages,
    January 2012) is kept at `test-pdfs/archives/xfa_spec_3_3.pdf`, which is gitignored and is
    never committed (Adobe copyright; source and checksum on #1547). Cite it by page number;
    its printed page numbers equal the PDF page index.
16. **XFA JavaScript never runs.** #1571 (a restricted JavaScript engine) is closed. Totals,
    validations and barcodes a form computes in JavaScript are not updated by excise; the
    dynamic-form notice says JavaScript does not run.
17. **Redacting a filled dynamic form flattens the generated fields first.** Generated hidden
    and duplicate widgets can hold a value that an area redaction removed from a visible field,
    so the generated fields are baked into the page content, then the normal parse, filter,
    rebuild pipeline runs, then decision 5 removes the datasets. The redaction report says
    "generated XFA fields flattened". The ENGINE does it (#2037): `PdfXfaLayout.RemoveXfaFormForRedaction`
    calls `FlattenGeneratedXfaFields` first, before anything reads or rewrites page content, for
    every engine entry point (`RedactArea`, `RedactAreas`, `RedactText`, and the report variants),
    so a library caller cannot skip it; a flatten after the content rewrite would stamp the value
    back, and tests pin the order. `FlattenGeneratedXfaFields` stamps each shown widget's
    appearance through the AcroForm flatten path, removes the widgets and their fields, and records
    the row with the XFA removals; the report row reaches the user through `report.Carriers` (CLI
    and GUI notes). The two flattened-copy paths (GUI Save Flattened Form Copy, `fill-form
    --flatten`) still call it themselves, without the report row, because they make a flattened
    copy and do not redact; without it `FlattenAcroForm` would redraw a hidden field's `/V` as
    clipped page text. Lower-level public scrubbers (`PdfDocumentSanitizer.ScrubTerms`,
    `AppearanceStreamRedactor.RedactTerm`, `InteractiveRedactionScrubber`) never ran decision 5's
    removal and still do not.
18. **Reopening a form whose widgets and datasets disagree.** The datasets hash is stored with
    the layout marker. If the datasets still match it, a widget edit made by another tool is
    newer: it is written into the datasets and reported. If the datasets changed (Acrobat
    edited them), the datasets win and the widgets are regenerated. Not built; slice S4.
19. **Display formatting is separate from storage.** The datasets and `/V` stay canonical
    (XFA 3.3 p152, p163-164); decision 8 (values drawn raw) holds until slice S6, which applies
    the form's `format` picture to the drawn rendition only. With it comes one redaction rule: a
    hit on a field's drawn value scrubs that field's `/V`, `/AP` and every field bound to the
    same data node whole, never by substring. Fields whose bind picture excise cannot apply are
    read-only until then, and `/Perms` is stripped and reported on every save of a laid-out
    dynamic form (decision 12 extended).

## Zero cost for non-XFA documents

The layout runs only when `PdfDocument.DetectXfaForm()` returns
`Dynamic` and the catalog sets `/NeedsRendering true`. The second condition is
the one pdf.js uses. A document that is "dynamic" only because it has no
AcroForm widgets can have real page content (PDFium's
`rectangles_multi_page_xfa.pdf` has five drawn pages), and replacing those
pages with a layout would lose them. That check reads two catalog keys. It walks the AcroForm field tree
only when an `/XFA` entry exists. The service calls the layout only on that
result, so no XML is parsed, no allocation happens, and no type is loaded for
any other document. The static write-back (decision 11) costs `PdfField.SetValue` one
`/AcroForm /XFA` lookup on a document without XFA.

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
internal except `PdfDocument.ApplyXfaLayout(...)` with its options and result
types, `PdfDocument.HasXfaLayoutPages()` and `PdfDocument.RemoveXfaForm()`.

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
`usehref=".#som($template...)"` prototypes merge with the referencing
element (a SOM path rooted anywhere else, e.g. `$data`, is a data-binding
concern, not resolved here). Attributes and single-occurrence property
children that the element does not set come from the prototype. Resolution
is depth- and count-bounded and detects cycles. Prototypes in other
documents (`usehref="file.xdp#..."`) are never fetched.

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
  `minH`/`maxH`. A container's `minH` counts only under a `position` parent:
  under a flowing parent (`tb`, `lr-tb`, `rl-tb`, `table`, `row`) it is
  sized from its content, as pdf.js does. This departs from the attribute's
  definition; real forms carry design-time `minH` taller than their page
  (#1824). A table row's own `minH` still applies.
- `presence`: `hidden` and `inactive` take no space. `invisible` takes space
  and draws nothing. `relevant` (print/screen) is ignored for display.
- Fields: `caption` (placement, reserve, own font/para), `ui` border and
  margin, and `margin` insets.

Phase two paginates: the root subform's flowed content is poured into the
contentAreas of the current pageArea, in order. A pageArea within a
`pageSet` is chosen by `pagePosition` (`first`/`rest`/`last`/`only`) and
`oddOrEven`, falling back to the nearest less specific match, as pdf.js
does — a `simplexPaginated` set's `rest` area is real content-area geometry,
not a repeat of `first` (#1824). A box that does not fit moves to the next
contentArea. A flowed (`tb`, `table`) subform that does not fit splits
between its children, unless `keep intact` forbids it. A box up to 2pt
taller than a whole contentArea (`FitTolerance` in `XfaPaginator.cs`,
matching pdf.js's own fit check) is placed without a clipping report; only
beyond that is it placed and reported clipped. `breakBefore` and
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
  `draw` text, and `comb` cells. Characters base-14 cannot draw (CJK,
  Cyrillic, Greek...) use an installed wide-coverage Unicode font
  (`XfaFallbackFont`: a fixed per-platform list, read from the font directory
  by excise's own sfnt code, first font of a `.ttc`), embedded as a subset;
  excise bundles no font (#1577). Layout measures those characters in the
  same font.
- Widgets are drawn as their static appearance, with pdf.js's field chrome
  (decision 10). `checkButton`: box or circle (the widget border outlines it),
  with a check/circle/cross mark when on. `choiceList`: the selected display
  text (dropdown) or the item list (list box). `button`: its caption.
  `passwordEdit`: masked. `signature`: an empty box.
- Draw content: `rectangle`, `line` and `arc` values are drawn as shapes.
- Images (#1575): a draw's `<value><image>` and an `imageEdit` field's value
  (bound data first) are drawn. JPEG is embedded as is (`/DCTDecode`).
  Other formats are drawn only when the caller supplies a decoder
  (`XfaLayoutOptions.ImageDecoder`, internal): Excise.App passes a SkiaSharp
  one that opens PNG, BMP and GIF, and the pixels are embedded through
  `PdfImage.FromRgb`. The 50-megapixel cap is checked on the header's size
  before any pixel is decoded. Natural size comes from the JFIF, BMP or PNG
  `pHYs` density, else 72 dpi. Images are sized by `aspect` and anchored
  top-left as pdf.js anchors them. An `href` resolves only through the
  document's `/Names /XFAImages` tree; nothing is fetched.

After the new pages are written, the placeholder pages are removed and the new
pages are marked. Then the AcroForm fields are generated from the field map (decision 11); a
value no widget takes (the map or the generation failed) is drawn on its page, so a failure
costs the widgets, never a value. `XfaLayoutOptions.EmitWidgets = false` (internal) keeps the
Phase 2 pages, which the S1 oracles compare against.

### What is reported, not drawn

The result lists what the rendition leaves out, and the banner summarises it:

- scripts present (event names counted), per #1570/#1571;
- images, in a draw or an `imageEdit` field, that no decoder opens (without a
  decoder, anything but JPEG; in Excise.App, TIFF, which SkiaSharp cannot
  decode), and `href` images that are not in the document's
  `/Names /XFAImages` (#1575);
- barcodes (#1576);
- text outside WinAnsi that no installed fallback font covers, drawn as '?'
  (#1577);
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

### FormCalc threat model

A FormCalc script is attacker-controlled code that runs when a document opens (decision 6).
The attacker's goals are denial of service (hang, exhaust memory, crash the process), reaching
beyond the form, and making a displayed value escape redaction. Values below are the fields
named; the code is the authority.

| Threat | Bound | Enforced by |
|---|---|---|
| Huge or deeply nested source | script length, token count, parse depth | `FormCalcLexer.MaxScriptLength`, `FormCalcParser.MaxTokens`, `FormCalcParser.MaxDepth` |
| Deep AST at run time (a long `a.b.c...` chain) | stack probe, reported as a script error | `RuntimeHelpers.EnsureSufficientExecutionStack` in `FormCalcInterpreter.Exec`/`Eval` |
| Endless loop | steps per run; wall clock and cancellation every 256 steps | `FcLimits.MaxSteps`, `FcLimits.TimeLimit`, `FormCalcInterpreter.Tick` |
| Recursion | user-function call depth | `FcLimits.MaxCallDepth` |
| String growth | one string's length; characters all built-ins return in one run | `FcLimits.MaxStringLength`, `FcLimits.MaxTotalStringChars`, charged in `FormCalcBuiltins.Add`; `Replace` and `Space` check before allocating |
| Wide SOM matches | objects in one match list or `foreach` | `FcLimits.MaxListItems` |
| Nesting to reset the budget | `Eval` depth; a nested `Eval` or `resolveNode` gets what is left of the caller's steps, characters and time, and its spending is charged back | `FcLimits.MaxEvalDepth`, `FormCalcInterpreter.Remaining`/`Absorb` |
| Many scripts in one form | script runs per form; wall clock between scripts; calculate passes | `XfaScripts.MaxScriptRuns`, `XfaScripts.TotalTimeLimit`, `XfaScripts.MaxCalculatePasses` |
| The whole layout | wall clock plus the caller's token | `XfaLayoutOptions.TimeLimit`; the app passes `PdfDocumentService.XfaLayoutTimeLimit` |

Limits of the bounds: the clock is read between steps, never inside one built-in call, so a
single call on strings of `MaxStringLength` characters (`At`, `Replace`) runs to completion.
`TotalTimeLimit` is checked before each script, so a form can overrun it by one script's
`TimeLimit`. Memory is bounded through strings; objects and lists come from the form, which
`XfaBudget` already caps.

`FormCalcFuzzTests` holds the bounds: seeded random programs, every built-in with hostile
arguments, mutations of built-in scripts and of every `<script>` in the corpus's XFA forms, and
hand-made hostile shapes. Each input may only return or throw one of the two script exceptions,
and must stay inside its steps, characters, allocation and time.

**What a script can read.** Only what `IFcHost` and `IFcObject` expose: the merged form
(`XfaScripts.Host.ResolveRoot` answers `xfa` and `$form`; every other root, `$record`,
`$data` and `$host` included, is empty) and its field values and properties. The built-ins
are pure functions of their arguments except `Date`, `Time` and `Uuid`. There is no
reflection, no dynamic code, no file, network, clipboard, environment or host-application
access: `Get`, `Post`, `Put`, `xfa.host.*` and every other host method are absent, not stubbed
(`FormCalcInterpreterTests.HostFunctions_DoNotExist`, `NoBuiltinReachesTheOutsideWorld`).
The only method a script may call on an object is `resolveNode`/`resolveNodes`, and its
argument must be a plain SOM accessor.

**What a script can write.** A field's `rawValue` and any object's `presence`, through
`IFcObject.TrySetProperty`, in the in-memory form model only. Each script is a transaction
(`XfaScripts.RunOne`): a syntax or runtime error undoes its writes and is reported in
`XfaLayoutResult.ScriptFailures`. Any other exception type is a defect in the interpreter and
propagates out of `ApplyXfaLayout`; the app (`PdfDocumentService.LayOutDynamicXfa`) logs it and
opens the document with its own pages. A stack overflow cannot be caught, which is why every
recursion over script-shaped input is either depth-checked or a loop.

**Where scripts run.** Only in `PdfXfaLayout.ApplyXfaLayout`, and only when
`XfaLayoutOptions.RunFormCalc` is set; `FormCalcContainmentTests` fails on a new caller.
Redaction, save, printing and the command line never run a script.

**How output reaches redaction.** A value a script writes is laid out as ordinary page text,
so it is found, removed and verified like any other text; redaction of a laid-out form also
removes the whole `/XFA` packet, script source included (decisions 5 and 8,
`XfaFormCalcRedactionTests`, `XfaLayoutResult.FieldsWrittenByScripts`).

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

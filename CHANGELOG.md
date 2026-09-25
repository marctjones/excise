# Changelog

All notable changes to excise are documented here. Format roughly follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project uses
semantic versioning.

## [Unreleased]

### Added
- **Transparent PNG signatures and image stamps.** An image stamp now keeps its alpha channel as a real
  soft mask, so a PNG with a transparent background sits on the page instead of in a box, in excise and
  in every other reader (checked with mutool over a coloured page). A scan or photo of a signature on
  white paper offers to make the white transparent while keeping the ink, soft pen edges included. The
  picture keeps its own proportions inside the box you drag, instead of being stretched to fill it.
- **`Excise.Native`: a NativeAOT shared library with a stable C ABI over `Excise.Core`.**
  Open (bytes or path, with password), page count and size, text extraction, glyph-level
  text and area redaction, save, encrypt and decrypt, from C, Python, Swift or Rust through
  `include/excise.h`. Handle-based, no exception crosses the boundary (status codes plus
  `excise_last_error`), redaction that is not clean returns an error instead of success.
  Rendering is not included. Build with `scripts/build-native-lib.sh`; see `docs/native-api.md`.

### Changed
- **Digital-signature verification and application, and Bates numbering, moved from `Excise.App` into
  `Excise.Core`** (`Excise.Core.Signatures`, `Excise.Core.Editing`), so the CLI, other apps and the C
  library can use them. Logging is now an optional diagnostics callback instead of `ILogger`, and Core
  takes a `BouncyCastle.Cryptography` reference (the version the app already shipped). Behaviour is
  unchanged except that the app's signature diagnostics are all logged at Information level.

## [3.11.0] - 2026-09-24

### Added
- **FormCalc in dynamic XFA forms** (#1570). `initialize` and `calculate` scripts now run
  when a dynamic XFA form is opened, so computed fields show their values and scripts that
  show or hide a field are honoured. The interpreter is our own (a tree-walker under
  `Excise.Core/Xfa/FormCalc/`): no JavaScript engine, no reflection, and no network, file or
  host functions. Each script is bounded (steps, depth, string size, time) and a failing one is
  undone and reported without affecting the others. JavaScript, `validate`, `click` and
  `docReady` scripts still do not run. Redaction of a laid-out form removes the page text and
  the whole `/XFA` packet as before; a value a script wrote is derived data, covered by two
  new redaction tests. `XfaLayoutOptions.RunFormCalc` turns it off.
- **Fillable-field overlays are the field's own size and quiet until hovered** . A form's
  input boxes no longer inherit the theme's 32 x 64 minimum (on the IRS 1040, up to 18 dips too
  tall and 45 too wide over the fields around them) and sit at a faint tint and border until
  hovered or focused. Colours are unchanged.
- **Highlight/Underline/StrikeOut/Squiggly: arm the tool, then select** (#1793).
  Click the Highlight tool (Annotate menu, annotation toolbar, or floating
  palette), drag over text, release the mouse — the markup applies
  immediately, no separate "select, then click Add" step. Matches the
  arm-then-gesture pattern Square/Circle/Stamp (#1792) and the path tools
  already use. The direct-apply commands (apply to whatever's already
  selected) are unchanged and still available. Tool stays armed for marking
  up several passages in a row.
- **Interactive sticky-note popup** (#1788). Click a page point with the
  sticky-note tool to drop a note and type into it immediately; click an
  existing note's icon to reopen and edit it in place — the first
  edit-in-place path for an already-authored annotation. Spec-correct: each
  note gets a real linked `/Popup` annotation (ISO 32000-2 §12.5.6.14) with
  `/Parent`/`/Popup` cross-references and `/Open` state, so a note left open
  persists as open and the popup round-trips through other PDF readers, not
  just excise. The existing `Add _Sticky Note...` modal-prompt command is
  unchanged and still available.
- **Sticky notes now look and behave like a real post-it card** (#1794).
  excise's own viewer replaces the old ~17pt icon-with-glyph rendering with a
  post-it-sized card (default ~200x150pt) that shows its `/Contents` text,
  wrapped, directly on the card — both at rest (`SkiaRenderer`) and while
  being edited (the same card, in place, no separate popup or "Done"
  button). Clicking an existing, resting note's card edits its text in
  place; a press-and-drag on one moves it, updating its `/Rect`; Escape
  commits an open card alongside the existing click-away. Viewer-only: the
  underlying `/Text` + linked `/Popup` structure #1788 authors is unchanged,
  so other PDF readers still show the traditional small icon + popup until
  #1795 (a real `/AP` appearance stream) closes that gap.
- **`WidthPolicy.FixedMarker` (`redact --fixed-marker`)** (#1755). Closes the
  width gap like `--close-width` (destroying the content-stream residue
  #1715 measured recoverable at 91% recall@5 under the default, not just the
  rendered box) AND draws a covering box of one CONTENT-INDEPENDENT SIZE, so
  the redaction still leaves a visible mark (#1725: `--close-width` alone
  draws no box at all). **Opt-in, not the default**: measured with `mutool
  -F stext` (real glyph positions, not excise's own) to visually overlap the
  text the gap-closing shift reflows into place, in the common case rather
  than only when a line has little slack — the shift reused from
  `--close-width` moves the following text to the removed run's own left
  edge, and the marker is drawn from there out to a fixed width regardless
  of what that edge actually left available. Making this safe to default to
  needs the shift itself widened to make room for the marker; tracked as the
  remaining half of #1755.
- **A term that wraps across an ordinary line break (no hyphen) is now
  flagged, not silently reported as removed** (#1750). `excise redact
  ... "Betty Mary"` on a page reading "…signed by Betty" / "Mary on behalf
  of…" used to print `Redacted 0 occurrence(s)` and exit 0 while the name
  stayed fully readable — a silent false success, worse than the hyphen-wrap
  case #1372 already reported. Generalizes that detector
  (`FindHyphenWrappedCandidates` → `FindWordWrapCandidates`) to the far more
  common plain-word-wrap shape; the CLI now prints `NOT REMOVED
  (line-wrapped)` and exits **3** — not 0 — when a hyphen- OR line-wrapped
  occurrence survives (the pre-existing hyphen case exited 0 too, before this;
  both share the exit-code fix now). Scoped to this specific case only —
  general exit-code semantics are unchanged. Known limit, not yet closed:
  the detector still stops each side at the nearest blank, inherited from the
  hyphen case, so a THREE-OR-MORE-WORD name wrapping mid-phrase (e.g. "Mary
  Jane Smith" breaking after "Jane") is not yet caught — see
  `FindWordWrapCandidates`'s remarks.
- **Optional annotation toolbar row and floating tool palette** (#1789).
  Two new, independently toggleable, OFF-by-default ways to reach the 15
  annotation commands the Annotate menu already exposes — a second toolbar
  row and a movable floating palette, both in View ▸ (`Annotation Toolbar`,
  `Floating Annotation Palette`). Either, both, or neither may be shown; the
  Annotate menu is unchanged and remains the always-available fallback. Both
  bind directly to the existing `MainWindowViewModel` commands — no new
  command logic. The palette is an owned, non-modal window
  (`AnnotationPaletteWindow`) that closes with its main window rather than
  orphaning itself, and remembers its screen position; both toggle states and
  the palette's position persist in `window.json` the same way the other
  panel toggles do.
- **Printing on Linux, through CUPS** (#1710). `DocumentPrinterFactory`
  returned `UnsupportedDocumentPrinter` on Linux, so File → Print… could not
  print at all there; the README and `CLAUDE.md` both said Linux printing was
  "not planned". It now prints.
  - PDF is CUPS's native spool format and the print workflow already writes
    one per job (with pending redactions *removed* from the printed copy), so
    `LinuxCupsDocumentPrinter` enumerates queues with `lpstat` and hands the
    file to `lp`. No native dependency, nothing reflection-heavy for Native
    AOT.
  - Linux offers no system print dialog excise can call, so excise draws its
    own chooser: the queues with the system default preselected, copies with
    collation, and a page range that is validated against the document rather
    than silently turned into a different job.
  - `/P` gating is unchanged and platform-neutral — bit 3 and bit 12 both
    gate Print…, and a denied document causes no `lpstat` or `lp` subprocess
    at all.
  - Every failure is user-visible and distinct: CUPS tools missing, scheduler
    unreachable (quoting CUPS's own message), no queues, chooser failed, `lp`
    refused the job. A job that was not submitted is never reported as printed.
  - Scaling: *Fit to page* becomes CUPS's `fit-to-page`; *Actual size* and
    *Shrink oversized* send no scaling option, because CUPS has no shrink-only
    mode and claiming one would be inventing behaviour excise cannot deliver.
  - **What is tested.** Queue parsing, the `lp` command line, permission
    gating and every failure branch run on every platform against a fake
    process runner (`LinuxCupsDocumentPrinterTests`,
    `LinuxPrintPermissionGatingTests`). A real `cupsd` with an `lpadmin` queue
    is driven by `scripts/run-linux-print-test.sh` in a podman container, and
    the PDF the queue produces is counted by **qpdf/mutool, never by excise**;
    `--demo-failure` points that harness at a queue that does not exist, so it
    is known to be able to go red. Physical printer hardware and the chooser
    window itself are unexercised.

### Changed
- **The Windows installer and portable zip are Native AOT, like the macOS and
  Linux packages, and every release job proves its package is.** The Windows
  build was a single-file self-contained publish. `scripts/build-windows-installer.ps1`
  now publishes `Excise.App` and `Excise.Cli` with `PublishAot`, and
  `scripts/check-aot-payload.py` (run by every `release.yml` job on the unpacked
  package) fails the release on a managed assembly, the .NET runtime files, or a
  single-file bundle marker in the main executables. The Windows job also
  launches the installed CLI and GUI. Scripting is off in every shipped build
  (it already was for Release) and OCR still shells out to the system `tesseract`,
  so no shipped feature changes.
- **`excise unredact` focuses on TEXT recovery; the image and OCR channels are
  deferred, not deleted** (#1690). Product decision by Marc Jones, 2026-09-20.
  The attack that breaks excise's own guarantee is a text attack (the PoPETs
  2023 glyph-shift work, #1689), and text is the only channel with crisp ground
  truth: a recovered string either matches or it does not, and mutool and
  pdftotext can confirm it independently.
  - One authority, `Excise.Core.Redaction.Recovery.RecoveryChannelTiers`, keyed
    by channel name, so the engine, the CLI and the bench cannot disagree about
    what was graded. Tier 2 is exactly three channels: `ocr-differential`,
    `image-layer`, and the RASTER half of covered content (`covered-image`).
    The VECTOR half stays Tier 1.
  - **Deferred is not deleted.** Every channel is still implemented and still
    tested; the deferred ones are declared SKIPPED **with their reason and the
    flag that brings them back**, never silently absent. `--include-deferred`
    runs the image channels, `--ocr` the OCR differential; the library
    equivalent is `RecoveryScanOptions.IncludingDeferred`, and the default is
    the same in the library as in the CLI, deliberately (a per-caller default
    would rebuild #1665).
  - A deferred finding is **reported but not graded**: it appears in the
    finding list, the per-mark summary and the JSON, and it does not move
    `recovered` / `fullyRecoverable`. An OCR hit used to be counted there as
    "text present" — a recognition presented with the authority of a
    byte-for-byte recovery. The exit status is unchanged and stays paranoid.
  - Every model finding carries its `tier` (`text` | `deferred`) in the JSON.
  - A report that skipped a deferred channel prints **what it does not cover,
    next to its own result** — including next to the all-clear, which is the
    branch that matters. The wording is built from the channels actually
    skipped, so `--ocr` alone does not claim a blind spot the run does not
    have.
  - ⚠️ **The blind spot is narrow, and the broad version is wrong:** the hole
    is a page whose mark covers PIXELS with no surviving text layer beneath.
    A scanned page whose invisible OCR text layer survives under the box **is**
    recovered, by the Tier 1 hidden-text channel.
  - Bench: the unredaction scorecard and confusion matrix print a graded (Tier
    1) total and a separate, still-printed deferred block; `excise vs best
    reference` covers Tier 1 only. The benches keep MEASURING Tier 2 — a
    permanent zero is indistinguishable from a regression — except over the
    real-world NEGATIVES, which are scored at the product's default, because a
    mark holding any finding grades `candidates-only` rather than
    `not-recovered` and running the image channels over 113 clean filings would
    invent false positives the shipped command does not produce.
  - `README.md` documents `excise unredact` for the first time.

### Fixed
- **Saving, and `decrypt`, on encrypted files with a usage-rights signature** (#1823). Files such as
  the Canadian IRCC visa and work-permit forms opened and displayed but threw "The input data is not
  a complete block" on Save. A signature dictionary's `/Contents` is not encrypted (ISO 32000-2
  §7.6.2), and excise was AES-decrypting it. It is now left alone, recognised by its `/ByteRange` and
  `/Contents` pair because Adobe's usage-rights signatures carry no `/Type`.
- **Text markup landed off the selected text** (#1796). Highlight, Underline,
  StrikeOut and Squiggly applied in the continuous view (the default) were
  placed at the wrong position and size, scaled by the zoom factor (72 pt off
  at 150 %), and on the wrong page when the selection was not on the
  viewport's current page. The viewer now reports the selection with its page
  and coordinate space.
- **Sticky-note button on the annotation toolbar and palette arms the
  click-to-place tool** (#1796), like every other tool on those surfaces;
  it used to drop a note at a default spot on the current page before you
  pointed anywhere. The main toolbar's quick-add button and the Annotate
  menu's `Add _Sticky Note...` still add a note on the current page or text
  selection.
- **Pressing "Select Text Mode" while a markup tool was armed exited text
  interaction entirely instead of dropping back to plain selection** (#1793).
  `ToggleTextSelectionMode()` did a plain boolean flip; markup mode
  (#1793) is the one mode that sets `IsTextSelectionMode = true` itself
  (reusing the gesture) rather than turning it off, so the flag was already
  `true` when the toggle ran — flipping it landed on `false`, not back on
  plain selection.
- **Square, Circle, Text Box, Stamp and Image Stamp annotations had no
  working path through the GUI** (#1792). Clicking any of them from the
  Annotate menu, the annotation toolbar row, or the floating palette always
  produced "Drag a box on the page before adding a..." — no sequence of
  clicks or drags ever got past it. They had no drawing mode of their own:
  their Add\*FromDrag commands read the rect the *redaction* tool's drag
  gesture stages, reachable only via a genuinely-enabled Redaction Mode —
  and enabling that, then dragging a box, also immediately marked the area
  as a pending redaction and cleared the rect as a side effect. Now give
  these five the same one-drag-places-it interaction mode Ink/Line/Arrow/
  Polygon/PolyLine and the sticky note already have, with no redaction
  involved at any point.
- **A pushbutton's custom caption text survived a term redaction** (#1760).
  `excise redact ... toggled` reported success while a page kept rendering
  "This Button can be toggled" — a widget's `/AP/N` appearance stream commonly
  draws a caption as real glyphs even though the field has no `/V` value
  (`/V` on a Button is an on/off state name, never text). Two blind spots,
  same stale assumption ("a Button has no readable text"): `TextExtractor`
  never emitted letters for a Button field's caption, so the text was never
  a candidate match; `InteractiveRedactionScrubber` also skipped every Button
  field outright, so even a match would never have reached the appearance
  rewrite that removes it. Both now route Button fields through the same
  appearance-text machinery #669 already built for Signature fields.
- **A shared NAMED marked-content property list (`/Span /P1 BDC`) the scrubber
  correctly declined to touch — because another surviving span still
  references it — was silently left off the report** (#1599 follow-up; the
  scrub itself already reached the named form). The redaction now reports it
  as an explicitly refused carrier, so `IsCleanSuccess` reflects that the
  term's `/ActualText` is still reachable there, instead of a report that
  looks clean over a leak the engine chose not to risk over-removing.
- **A redaction leak assertion routed through a local variable was invisible
  to `check-redaction-oracles.sh`'s method-level gate** (#1786). The gate only
  matched a self-oracle extraction CHAINED onto its own assertion
  (`.Text.Should(...)`); a method that read the extraction into a variable and
  asserted a derived value several lines later went undetected — the same
  failure shape as #636/#608, one level down in the tooling meant to catch
  it. Along the way, found and fixed the reason the widened check needed
  `-v`→`ENVIRON`: `awk -v x="$shellvar"` silently drops a backslash before any
  character it does not recognise as an escape, which is why the new
  `GetPage(...)` pattern matched nothing at first and (unnoticed until now)
  is why the pre-existing `\.Text\.Should` pattern only worked by the
  accident of an unescaped `.` still matching a literal dot.
- **A CLI test running in a git worktree spawned the MAIN checkout's `excise`**
  (`UnredactOcrChannelTests`, `UnredactCarrierChannelTests`), so it exercised
  whatever branch happened to be checked out there and passed on code the
  branch under test does not contain. Build output resolves through the LOCAL
  checkout now — the #1527 rule, applied where it had not been.
- **`excise unredact --mode residue` declared six of fourteen channels
  skipped** and silently omitted the rest, so a report over one channel read
  like one over seven (the #1181 Coverage rule). The list is derived from
  `RecoveryScanner.Channels.All` now.
- **Three XAML resource references named keys nothing defines** (#1800).
  The modern-button hover border and the status bar's operation text
  referenced `AccentBrush`, and the Bates preview referenced
  `MonospaceFontFamily`. A `DynamicResource` that resolves to nothing leaves
  the property unset and reports nothing, so each was dead from the day it was
  written. The two brushes now point at the brand blue; the status text uses
  the darker shade (#005A9E) because #0078D4 on the status bar is under WCAG AA
  for its size. `MonospaceFontFamily` is defined in `App.axaml`. The hover
  border is still not visible: FluentAvalonia draws its own button state
  colours over it (#1801). New t0 gate
  `xaml-resource-keys` (with a selftest) fails on any `{DynamicResource}` /
  `{StaticResource}` key in `Excise.App` that no `x:Key` defines.

## [3.10.0] - 2026-09-17

Milestones **P1.1 — Redaction correctness: geometry, leaks, and fail-open
safety** and **P1.5 — Redaction policy and de-redaction side channels**.

### Changed
- **Redaction output profiles: Standard is the new default everywhere, and Maximum is an explicit choice** (#1586).
- **PDF/UA identification survives the metadata strip** (#1586, extending #1507).
- **`PdfDocument.ScrubMetadata` now clears EVERY `/Info` key**, not the §14.3.3 Table 349 list (#1583).
- **The carrier-scrub 3-character floor applies to `Strip` and not to `RemoveWhole`** (#1586).

- **Redacted output carries no attachments by default** (#1572).
- **Any redaction of a document with an XFA form removes the XFA packet** (#1574).
- **The thumbnail sidebar no longer pre-renders the whole document while you are waiting for the first page** (#1565).
- **The background thumbnail pre-render is OFF by default** (#1565).
- **The quiet period IS the existing idle delay** (#1565) — Preferences › Performance › "Idle delay (seconds)", 30 s by default, formerly labelled "Idle delay before releasing caches".
- **Pre-warming a thumbnail no longer builds three bitmaps to throw them all away** (#1565).
- **The search-index status no longer redraws the status bar once per page** (#1565).

### Fixed
- **`unredact` saw NOTHING on the Manafort filing: both detectors treated an unset fill colour as white rather than §8.6.8 black** (#1617).

- **A long dialog message pushed its own buttons off the window** (#1622).
- **The attachments warning is a persistent banner, not a 5 s toast** (#1619).
- **The hidden-layer removal stopped at the page** (#1586).
- **An `/Alt` describing a redacted image could not be checked, and was not reported** (#1586).
- **Maximum could take an attachment the caller asked to keep** (#1586).
- **The redaction covering box was untagged content** (#1586).
- **Redaction leaks in interactive carriers** (#1581), each confirmed with qpdf's object dump after redaction and each now clean: a widget's `/AA /K` JavaScript, a widget's `/AA /F` JavaScript held as a **stream**, a non-terminal field's `/A` JavaScript, a `/Launch` action's file target, the appearance stream of a widget flagged hidden, and a text field's **`/RV`** rich value.
- **Redaction leaks in document-level carriers** (#1583): custom `/Info` keys, structure-element `/T`, and `/PieceInfo` private data.
- **`KeepAttachments` silently lost a file to the action strip** (#1586).
- **Ctrl+Tab switches document tabs on macOS** (#1598).
- **Windows printing now honours each annotation's `/Print` flag** (#1573).
- **The pre-push gate checked the wrong commit range on a stepped push** (#1600).
- **Closing a document after an idle trim kept the whole document in memory** (#1564, #1543).
- **Opening a second document as a tab crashed excise on macOS** (#1584).
- **On macOS, excise ignored PDFs opened from Finder** (#1585).
- **Attachments on page annotations survived "attachments scrubbed"** (#1572).
- **A failed open left the previous document's attachments listed** (#1563), and opening another document kept the old list on screen until the new one finished loading.
- **Remove All Attachments claimed removals it had not made** (#1563).
- **Area redaction deleted the `pdfaid` XMP, so it could never produce a PDF/A-conformant file** (#1507).

- **A PDF/A-1 file whose XMP declares `pdfaid:part` as an ATTRIBUTE was not recognised as PDF/A-1, so every save path wrote object streams into it — which ISO 19005-1 forbids** (#1524).
- **The four independent readers of the PDF/A identification were consolidated onto one parser, with presence and value kept as separate, named questions** (#1526, the cause of #1524).
- **excise signed PDFs with a BER-encoded CMS object where ISO 32000 requires DER, and its verifier could not locate that object inside the padded `/Contents` value** (#1494) — two halves of one defect, both in the signature path.
- **A signer certificate that was outside its validity window at the claimed signing time was reported as a digest mismatch** (#1494) — BouncyCastle throws before computing the digest, so the report claimed tampering that was never tested for.
- **Redacting a form field value cost the file its PDF/A conformance** (#1499) — the interactive scrub ended with `/NeedAppearances true` whenever anything changed, and PDF/A forbids that entry (ISO 19005-2 6.4.1#3, ISO 19005-1 6.9#1), so a redacted PDF/A form stopped being PDF/A while its XMP went on claiming it was.
- **`PdfA()` plus a date field produced a file veraPDF rejected** (#1498) — `AddDateField` writes Acrobat `AFDate` format and keystroke actions, and PDF/A forbids JavaScript actions outright (19005-1 6.6.1#1, 19005-2 6.5.1#1).
- **A FreeText annotation with Arabic `/Contents` and no `/AP` rendered blank** (#1363) — the synthesised appearance refused any string that needs complex-script shaping, and drew only the first line of the rest.
- **The TJ array adjustment was applied raw instead of composed through the text matrix** (#1391) — §9.4.3's horizontal branch did `_tm_e -= tx` with no `·_tm_a` and no effect on `_tm_f`, three lines below an already-correct §9.4.4 glyph advance.
- **The saved-PDF leak scanner scanned the trailer `/ID`** (#1295) — a random 16-byte file identifier written as uppercase hex, which no page text can leak into, so a short ASCII needle collided with it and made short-term redaction assertions intermittently red (observed four times with a provably clean redacted page).

### Added
- **`excise unredact` reads every carrier a redaction can leave behind.** The certain channel used to read only structure-tree `/ActualText`/`/Alt`/`/E` and annotation `/Contents`/`/RC`.

- **Carrier traps for the unredaction scorecard and the redaction bench.** `CarrierTrapFixtures` generates one synthetic PDF per carrier above in memory.
- **Several documents at once, in separate windows** (#1463, #1551–#1553).
- **Attachments pane in the sidebar, visible by default** (#1563).
- **Dynamic XFA forms are displayed** (#1547, phase 2).
- **Reduce File Size** (#1550).
- **XFA forms are detected and explained on open** (#1547, phase 1).
- **Printing on Windows** (#1546).
- **Printing on macOS** (#1545, superseding #621's won't-fix).
- **`unredact` covers every redaction failure mode the matrix names** (#1592, #1606, #1607, #1608, #1609).

- **`unredact` says what could fit each redaction that HELD** (#1589) — character range from the font's narrowest and widest glyphs, pattern classes that fit (SSN, phone, date, amount, each tested by MEASURING a sample, since in a proportional font "ten digits" and "ten letters" are very different widths), dictionary candidates ranked by width error, and bits leaked as log2 of the admissible set, with plain wording — "fits exactly", "narrowed", "wide open".

- **The unredaction bench's real-world documents: a committed vetting record, a manifest-driven fetcher, and a gate** (#1591).

- **A tier-A unredaction bench** (#1590), generated at run time with exact ground truth, whose axes are DERIVED from the failure-mode registry so a mode cannot be silently omitted.
- **`unredact` has ONE recovery model, and it reports coverage rather than a finding count** (#1587).

- **Safe-redacted-copy refusal for unresolved `/Redact` annotations** (#1430) — the `redactionReviewDrafts.safeRedactedCopy` product-policy rule was unimplemented, so excise would produce output treated as safely redacted while content a reviewer had explicitly flagged was still fully present.
- **Hyphen-wrapped occurrences are now reported instead of silently missed** (#1372) — a term wrapped across a line (`Ander-` / `son`) is never matched and never removed, and excise used to report a clean success over it.
- **Queryable live performance metrics** (#1491) — the `Excise.Viewer` and `Excise.App` meters publish band and single-page render times, composite sizes, per-viewer cache bytes and counts, document-open phase timings, text-index progress and thumbnail renders.
- **Preferences → Performance** — trade memory and CPU against scroll-back speed from the GUI.

### Fixed
- **Preferences applied on a thread-pool thread and could be reverted on close.** The dialog's Save ran through `ShowDialog(...).ContinueWith`, off the UI thread; and `window.json` had two whole-file writers (window close saved its startup snapshot, document close did load/modify/save), so the last one silently reverted the other.

### Notes
- #1180 (unredact certain channel missing visible-but-readable failed redactions) does not reproduce and was closed.

### Added
- **Per-carrier redaction scrub scope and mode** (#1188, #1169).
- **Whole-word matching as an explicit option** (#1052, `--whole-word`, Preferences → Redaction → Match Rule).
- **`WidthPolicy.OvershootPreserveLayout`** (#1189, `--overshoot-box`, Preferences → Redaction → Covering Box Width).
- **Unicode control diagnostics at every identifier display** (#1205).

### Fixed
- **The `Annotations` carrier was silently overriding the `ActionUris` carrier.** A link annotation's `/A /URI` was scrubbed under the annotation carrier's scope and mode (a leftover from #1155 that #1168's complete URI walk made redundant).
- **Redaction policy preferences now persist across launches.** A security preference that silently reset to the less-safe default on every launch is worse than no preference at all.

### Fixed
- **A form field made a `PdfA()` document non-conformant by embedding no font for its own appearance** (#1435).

### Changed
- **BouncyCastle.Cryptography 2.6.2 -> 2.7.0** (#1494).

- **Redaction no longer rewrites the operators it did not touch** (#1093).

### Fixed
- **Closing, quitting, Ctrl+W, or opening another file discarded unsaved changes with no prompt** (#1233).
- **Ctrl+Z / Ctrl+Y / Ctrl+Shift+C were menu labels with nothing behind them** (#1170).

### Added
- **Drag a PDF onto the window to open it** (#1002).
- **Attachments panel** (#1414) — Document ▸ Attachments… lists files embedded in the PDF (name, decoded size, description), saves one to a chosen path, and strips them all.
- **Bates numbering reached the UI** (#1306) — Document ▸ Bates Numbering… stamps a sequential number on every page (prefix, suffix, start, padding, position, size, with a live preview).

### Removed
- **`RecentFilesService`** (#1307), a dormant duplicate.
- **`FdfSerializer` / `XfdfSerializer`** (#921), 1,782 lines reachable from no shipping surface.

## [3.9.4] - 2026-09-09

**Corrects [3.9.3]'s "Closed as not-reproducing" entry below for
#1431/#1432/#1434.** Those three genuinely do not reproduce on the `v3.9.3`
*sample* that was checked before closing — but that check used a lower-level
API entry point than the one the actual failing tests exercise. Reopened
after re-verification against a package built from `v3.9.3` itself (not the
orphaned `v3.9.2` lineage) using the exact call pattern the original reports
used, and this time it reproduced. Root cause and fix below. If you're
reading the [3.9.3] entry first: skip its "Closed as not-reproducing" note
for these three, the entry here is the current, accurate one.

### Fixed
- **`/TU` tooltip, embedded font name, and `/Widget` annotations invisible to a raw-byte scan of the saved file** (#1431/#1432/#1434, one root cause).
- **The size-budget gate didn't watch its own worst case.** `Pdf15Save_SmokeCorpusCompressedOutputStaysUnderSourceSizeBudget` held three fixtures to a 1.20 compressed/source-size ratio, but the fixture most affected by the fix above — `irs-1040.pdf`, the corpus's most form-heavy document — wasn't in the list, and had silently reached 1.33× once the AcroForm carve-out landed.
- **No packable project shipped its XML doc file.** `Excise.Core`, `Excise.Rendering`, and `Excise.Avalonia` now set `GenerateDocumentationFile`, so a `///` comment actually reaches a NuGet consumer's IntelliSense instead of being silently dropped at compile time.

### Added
- **`ContentTransform.TransformPoint(x, y)` is public** (#1436), a follow-up to #1433.

### Investigated, no code change
- **#1437** (direct embedded-image extraction, for OCR input) — measured rather than assumed: `SkiaRenderer.RenderPage()` is bit-exact at a scanned page's native resolution (120,000/120,000 pixels identical, max per-channel delta 0, across RGB/1-bpc-bilevel/DCT fixtures), and that resolution is derivable from existing public API (`page.GetXObject(name).GetInt("Width")`).

## [3.9.3] - 2026-09-09

**Read this before upgrading from any 3.9.x build.** The `v3.9.2` git tag
sits on a lineage that is not an ancestor of `develop` — the two histories
share only a June 2026 common ancestor, discovered while investigating
issues #1431/#1432/#1434 (below). Nobody currently knows why or how the
divergence happened; it predates this release. If you consumed a package
built from `v3.9.2` (or any earlier `v3.x` tag) and hit a regression, a
rebuild from `v3.9.2` will still show it — the fix is here, on `develop`,
not on that lineage. `v3.9.3` is cut directly from current `develop`.

### Fixed — security
- **Four real redaction-content-carrier leaks**, each independently oracle-verified (qpdf/mutool), each with a `CanaryInjectionLeakTests` regression pin: AGL underscore-joined ligature names weren't decoded before matching, so a redacted term survived under an affected font (#1423); annotation `/Subj` and `/Redact` annotations' `/OverlayText` weren't in the document-carrier scrub list at all (#1427); `/FileAttachment` annotations' own `/FS` was invisible to `GetEmbeddedFiles()`/redaction — only catalog-level `/Names/EmbeddedFiles` and `/AF` were ever walked (#1428); annotation `/AP` appearance-stream text (Stamp, Watermark, and others) was rendered but not extracted, so it survived a redaction that removed the same text from the page body (#1429).
- **A decompression-bomb guard on Flate/LZW decode** (#1408).
- **`Tc`/`Tw` (character/word spacing) silently dropped or blown off-canvas within a `Tj` string** (#1392) — two distinct bugs in `SkiaRenderer.Text.cs`: with no `/Widths` array (the common case for base-14 fonts, which aren't required to carry one), spacing had zero effect on glyph layout; with `/Widths` present, spacing was incorrectly scaled by font size a second time, pushing glyphs off-page under a large `Tw`.
- **`/ImageMask true` stencils with no explicit `/BitsPerComponent` rendered as a blank page.** Per ISO 32000-2 §8.9.6.2 the key "shall not be specified" for a stencil mask (implicitly 1) and real producers commonly omit it; the renderer defaulted the missing key to 8 before checking `/ImageMask`, so the 1-bit decode path never triggered.

### Fixed — performance
- Three DeviceCMYK rendering hot-path fixes: reading the ICC profile PCS field so XYZ-PCS lut16 profiles decode correctly instead of as Lab (#1424); caching the vector CMYK→RGB conversion through the image lattice instead of per-pixel ICC (#1425); converting a per-pixel `GetPixel` call in the CMYK backdrop sync to raw spans (#1426).

### Added
- **Outline (bookmark) and embedded-file (attachment) authoring APIs** (#1412, #1413) — `PdfOutlineAuthoring`/`PdfOutlineParser` and the embedded-file authoring surface.
- **A save-warning before overwriting a digitally signed document's edits** (#1415).
- **`ContentOperator.GraphicsTransform` now populates for every content-stream operator**, not only text-showing ones, and is public along with the `ContentTransform` struct it uses (#1433).

### Registry
- The PDF capability registry's independently-oracled evidence grew substantially this cycle: **verified capability/mode pairs 71 → 209 (7.9% → 23.3% of 898 target modes)**, via a large parallel verification pass (six concurrent audits, one per registry section) plus the earlier RC22 milestone.

### Closed as not-reproducing
- #1431 (`/TU` tooltip not written), #1432 (PDF/A archival output not embedding a Unicode font), #1434 (exported fillable PDF missing `/Widget` annotations entirely) — all three were real regressions on the orphaned `v3.9.2` lineage described above, but do not reproduce on `develop`; verified with real repros against excise's actual public API before closing, not assumed fixed by association.

## [3.9.2] - 2026-09-05

151 commits since 3.9.1. Nothing user-facing changed in the PDF engine; this
release is testing infrastructure, one GUI capability that was implemented but
unreachable, and the measurement work that found several real defects.

### Added
- **Sign Document** in the File menu (#1308).
- **A registry of every interactive GUI function** (#1374): 93 menu items, 45 toolbar buttons, 9 viewer mouse gestures and 28 keyboard shortcuts, each joined to the command it invokes, and gated so a command cannot silently become unreachable.
- **Every control now carries an automation identity** (#1375), including the parameterised ones — 15 stamp items and 8 colour swatches share a verb and record their parameter values.

### Build and verification
- **GitHub Actions was removed.** All gates now run locally on macOS (`LOCAL_GATES.md`); `CI_GATES.md` is gone and `tests/coverage-floors.tsv` keeps only the `full` profile.
- **Every gate is one row of `tests/gates.tsv`** (#1358).
- **The report decides every runner's exit** (`scripts/report-gates.sh`): a NEW red blocks, a KNOWN one (an OPEN issue cited in the row's `knownIssue` cell) does not, and a cited issue that has CLOSED reads STALE and blocks until the acceptance is deleted.
- **The registry gate reads its test evidence instead of regenerating it** (#1366): the t0 `pdf-capability-registry` row regenerates from committed inputs and reads the test-outcomes snapshot, so a run's own trx files can no longer redden it; a full-tier GRADE row, `pdf-registry-outcomes`, imports the run's trx from its ledger, prints the report's `test evidence` line and stashes the regenerated snapshot for `--adopt` and commit.
- **The `--no-build` freshness guard reads solution files** (#1367): it crashed on `excise.sln`'s backslash project paths, and the runner read the crash as "stale", so the redaction suites did not run in the first manifest-driven full run.
- **Check-gates leave mtimes alone.** `verify-license-manifest.sh` restored the reviewed licence file with a fresh mtime on every pass, and the guard then refused every later `--no-build` row that reaches `Excise.App` — all sixteen App rows of the first full run failed on a file whose bytes had not changed.
- **What the first full run (2026-09-05, 4h01m) accepted or re-tuned:** the #1361 acceptance now also covers the chunked `Excise.Rendering.Tests` row (one defect must not read KNOWN on one row and NEW on another); the Rendering skip allowlist is re-tuned to this machine (two entries added with their reasons, one deleted because the test runs again, one count corrected); two PDFium corpus pages moved PASS_ONE → PASS and are accepted; `render-quality-scan` cites #1370 for its 47 contract departures and records its 2h28m cost.
- **`--resume` keeps trx consumers pointed at the evidence.** A row that reads another row's trx (`{TRX:x}`, `{TRXARGS:x}`: test counts, skip budgets, oracle floors) resolved it under the new run's log directory even when the producer had been taken from a checkpoint, so every such row failed on the first resumed full run.
- **PDFium never runs inside our process** (#1369).
- **The redaction bench measures with two engines on every axis** (#1372): leaked text by mutool and pdftotext, mark geometry by pdftocairo and PDFBox, visual survival by Ghostscript and pdftocairo.
- **`--suite` presets** name a slice of tier full (`redaction`, `rendering`, `benches`, `suites`, `gates`) as patterns over manifest row names, so a new row joins its suite automatically.
- **Silent skips are closed.** A gate that cannot run exits 77 and is reported SKIPPED or FAIL according to its `prereqPolicy`; five scripts that exited 0 on a missing prerequisite (accessibility, visual, perf budgets, copy-whitespace parity, bench tiers) no longer do.
- **Removed** the eight legacy runners (`run-all-tests`, `run-atomic-tests`, `run-passing-tests`, `run-avalonia-tests-linux`, `run-coverage`, `run-long-tests`, `run-corpus-tests`, `run-automation-tests`); what they ran is now manifest rows.
- **The macOS NativeAOT publish builds again.** Homebrew keeps OpenSSL and brotli keg-only, so the ilcompiler link line could not resolve `-lssl` or `-lbrotlienc`; the AOT release gate had been red on this since the toolchain bump.

### Architecture and registries
- A checked architecture registry with Roslyn-derived code topology, member-level reachability, XAML structural resolution, and observed boundary drift (#1234, #1247, #1248, #1300-#1304, #1316).
- PDF capability evidence scorecards, implementation-evidence progress scoring, and test/benchmark attribution.

### Fixed
- **Encrypted assembly output is preserved** through CLI `merge`/`split` (#1343).
- **Mutation-derived state is invalidated** rather than served stale (#1326).
- **Editing-mode cleanup is idempotent** (#1268).

### Known problems at this point
- The redaction bench segfaults the test host (exit 139) 13 minutes in under the full tier, so the `redaction` grade restates the 2026-08-27 history (#1369).
- The t0 registry gate still depends on four inputs that are not committed sources: a gitignored index file, the installed-tool probe, a test that reads `logs/`, and a solution-wide trx that keeps one project's results (#1368).
- 47 render-quality contract expectations depart under the full tier's five-oracle set; triage decides whether the pins or the oracle set change (#1370).
- The capability registry measures whether evidence paperwork exists, not what is implemented: its `testRefs` cannot join to real test names at all (#1344), and 929 of 964 modes read `unknown` because nobody filled a form (#1345).
- Rendering is 3.5-7x slower than mutool on heavy pages; 72% of the Altona render is per-pixel colour conversion (#1350).

## [3.9.1] - 2026-08-29

### Security and redaction
- **Redaction now handles image-only OCR overlays and region-level JBIG2/image redaction**, with independent extractor, rendered-ink, carrier, and save/reopen assurance gates.
- **Encryption identity crypt filters and non-display carriers are preserved or scrubbed according to explicit policy**, preventing previously unexamined secret-bearing paths from being reported clean.

### Text, copy, and accessibility
- **Copy/selection quality has new reading-order and Unicode-safety coverage**, including multi-column text and unsafe display-control diagnostics.
- **Automation and accessibility release smoke remains CLI-first and platform-neutral**, with no runtime scripting compiler in shipped AOT builds.

### Performance and verification
- **A fresh-process renderer benchmark now compares Excise with independent renderers** and records wall time, RSS, and fidelity separately, including ACC and Altona stress fixtures.
- **Release documentation no longer embeds a stale version number in command examples.**

### Fixed
- **An unsigned `/FT /Sig` widget no longer gets a placeholder border** (#1005).

- **A `/MK` with a background and no border colour no longer gets an invented border** (#1005, same code path).

- **`/DA` auto-size (`0 Tf`) fits the value to the field** (#1003).

- **A synthesized `/Highlight` overshoots its quad by the quad's height ÷ 5, not by half its height** (#1004).

- **A link with no `/Border` and no `/BS` gets the §12.5.6.5 default 1 pt border** (#987).

- **A negative `Tf` size in a widget's `/DA` is a real size, not "auto-size"** (#991).

- **A synthesized field value is clipped to its widget's `/Rect`** (#991).

### Added
- **`tests/annotation-synthesis-policy.json` — appearance synthesis as data, with its evidence attached** (#993). 45 rows, one per (subtype, state, condition), each carrying the decision, the shape drawn, the fixture in full, per-oracle evidence as **bbox + shape rather than a bare count**, and the majority verdict with the size of the pool it was taken over.

### Changed
- **There is now exactly ONE content-stream state machine** (#992, #995, #996, #997).

- **The ExtGState `/Font` entry (Table 58) is implemented** (#990).

### Removed
- **`ParserDifferentialTests` (58 tests)** (#997).

## [3.8.0] - 2026-08-12

One theme: **annotations, finished and independently verified.** The app could
author 2 of the 15 annotation types its own engine supported; it now authors all
15, and — more to the point — the suite can now tell when one of them is wrong.

### Added
- **All 15 annotation types are reachable from the Annotate menu** (#912, #934).

- **`PdfAnnotation.LineEndings`** exposes `/LE`, so an arrow is now readable and not merely writable.

- **An independent structural oracle for annotations** (#933) — `QpdfReferenceTool.ListAnnotations` reads the object graph with qpdf's own parser.

### Changed
- Annotation structural invariants grew from 37 cases to 58 (#933), covering every subtype the authoring API can produce except Stamp and ImageStamp, which are covered by GUI tests instead.

### Notes for anyone reading the tests

- the image stamp passed with red and blue swapped — *a Stamp exists and the file grew* is true of a picture with the wrong colours;
- *a non-empty `/InkList`* is true of a reversed stroke and a decimated one;
- the distinct-subtype guard used by every earlier type is blind to Arrow by construction, since an Arrow **is** a Line;
- a vertex list that resets on each click still yields a valid shape, just one built from the last click or two.

### Known limitations

- **The corpus gate still judges annotations by a single most-inked oracle** (#932, #907) — so ~21 pages remain pinned as excise defects for *agreeing with the majority of renderers*.
- **A document is still opened twice** (#917) — every annotation type added this release has to write itself to both the save document and the viewer document by hand, or the saved file is correct while the screen never changes.

## [3.7.0] - 2026-08-11

Two themes. The first is the continuation of 3.6.0's corpus work — renderer and
parser defects found by measuring against independent renderers, not by user
reports. The second is **redaction honesty**: excise used to report unqualified
success over document carriers it had never examined, and now says what it did
not look at.

**A caution carried forward from 3.6.0, because it still applies.** The corpus
expectation manifests are a ratchet recording each page's current status, not a
quality result. A green gate means "nothing regressed". The gate itself has a
known defect this release did not fix — see *Known limitations* below.

### Added
- **Redaction now reports the carriers it could not examine** (#916, #905) — bookmark titles carry no position, and annotations away from the redaction box are never visited, so an area redaction cannot know whether either mentions what it removed.

- **Underline, StrikeOut and Squiggly annotations from the GUI** (#912) — Core could author fifteen annotation subtypes and the app exposed two.
- **Coverage floors ratchet up** (#909) — `check-coverage-floor.sh --update` raises a floor when coverage improves materially and **never** lowers it; a regression exits non-zero and leaves the file untouched.

### Fixed
- **Area redaction left `/Info` and the XMP packet intact** (#897) — draw a box over a name, save, and the name was still in the document title.
- **The carrier scrub ignored the caller's case sensitivity** (#905) — `RedactText` matches page content case-insensitively by default while the scrub was `Ordinal`, so redacting `smith` cleared the page and left `Smith` in `/Info /Title`.
- **Default and regex search silently missed visible text** (#924) — both read `PdfPage.Text`, which drops content on multi-column pages; only "whole words only" read the complete word list.
- **OCR could hang the application indefinitely** — `PdfOcrService` read tesseract's stdout to end and only then its stderr, which deadlocks once the child fills the stderr buffer, and then waited with no timeout at all.
- **The AOT publish warned about the artifact it produces** (#906) — four IL3050 and two IL2026 warnings, now zero.
- **Renderer and parser defects found by the corpus scan** — annotation appearances for button widgets and FreeText, and `/AP` appearances that were present and ignored (#885, #888); `/Font` in an `ExtGState` (#886, 9 pages); the CFF standard-strings table held 244 of 391 entries (#886); format 4 and 6 symbolic TrueType cmaps (#891); two name→GID routes that did not exist (#892); the page group starting opaque instead of transparent (#890); inline images whose `ID` is followed by CRLF (#887); JBIG2 `/JBIG2Globals` resolution and sequential halftone MMR plane decoding (#874); CCITT `/EndOfBlock` classified by value rather than presence (#893) and `/EncodedByteAlign` correctly reported as Group-4-only; four parser recovery gaps, one of which returned the wrong object (#869, #884).
- **The viewer rendered the same page once per grid cell on first paint** (#855).
- **Two internal gates were reporting the wrong thing.** The `ci` coverage floor was derived on a developer machine — applying CI's test filter locally does not reproduce CI's environment, because 86 corpus-gated tests skip there without announcing it, and the resulting 24-point error kept CI red for four commits.

### Removed
- **292 lines of unreachable page-render machinery** (#920) — the legacy ViewModel render path, bypassed when the bound viewer control took over display rendering and never deleted, along with an entire adjacent-page prefetch feature reachable only from it.

### Known limitations

- **Open-and-save inflates every PDF, up to 2.79x** (#923) — the writer emits no object streams or cross-reference streams, so a document opened and saved with **zero** edits grows.
- **The corpus gate is one-directional** (#904, #907) — over-draw is computed and never gated, and under-draw is scored against whichever single oracle drew the most ink. 21 of 35 defect-class pages are that scoring, not excise bugs.
- **Redaction is slow on common terms** (#919) — 7-10 seconds to redact a frequent word from a six-page form, because each match re-extracts every letter on the page.
- **A document is opened twice** (#917) — two `PdfDocument` instances kept in sync by hand.
- **Multi-column text assembly still drops content** (#899) — the letter stream is complete; the loss is in serialising it to a string.

## [3.6.0] - 2026-08-03

This release is dominated by one thing: excise's renderer and parser were
measured, page by page, against independent renderers across all four test
corpora (3,915 documents), and the defects that measurement exposed were fixed
at the root. Seven of the parser/renderer fixes below (#871, #872, #873, #878,
#881, #884, #885) were found that way, not by a user report — as was a bug in
one of the reference oracles itself (#868).

**What the corpus gate does and does not claim.** The checked-in expectation
manifests (`tests/corpus-expectations*.tsv`) are a **ratchet recording each
page's current status**, not a quality result — `update-corpus-expectations.sh`
writes every status verbatim, including statuses that encode an excise defect.
A green gate means "nothing regressed", not "every page is correct". The
remaining known gaps are tracked and still open: #874 (JBIG2), #884 (14 parser
refusals), #885/#888 (annotation appearances), #886 (embedded-subset code→GID),
#887 (a 14-page blank tail), #875 (one ambiguous page).

### Fixed
- **Hybrid-reference files resolved to a SUPERSEDED revision** (#872) — a PDF written with both a classic xref table and a cross-reference stream (PDF 32000-1 §7.5.8.4) points at the stream from its trailer's `/XRefStm`, which must be consulted *before* `/Prev`.
- **Colour-key `/Mask` was silently ignored** (#873) — an image declaring a colour-key mask (§8.9.6.4: an array of sample ranges that must not paint) had every masked sample painted opaque, so pages that use it to knock out a background rendered with solid blocks over content.
- **A failed image decode fabricated a uniform fill instead of failing visibly** (#878) — when a decoder returned fewer bytes than the image geometry requires, the renderer painted the undersized buffer anyway, producing a plausible-looking flat colour where the real image should be.
- **A self-referencing `/Parent` hung inherited-attribute lookup** (#881) — a page whose parent chain cycles made the `/Resources`, `/MediaBox` and `/Rotate` inheritance walks loop forever on untrusted input.
- **Four parser refusals that condemned whole documents** (#884, partial — 36 pages → 14) — each was excise refusing a file that Poppler and MuPDF read without complaint: an undefined indirect reference now resolves to null per §7.3.10 (a free xref entry already did); a missing `endobj` keeps the object it already parsed; a `/Length` that overruns the file yields truncated stream data rather than throwing; and a page with no `/MediaBox` anywhere in its ancestry defaults to US Letter (measured from pdftocairo) instead of being refused.
- **11 unhandled parser exceptions on untrusted input** (#871) — including an `OverflowException` from an out-of-range object number in an xref header scan.
- **Line, Polygon, PolyLine and Ink annotations were invisible without an `/AP`** (#885, partial) — §12.5.5 lets a viewer synthesise an appearance when the annotation carries none; excise drew nothing.
- **Continuous view: a page's top strip stayed blank while scrolling** (#848, #849) — content-addressed tiles are now composited into one bitmap per page.
- **Selection drag jumped to the wrong line** (#845, #850) — the drag hit-test anchors to the pointer's own line rather than an X-closer neighbour on an adjacent line.
- **GUI tests leaked every window they opened** (#706) — `MouseInputTests` called `Show()` thirteen times and `Close()` zero times; `PointerInteractionTests`, eight and zero.
- **Binary fixtures were being corrupted on Windows checkouts** — the repo had no `.gitattributes`, so `core.autocrlf` rewrote LF to CRLF inside files git guessed were text.
- **PdfBoxReferenceRenderer returned the wrong page** (#868) — it matched no shipping PDFBox version, so an oracle the suite was about to start trusting would have corroborated the wrong thing.
- **Fit-Width / Fit-Page now handle mixed portrait+landscape documents** (#847) — the "Fit" toolbar button fitted only the *current* page's width, so in a document mixing portrait and rotated/landscape pages (which share one zoom in the continuous view) a wider page overflowed the viewport and pages shifted off-center.
- **Glyph rectangles: correct width/height (matrix scale) and resolved `/Widths`** (#833, #843) — two independent extraction-geometry bugs that gave wrong glyph bounding boxes (feeding copy spacing, selection highlights, and redaction boxes).
- **Copied text no longer fuses words on tight-tracking lines** (#835) — the word-space heuristic judged the horizontal gap against the glyph *height* (`0.5·lineHeight ≈ 0.5em`), a bar above a normal word space, so lines that position words with small gaps and no real space glyph fused (`ForewordItisagreat…`).

### Added
- **The corpus rendering scan is now a gate** (#862) — page 1 of all 3,915 documents across four corpora (veraPDF 2694, pdf.js 685, Isartor 205, PDFium 331) is rendered and classified against up to five independent oracles, then checked against a per-corpus expectation manifest.
- **PDFium and PDFBox are real oracles now, not decorative ones** (#857) — the file map advertised six reference renderers; two were referenced by zero tests (PDFBox) or only by argument-string unit tests that never invoke a binary (PDFium).
- **Refusals are corroborated instead of assumed** (#882, #877) — the scan reported `AGREED_REFUSAL` on pages where **the oracles were never invoked**, so "no renderer could open this" was an assumption, not a measurement, and it masked excise-only failures.
- **The gate can detect small missing content** (#883) — it previously compared excise against whichever oracle was *closest* to excise, which is backwards: adding oracles made it detect **less** (three genuinely-missing-content pages flipped to PASS).
- **A restartable, memory-bounded full-suite runner** — `scripts/run-full-suite.sh --resume` checkpoints per step so a 30-minute run survives interruption.
- **The skip allow-list is environment-conditioned** (#854) — entries may declare `[requires: tool:NAME corpus:NAME env:NAME]`.
- **Bomb and implementation-limit fixtures are tested for what they are FOR** — Isartor contributed 10,223 of the 14,589 pages across all four corpora, and almost all of them are a single PDF/A-1b implementation-limits *violation* fixture — one file whose 10,000 near-identical-by-construction pages are 69% of every page in every corpus.
- **Corpus scans no longer lose work or collide** (#879, #880) — a chunk timeout discarded every page it had already completed; pages are now published as they finish.
- **Live visual-mutation trace harness** (#695 Phase 3 / #846) — `scripts/run-visual-mutation-trace.sh` drives a page mutation (rotate/remove/move/zoom) then a scroll sweep, zoom, and save in the **real running app** (where the compositor re-renders the continuous view, unlike the headless host), capturing a PNG per frame plus an ink-centroid trajectory and a per-phase stability summary.
- **GUI expected-effect registry** (#695 Phase 2) — builds on the Phase 1 sweep with a per-command contract: for 16 view/zoom/navigation/mode/panel-toggle commands, clicking must keep the page surface inked, flip exactly its declared panel (Outline / Thumbnails / Clipboard / Search), and leave every OTHER panel's visibility unchanged — the "a click changed the wrong region" guard Phase 1 is blind to.
- **Universal GUI click-safety sweep** (#695 Phase 1) — a headless test enumerates every command-backed leaf Button/MenuItem in a real MainWindow with a document loaded and *invokes* each one (40 commands today), asserting none throws and the app still renders a document afterward.
- **Text selection spans pages in the continuous reading view** (#832) — a drag that starts on one page and ends on another now selects across the boundary instead of being clamped to the anchor page.
- **Selection highlights are guarded against the invisible-sliver regression** (#840) — a headless test drives the full continuous-view pipeline on a font with an all-zero `/Widths` array (glyphs extract at ~0 width) and asserts the RENDERED highlight `Rectangle` visuals are at least half a glyph-advance wide, not slivers.
- **Copy-parity now measures reading ORDER, not just token sets** (#838) — the copy-whitespace gate scored word/line agreement with **order-insensitive** multiset Jaccard, so a reading-order regression (multi-column scramble) was invisible — the #774/#824 fix did not move the numbers at all.
- **Parity gates can no longer green vacuously on a tool-less runner** (#841) — `check-copy-whitespace-parity.sh` skipped (exit 0) when poppler or the corpus was absent, so on CI it measured nothing while reporting success.
- **Redaction/extraction geometry now has two independent-oracle regression gates** (#842, #839) — both guard the glyph-box path the #833/#843 fixes touch, and neither lets excise grade its own homework.
- **Copy-whitespace parity is now a ratcheting CI gate** (#837) — the harness that measures copied-text word/line agreement against poppler `pdftotext` (`CopyWhitespaceParityHarness`) now enforces per-document floors from `tests/copy-whitespace/floors.json` and fails when a score regresses; `scripts/check-copy-whitespace-parity.sh` (wired into tier `t1`) runs it and skips loudly when `pdftotext`/corpus are absent, mirroring the extraction-parity gate.

### Fixed
- **Full-width headers/footers no longer scramble two-column copy** (#774/#824) — a continuous full-width running header, footer, title, or page-number line spanning the column gutter used to fill the horizontal sweep, so column detection found no gutter and the page copied in woven row-major order ("colA-line1 colB-line1 colA-line2 …").
- **Copied text rejoins soft (line-break) hyphens** (#836) — in Smart mode (the reader-friendly default), a hyphen at a line end followed by a lowercase continuation is rejoined (`unfamil-\niar` → `unfamiliar`), matching `pdftotext` and most readers; guarded so ranges, capitalised continuations and paragraph-break hyphens stay intact.
- **Degenerate glyph widths no longer break copy spacing or hide selection highlights** (#833) — some TrueType-subset fonts (e.g.

## [3.5.1] - 2026-07-28

### Fixed
- **macOS app menu now shows "About Excise" and opens the app's own About dialog** (#834) — the bold app-name menu was showing Avalonia's built-in default "About Avalonia" (which opened the framework's `AboutAvaloniaDialog`), not Excise's.

## [3.5.0] - 2026-07-27

### Added
- **Text selection is on by default in the reading view** (#831) — selecting text is now the resting affordance of the viewer: open a document and drag, and it selects, exactly like every other PDF reader — no "Select Text" mode to hunt for first.
- **Pointer-interaction test coverage + thumbnail drag-reorder fix** (#827, batch A) — new headless-Avalonia suite `PointerInteractionTests` that drives the *real* pointer/keyboard gesture on the *real* control and asserts the downstream effect (never the VM method directly) for eight surfaces that had only command-level or no coverage: external-link click (fires `ExternalLinkClicked` + runs the confirm dialog), dangerous `/Launch` link click (fires `DangerousLinkClicked` + runs the refusal), FormAuthoring drag-to-create (fires `FormFieldRectDrawn` with a Y-flipped PDF-point rect on the correct page), form-field checkbox toggle (fires `FormFieldEdited`, mutates the `PdfField`, marks the document dirty), thumbnail drag-reorder (+ the `from == to` no-op), thumbnail click-navigate, thumbnail batch-select checkbox, and search-result row click.
- **Ctrl+wheel zoom and middle-button pan in the PDF viewer** (#827) — the viewer now handles the mouse wheel directly: **Ctrl (or ⌘) + wheel** zooms in/out (reusing the existing 25%-step, min/max-clamped zoom), while a **plain wheel** still scrolls natively and is never consumed.
- **Copied-text whitespace fidelity: paragraph + list awareness, as the default** — copying text now inserts a blank line at detected **paragraph** breaks (a vertical gap meaningfully larger than the block's typical leading) and keeps **bullet/numbered lists** (•, -, –, *, `N.`, `N)`) on tight, own-line items with their indentation preserved, so a copied list still reads as a list.
- **Column-aware reading order for text copy, as the default** (#774) — copying a selection that spans a multi-column layout (or a whole multi-column page) now yields all of column 1 top-to-bottom, then column 2, instead of interleaving the columns line-by-line across the gutter.
- **About dialog now carries a completeness-gated third-party license manifest** — the About dialog already listed every shipped NuGet package (name, version, SPDX id, copyright, verbatim license text) from the embedded `Excise.App/Assets/third-party-licenses.json`, but nothing guaranteed the list stayed complete.
- **Interactive GUI tests for file-ops toolbar/menu commands** (#816 batch 2) — `Excise.App.Tests/UI/FileOpsCommandTests.cs` executes the real `ReactiveCommand` behind Save/SaveAs/SaveFlattenedFormCopy/Open/LoadRecent/ ExportCurrentPage/ExportPages/Print and asserts the effect (bytes on disk, loaded-document state, exported PNGs, or the shown dialog message), closing a false-coverage gap where these were previously only exercised via their underlying async method with an already-known path — a mis-wired command would have passed every existing test.
- **Text selection works in the continuous reading view** (#815) — text selection used to be a mode toggle that forced single-page layout, so the default continuous reading view had no way to drag-to-select and showed no highlight.

### Fixed
- **Three menu keyboard shortcuts were advertised but did nothing** (#827) — `Ctrl+E` (Export Current Page), `Ctrl+,` (Preferences), and `Enter` (Apply Redaction) each showed an `InputGesture` in the menu but had no key handler behind them (Avalonia's `InputGesture` is display-only — every *working* shortcut is explicitly duplicated in `MainWindow_KeyDown`).
- **Keyboard shortcuts now have effect-asserting GUI coverage** (#827) — the new `KeyboardShortcutEffectTests` suite dispatches the REAL key (raw headless input for window shortcuts; routed `KeyDownEvent` for viewer/search controls) and asserts the RESULTING EFFECT — loaded/closed document, advanced search index (`CurrentSearchMatchIndex` F3/Shift+F3), rotated page (Ctrl+L/R exact angle), toggled sidebars (Ctrl+Shift+O/T), clipboard-history copy (Ctrl+C), print-explanation dialog (Ctrl+P, #621), preferences/shortcuts dialogs (Ctrl+,/F1), pending-redaction apply (Enter), viewer page nav (Left/Right) and tagged-heading nav (H/Shift+H) — rather than the old `Command.Should().NotBeNull()`, which passed even when a key was unwired (see the three-bug fix above).
- **Text-selection highlight now has automated GUI coverage** (#815) — the single-page "selection box" drawing was untested at the GUI level, so any coordinate/z-order regression (e.g.
- **Interactive GUI tests for page-organization toolbar/menu commands** (#816) — a new `PageOrganizationCommandTests` suite executes the real `MainWindowViewModel` commands a user clicks (Combine, Split, Add/Insert Before/After, Extract Current/Selected, Remove/Move/Clear Selected, Move Current Earlier/Later, Rotate Left/180) and asserts the resulting page order, count, rotation, or saved-file content — closing an audit gap where these effects were proven only by calling the underlying async methods, so a button mis-wired to the wrong method would have passed.
- **GUI test coverage: annotate/style + dialog/misc commands, batch 4 (#816)** — `AnnotateAndDialogCommandTests.cs` executes the real `ReactiveCommand` behind ten toolbar/menu entries and asserts the real effect, closing a false-coverage gap where these were previously proven only by calling the underlying viewmodel method directly or by a `NotBeNull` wiring check (the same pattern that hid #815's bug): `AddHighlightAnnotationFromSelectionCommand` and `AddStickyNoteAnnotationCommand` now assert a real annotation lands on the saved page's `/Annots`; `SetTypewriterColorCommand` asserts the active box's `Style.Color` changes; `VerifySignaturesCommand` asserts the verification summary is actually surfaced; `SecurityCommand`, `ShowPreferencesCommand`, and `AboutCommand` assert the real dialog/window opens; `ShowDocumentationCommand` and `ShowShortcutsCommand` assert the real open/show path fires without launching a real external app or driving headless overlay internals; `GoToPageCommand` asserts `CurrentPageIndex` actually moves.

## [3.4.0] - 2026-07-27
- **Interactive GUI-command coverage for redaction and search (#816, batch 3)** — `RedactionAndSearchCommandTests` executes the real ReactiveCommands behind the redaction buttons and search bar (`ApplyAllRedactionsCommand`, `ApplyRedactionCommand`, `ClearAllRedactionsCommand`, `RemovePendingRedactionCommand`, `FindCommand`, `FindNextCommand`, `FindPreviousCommand`, `JumpToSearchMatchCommand`, `CloseSearchCommand`) and asserts their real effects — closing a gap where redaction removal had only been proven through the scripting path / "doc still open", never by executing the actual Apply command.

### Added
- **PDF/UA-1 and PDF/A conformance checker** (#772) — a new `Excise.Core.Validation` namespace adds a *checker* (not an emitter): `PdfUaValidator.Validate(document)` reports a bounded, honestly-scoped subset of PDF/UA-1 (ISO 14289-1) rules that are decidable from what excise already parses — document is tagged (`/MarkInfo /Marked`), has a `/StructTreeRoot`, declares `/Lang`, has a title (Info `/Title` or XMP `dc:title`) with `/ViewerPreferences /DisplayDocTitle true`, custom structure types are role-mapped (`/RoleMap`) to standard ones, figures carry `/Alt` or `/ActualText`, heading levels don't skip, tables use `TR`/`TH`/`TD`, lists use `L`/`LI`/`LBody`, and real page text is either inside the structure tree or marked `/Artifact`.
- **Type-over (typewriter) styling UI** (#781) — the type-over engine already supported font size, colour, and text alignment (`PdfTypewriterTextStyle`/`PdfTypewriterTextOperation.WithStyle`), but nothing in the GUI called it, so every box was Helvetica 12pt black left-aligned.
- **PDF 2.0 page-level and document-level structural features: parse, model, round-trip** (#331) — page transitions (`/Trans`, all twelve ISO 32000-2:2020 §12.4.4 styles: Split/Blinds/Box/Wipe/Dissolve/Glitter/R ("Replace")/Fly/Push/Cover/Uncover/Fade, plus duration/dimension/motion/ direction/fly-scale/fly-rectangle) via `PdfPage.Transition`; page display duration (`/Dur`) via `PdfPage.Duration`; embedded page thumbnails (`/Thumb`) via `PdfPage.ThumbnailStream` (parsed and preserved, deliberately not decoded/rendered — a thumbnail strip should fall back to the renderer when null); and document/page actions (`/OpenAction` — both the modern action- dictionary form and the legacy bare-destination-array form —, `/AA` on both document and page, and the `/Names/JavaScript` name tree) via the new `PdfAction` model (`PdfDocument.OpenAction`/`.AdditionalActions`/ `.DocumentJavaScriptActions`, `PdfPage.AdditionalActions`).
- **Accessibility MCID→letter bridge: screen readers read tagged elements' real body text (#776).** Follow-up to the tagged-PDF structure layer (#631, PR #775).
- **OCG-aware text extraction now resolves OCMD membership and visibility expressions** (#336) — the per-letter hidden-layer flag (`Letter.IsInHiddenOptionalContent`) previously identified only content inside a directly-referenced Optional Content Group named in the catalog `/OCProperties /D /OFF` array, matched by name.
- **ICCBased-CMYK (N=4) overprint participation** (#803, follow-up to #634) — a fill or stroke whose colour space is an ICCBased space with four components now takes part in overprint simulation, treated as DeviceCMYK under the same nonzero-overprint-mode (`/OPM 1`) gating: a component that is exactly zero leaves that colorant of the backdrop unchanged instead of knocking it out.
- **App-wide in-session undo/redo** (#782) — a single edit-history stack (command pattern with per-operation inverse closures, plus a collection snapshot for type-over edits) now covers the reversible, pre-flatten editing state: type-over create/edit/move/delete, annotation authoring (highlight and sticky-note add), and page reorder/rotate/delete.
- **Audit flag for symbolic (3,0) glyphs no extractor recovers** (#796) — a simple symbolic TrueType with a Microsoft-Symbol `(3,0)` cmap AND an `/Encoding` renders meaningful text through its `(3,0)` glyphs, but every extractor (excise, mutool, poppler — established by #794/#795) honours `/Encoding` and extracts the WinAnsi interpretation instead.
- **Overprint simulation for Separation and DeviceN colours (#634).** Overprint (ISO 32000-1 §8.6.7) previously engaged only for DeviceCMYK fills/strokes; it now also engages for Separation and DeviceN colours whose tint transform resolves to a DeviceCMYK alternate.

### Performance
- **Renderer image decode / colour-conversion allocation cuts** (#599) — the image decode path no longer stages a large transient managed pixel buffer per image.
- **Renderer glyph-outline caching on the text hot path** (#598) — glyph outlines are now tessellated once per (typeface, size, glyph) and reused for the rest of the page instead of re-decoding the same outline on every draw.
- **Text-extraction hot path: fewer allocations, less CPU** (#600) — the `TextExtractor` content-stream parse now caches all per-font derived state (ToUnicode map, `/Differences`, the Identity / Mac-glyph-order / embedded-CID / symbol-cmap decode tables, and the CID/CMap/`/W` width geometry) keyed by the resolved font dictionary, instead of re-parsing those streams on every `Tf` operator — the dominant repeated cost, since every text block re-issues `Tf`.

### Fixed
- **Flaky redaction test: `FullwidthFormsRedactionTests` collided with the random `/ID`** (#771, #800) — the fullwidth-forms redaction test intermittently failed on macOS and Windows CI (same commit could pass on Linux and fail on macOS — definitionally non-deterministic, and it was a false red, never a real leak).

### Changed
- **Linux coverage gate restored to green** (R6 CI health) — `Excise.Core` line coverage had drifted to 92.39% on `develop`, below the 93% ratchet, reddening every merge.
- **`Excise.Cli.Tests` wall-clock cut ~93% (72s → 5s measured, `-c Release` only) by removing an accidental full rebuild** (#731).

## [3.3.1] - 2026-07-26
- **GUI interaction latency: per-page search-highlight index (#601).** Page navigation recomputed the current page's search highlights with a linear `O(total matches)` scan over every match on *every* page flip, so the cost grew with document size (a dense search on a large book — thousands of matches — made each page change scan all of them).

### Fixed
- **Symbolic TrueType with a (3,0) symbol cmap: text extraction mis-decode** (#791) — a simple (non-Type0) symbolic TrueType font that carries a Microsoft-Symbol `(3,0)` cmap subtable and ships no `/ToUnicode` addresses glyphs through an F000-based Private Use offset.

### Investigated (no code change)
- **Symbolic TrueType with a (3,0) symbol cmap AND `/Encoding` present** (#794) — the sibling case of #791 was measured and found **not to reproduce** as a excise-specific mis-decode, so no extraction/precedence change was made.

## [3.3.0] - 2026-07-26

### Added
- **Type-over tool: GUI-save independent-oracle test coverage** (#780) — closed a no-self-oracle gap in type-over save verification.
- **Type-over tool: move/resize-handle and wrap-parity test coverage** (#780) — closed the two coverage gaps left after the type-over workflow work.

### Fixed
- **Typewriter (type-over) workflow: pending edits can no longer be lost or flattened unseen** (#780) — pending type-over edits used to persist silently after leaving typewriter mode and bake into the PDF on the next save with no signal, off-page edits were never shown yet still flattened, and a plain click placed nothing.
- **RTL redaction: numbers inside right-to-left lines no longer evade removal** (#632) — a number embedded in an Arabic/Hebrew line (an ID, date, or phone number) kept its surrounding words in visual order, so a phrase-spanning-a-number search matched nothing and `RedactText` silently removed nothing while reporting success.

### Security
- **RTL redaction in Type0/Identity-H (CID) Arabic/Hebrew fonts verified with independent oracles** (#632) — the redaction-critical case where the content stream carries the word only as 2-byte CIDs (glyph indices), so the Unicode string never appears in the file and a saved-bytes search — even UTF-16BE — is structurally blind to it.
- **Type0/CID horizontal advance now scales `Tc`/`Tw` by `Th`** (#734) — for Type0 fonts the character/word-spacing contributions were applied outside the horizontal-scaling factor (`Th`), drifting extracted glyph positions on text that combines Type0 fonts with non-default horizontal scaling and non-zero spacing (ISO 32000-1 §9.4.4).
- **Renderer: horizontal Type0/CID glyph advance now applies `Tc`/`Tw`** (#734) — `SkiaRenderer.RenderCidBytes` advanced the horizontal text matrix by summed `/W` glyph widths only, never adding character spacing (`Tc`) or word spacing (`Tw`, single-byte code 32 only per §9.3.3), unlike the simple-font path and the #515 vertical Type0 path, which both already applied them.
- **Redaction on a multi-run line no longer shifts the kept text** (#758) — when `GlyphRemover` removed a text-showing operator from a multi-run `BT` block, it dropped that operator's pen advance, so kept runs after the redaction on the same line shifted left.
- **Deterministic real-number formatting in the PDF writer** (#762) — every real-number emit site (content-stream operands in `ContentStreamWriter`, object serialization in `PdfObjectWriter`, `ContentOperator.ToString`) now formats through a shared `PdfNumberFormatter`: invariant culture, at most six decimal places, trailing zeros trimmed, never exponent notation.
- **CID glyph-selection matrix: deterministic handling of missing maps** (#515, final slice) — the renderer's CID→GID resolution for Type0 fonts now handles every cell of the matrix the way the reference renderers do, each behavior verified empirically against poppler/Ghostscript (and mutool where it has CMap resources) rather than assumed: instead of falling through to identity — which indexed the CFF's unrelated glyph order with the CID and **drew an arbitrary wrong glyph**.

### Added
- **Right-to-left text selection in the viewer** (#373) — selecting a line that contains Arabic/Hebrew now copies the text in logical reading order (the way it is read) rather than the visual order it is painted in, reusing the same bidi ordering the extractor already applies (#632) instead of a second bidi pass.
- **Visible signature appearance** (#623, last remaining bullet) — a signed signature field whose widget `/Rect` has non-zero area now gets a baked `/AP /N` appearance stream (`SignatureAppearanceAuthoring` in `Excise.Core`, mirroring the annotation-authoring baked-appearance pattern from #626): a bordered box with "Digitally signed by {name}", the signing date, and any `/Reason`/`/Location` the caller supplied.
- **Tagged-PDF structure accessibility layer** (#631) — the Avalonia viewer's automation peer tree now exposes the tagged-PDF structure tree to screen readers: the page's accessible text is ordered by the structure tree when a tagged document supplies orderable `/ActualText` (falling back to geometric reading order otherwise), structure elements are surfaced as role peers (headings H1–H6, lists and list items, tables/rows/cells; figures continue to come through as `/Alt` image peers), and `H` / `Shift+H` navigate to the next/previous heading across page boundaries.
- **Remaining #626 annotation subtypes: markup, shapes, stamps, edit/reply** (#626) — `PdfAnnotationAuthoring` now covers the rest of ISO 32000-2 §12.5.6's programmatic authoring surface, each with a baked, self-contained `/AP /N` appearance stream so third-party viewers render the same pixels (excise cannot be its own oracle for this — see below): `AddSquigglyAnnotation` (§12.5.6.10) mirror `AddHighlightAnnotation`'s single-quad shape but bake a stroked line/zig-zag appearance, since (unlike Highlight) most viewers do not synthesize one for these subtypes.
- **FDF annotation import/export round-trip** (#626) — `Excise.Core.Forms.FdfSerializer` reads and writes the PDF-syntax `/FDF` annotation interchange format (the counterpart to XFDF), so annotations round-trip with tools that prefer FDF.
- **DeviceCMYK overprint rendering** (#634) — the renderer now honours `/OP`, `/op`, and `/OPM` overprint state for DeviceCMYK fills and strokes (ISO 32000-1 §8.6.7): with overprint on, a zero colorant no longer knocks out the underlying separation.
- **Screen readers announce tagged-PDF `/ActualText`** (#631) — replacement text (`/ActualText`, ISO 32000-2 §14.9.4) is exposed to assistive technology through the viewer's automation tree, so hyphenation rejoins, ligature/symbol substitutions, and pages where glyph extraction fails are read correctly.
- **XFDF annotation import/export round-trip** (#626, final headline slice) — `Excise.Core.Forms.XfdfSerializer` speaks Adobe XFDF 3.0, the interchange dialect behind Acrobat/Foxit "Export comments as data file" review workflows.
- **Apply self-signed PDF signatures — PKCS#7/CMS detached** (#623, first slice) — `SignatureApplicationService` signs a document with a self-signed or locally-held certificate: `/Sig` dictionary (`/Filter /Adobe.PPKLite`, `/SubFilter /adbe.pkcs7.detached`), correct two-pass `/ByteRange` (a fixed-capacity zero-filled `/Contents` hex hole plus a fixed-width ByteRange placeholder patched in place after serialization, so no byte offset shifts), and a BouncyCastle detached CMS SignedData backfilled into the hole.
- **Ink (freehand) annotation authoring** (#626) — `AddInkAnnotation` writes ISO 32000-2 §12.5.6.13 ink annotations from one or more polylines: `/InkList` (one inner array of x/y pairs per stroke), `/Rect` (the bounding box of every point, padded by half the pen width), stroke color (`/C`) and pen width (`/BS`) — plus a baked, self-contained `/AP /N` appearance stream that strokes each polyline with round caps and joins, so the drawing renders identically in excise, Acrobat, mutool, and pdftocairo (verified by independent-renderer differential tests).
- **FreeText annotation authoring** (#626) — `AddFreeTextAnnotation` writes ISO 32000-2 §12.5.6.6 text-box annotations: `/Contents`, a `/DA` default appearance string (color + base-14 Helvetica + size), `/Q` quadding (left/center/right via the new `PdfFreeTextQuadding` enum), optional border (`/BS`) and background fill (`/C`) — plus a baked, self-contained `/AP /N` appearance stream that draws the text (word-wrapped with real Helvetica advance widths, quadding-aware) so the box renders identically in excise, Acrobat, mutool, and pdftocairo (verified by independent-renderer differential tests).
- **Signature trust-chain validation and consolidated result states** (#466) — signature verification now evaluates the signer certificate chain (OS trust store by default; an explicit trust-anchor policy is injectable) in addition to the existing ByteRange/CMS checks, and reports a consolidated state: valid+trusted, valid-but-untrusted, invalid (modified after signing / broken signature), or indeterminate (could not verify).
- **Square and Circle annotation authoring** (#626, first slice of #271) — `AddSquareAnnotation` / `AddCircleAnnotation` write ISO 32000-2 §12.5.6.8 shape annotations with border color (`/C`), optional interior fill (`/IC`), border width (`/BS`), and — unlike the earlier sticky-note/highlight authoring — a baked normal appearance stream (`/AP /N`), so the authored shape renders identically in excise, Acrobat, mutool, and pdftocairo (verified by independent-renderer differential tests).
- **Complete predefined CJK CMap coverage — the full PDF 32000 Table 118 set** (#515) — 50 more registered encoding CMaps ship embedded (Adobe cmap-resources, BSD-3), covering every predefined name a conforming reader must support: the legacy national encodings (GBK-EUC, GB-EUC, GBpc-EUC, GBK2K, Big5 `B5pc`/`ETen`/`ETenms`/`HKscs`, CNS-EUC, EUC-JP, the RKSJ Shift-JIS family, ISO-2022 `H`/`V`, KSC-EUC, KSCms-UHC, KSCpc-EUC) and the PDF 1.5+ `Uni*-UTF16` encodings including Adobe-KR's `UniAKR-UTF16-H` (ISO 32000-2).
- **Type 3 d0/d1 glyph metrics and d1 bounding-box clipping** (#514) — the renderer now honors the metrics a Type 3 CharProc declares: when the font has no `/Widths` entry covering a code, the advance falls back to the `wx` operand of the glyph's leading `d0`/`d1` operator (`/Widths` still overrides an inconsistent `wx`, per §9.6.5); glyph-space advances map through `/FontMatrix` as a displacement vector, so rotated matrices no longer drift glyphs apart by a bogus 1/1000 scale; and the glyph bounding box declared by `d1` clips the glyph description (an all-zero box declares no bounds).
- **Vertical writing mode for Type0/CID fonts** (#515) — the `/W2` and `/DW2` vertical metric tables (PDF §9.7.4.3) are now parsed and honored across the extractor, the redaction content-stream parser, and the renderer.

### Fixed
- **Word spacing (`Tw`) no longer fires on 2-byte character code `<0020>`** in CID fonts — per §9.3.3 it applies only to the single-byte code 32 (e.g. 90ms-RKSJ's 1-byte space still gets it).
- **CID width tables are parsed by one shared, hardened parser** (`CidFontWidths`) instead of three divergent `/W` walks: indirect references are resolved at every level, junk tokens are skipped, reversed ranges are dropped, and hostile ranges like `[0 999999999 500]` are clamped to the valid CID space instead of allocating billions of entries.

## [3.2.1] - 2026-07-24

A redaction-trust release. Search and redaction now match a word regardless of
how the PDF happens to store it — several of these were **silent redaction
failures** (the tool reported success and left the word in the file). CJK text
now extracts *and* renders, and page content is exposed to screen readers.

### Fixed — silent redaction failures (search/redaction now matches however text is stored)
- **Right-to-left text is matched in logical order** (#632) — Arabic/Hebrew stored in visual order (the common single-`Tj` encoding) extracted reversed, so `RedactText` matched 0 and reported success.
- **Arabic presentation forms fold to base letters for matching** (#632) — a base-letter search now matches text stored as shaped forms / lam-alef ligatures (U+FB50–FDFF, U+FE70–FEFF).
- **Latin ligatures fold for matching** (#722) — a search for "office"/"final" now matches text stored with `ﬃ`/`ﬁ` (U+FB00–FB06).
- **Canonical (NFC) accents match** (#724) — precomposed "café" and decomposed `cafe`+U+0301 now match.
- **Arabic harakat / Hebrew niqqud are matched insensitively** (#725) — a bare-letter needle finds vocalized/pointed text.
- **Invisible separators no longer break matching** (#726) — soft hyphen (U+00AD), zero-width characters, and non-breaking spaces.
- **Fullwidth ↔ halfwidth forms match** (#727) — a keyboard "ABC"/"123" finds `ＡＢＣ`/`１２３` and halfwidth katakana.

### Added
- **Text extraction for Type0 CJK / CID fonts** (#715, #515) — `/ToUnicode` `/Identity-H|V` (name form), non-embedded Identity-H CID fonts via the standard Macintosh glyph order (#532), and embedded fonts via reverse-cmap GID→Unicode now decode correctly instead of garbling (which previously made `RedactText` silently fail on CJK).
- **Registered (predefined) CJK CMap support in text extraction and redaction** (#515 slice 2; the CJK half of #715) — a Type0 font whose `/Encoding` is a registered CMap NAME (`/UniGB-UCS2-H`, `/UniCNS-UCS2-H`, `/UniJIS-UCS2-H`, `/UniKS-UCS2-H`, `/90ms-RKSJ-H`, and their vertical `-V` variants) now decodes code→CID through the actual Adobe CMap data, and — when there is no embedded `/ToUnicode` — CID→Unicode through the `Adobe-<Ordering>-UCS2` CMap selected from the descendant's `/CIDSystemInfo` (PDF §9.10.2 method (b)).

### Fixed
- **Renderer now selects glyphs through registered CMap names** (#515 renderer slice) — `SkiaRenderer`'s Type0 path only honored an embedded `/Encoding` CMap *stream*; a registered CMap NAME fell through to identity decoding, so 2-byte character codes were misread as CIDs and CJK pages rendered as .notdef tofu even though extraction (above) read them fine.
- **Content-stream parser no longer mangles multi-byte text operands** (#515) — `ContentStreamParser` round-tripped `Tj`/`TJ` string operands through `PdfString.Value`'s document-string decode heuristics and Latin-1, clamping any byte the heuristic mapped above U+00FF to `?`.
- **Text no longer renders heavier than reference renderers** (#710, root cause of #584) — fill-mode text was rasterized through `SKCanvas.DrawText`, whose glyph masks come from the platform scaler (CoreText on macOS, hinted FreeType on Linux, DirectWrite on Windows); on macOS that added ~+0.45px of width to every stem at body-text sizes (~17% more ink on an identical embedded Type 1C outline vs mutool/pdftocairo).
- **Raw Type 1 (`/FontFile`) text keeps the platform glyph-mask fill** (#710 regression fix) — the outline-path fill above made embedded raw Type 1 faces render *worse* against the references (highlights.pdf p5, URW Nimbus Roman: DifferingPixelFraction vs mutool 0.00067 → 0.0152), because the platform's raw-Type1 raster path never applied the CFF stem darkening the outline fill was fixing — DrawText already matched mutool almost exactly there.
- **Type 3 uncolored-glyph (d1) colour semantics** (#514) — a d1 CharProc's own colour operators are now ignored so the glyph paints in the text object's fill colour, per ISO 32000-1 §9.6.5.

### Added
- **PDF page text is exposed to the platform accessibility tree** (#631, first slice) — a screen reader entering the viewer now reaches the current page's text in reading order (via a `PdfViewerAutomationPeer`), updating on page navigation and content changes.
- **Direct per-font-class rendering test matrix** (#512) — embedded TrueType/CFF/OpenType, base-14, encoding, render-mode, and Type0/CID paths are now covered by focused render tests independent of complex corpus PDFs.

## [3.2.0] - 2026-07-22

A stabilization release: viewport continuity is preserved across zoom and
view-mode changes, the Native AOT release lane publishes with zero warnings,
and saved editing output (typewriter, forms) is now verified against
independent reference renderers.

### Fixed
- **Continuous viewport anchoring across zoom** (#700) — zooming in the continuous viewer now keeps the page under the viewport centre in place and the page-number label live, instead of jumping to the top and freezing the label.
- **View-mode switch visual continuity** (#693) — switching between single-page and continuous modes carries the reading position over and uses a unified display scale, so text no longer jumps or changes size across the switch.
- **Duplicate class-handler registration** (#700) — viewer input class handlers were registered in the instance constructor, so every constructed viewer added another static handler (N-plicated event dispatch, a source of UI-test flakiness).
- **Packaged/AOT startup crash** (#593) — `FluentAvaloniaTheme`'s compiled-XAML constructor hard-references the DataGrid themes at startup; an over-eager assembly trim broke app launch in the published bundle.

### Changed
- **Native AOT publishes with zero warnings** (#593) — `ReactiveUI.Avalonia` was dropped (the main-thread scheduler is vendored as `RxSchedulers.MainThreadScheduler`; `RxApp` is gone in ReactiveUI 23), and third-party AOT/IL warning roll-ups were eliminated at the source.
- **AOT support matrix documented** (#595) — `osx-arm64` is shipped and validated by the AOT CI lane and `run-aot-smoke.sh`; `win-x64`, `linux-x64`, and `osx-x64` are explicitly deferred with probe issues.

### Added
- **Editing-output fidelity gates** (#610, #611) — typewriter and interactive form saves are now verified against mutool/Ghostscript reference renders, so regressions in what excise writes are caught by independent tools.

### CI / Infrastructure
- **Windows veraPDF install fixed** (#666) — it had never actually run (a pwsh-wrapped bash string expanded `$(find …)` as its own subexpression behind `continue-on-error`); veraPDF setup failures are now explicit on all three OS jobs, and veraPDF prints its version on Windows for the first time.
- **Branch model** — `develop` is the default branch where work lands; `main` is a stable release pointer that only ever advances to release tags.
- Removed the never-configured Gemini workflows that reported red on every PR (#699).

## [3.1.0] - 2026-07-20

A display-correctness release: text is now crisp on HiDPI displays and at any
zoom, and two live-reproduced selection/zoom display bugs are root-caused and
fixed with pixel-level regression batteries.

### Fixed
- **Crisp text on HiDPI and when zoomed** (#682, #683) — both the continuous and single-page viewers render at device-pixel resolution (zoom × device-pixel-ratio), re-rendering as you zoom instead of upscaling a 96-DPI raster.
- **Selection highlights no longer drift left** (#693) — overlay canvases are pinned to the page image's origin; at narrow zooms the highlight previously landed up to ~400 dips left of the selected text.
- **Fit-after-selection display corruption** (#697) — pressing Fit in select-text mode at HiDPI showed ~2× oversized text over blank space with seemingly orphaned highlights.
- **Quick-win batch** (#675, #674, #668, #665) — in-page links no longer dispatch double click events; a flaky AES round-trip test stabilized; skip-budget comment hygiene.

### Added
- **Thumbnail viewport window** (#687–#690) — thumbnails are evicted, prefetched, and pre-warmed around the visible window with a disk-cache trim, keeping the sidebar responsive on large documents.
- **Page-assembly permission enforcement on CLI merge/split** (#677) — `/P` bit 11 now gates `excise merge`/`excise split` (`DocumentAction.AssembleDocument`).
- **Native AOT release lane for Excise.App** (#590), validated on osx-arm64.
- Deterministic SVG→raster icon generation script (#679).

### Tests / infrastructure
- Mode-switch display invariants across modes and device pixel ratios, pixel-level displayed-text verification for mode buttons, and a fit-after-selection live-repro battery (red-checked at dpr 2).
- Project-authored test-data drift gate and the skip-budget self-test wired into tier t0 (#678).
- `EXCISE_TRACE_VIEWER=1` viewer-state probes (ViewMode/render plans/overlay origins) used for the live #697 diagnosis.

## [3.0.0] - 2026-07-18

**The project is renamed from `pdfe` to `Excise`.** Same engine, same
philosophy (a tool must not be its own oracle for the property it exists to
guarantee) — new name, chosen to say what the product does: content isn't
covered, it's *excised*, and the removal is proven against independent tools.

This is the first tagged release since 2.28.0, so it also carries the changes
documented in the (never-tagged) **[2.29.0]** (test-integrity gates) and
**[2.30.0]** (the AES-256/AES-128 encryption epic, #624/#639–#644, and the
Make Searchable GUI) sections below.

### Changed — BREAKING (why this is a major version)
- **CLI command `pdfe` → `excise`.** `excise redact in.pdf out.pdf "secret"`, `excise info`, `excise render`, … — same commands, same flags.
- **Library namespaces / assemblies / NuGet ids `Pdfe.*` → `Excise.*`**: `Excise.Core`, `Excise.Rendering`, `Excise.Avalonia`, `Excise.Ocr`, `Excise.Cli`; the desktop app is `Excise.App`.
- **App identity**: window title, macOS bundle (`cl.skpt.excise`), and Linux desktop id updated to Excise.
- **Repository** renamed `github.com/marctjones/pdfe` → `.../excise` (old URLs redirect).

### Added
- **New document-first app icon.** A PDF page with a cleanly *excised* line — a see-through slot where text was, not a black bar hiding it — expressing the product in one mark.
- **First-class in-page links in continuous view** (#667) — click-to-follow and hover affordance for internal/GoTo and URI link annotations while scrolling.

### Notes
- No engine behavior changed in the rename; the redaction, encryption, and rendering pipelines are byte-for-byte the 2.30.0 code under new names.
- The two blocking roadmap tracks — **Redaction Trust** and **Document Security** — are complete and closed.

## [2.30.0] - 2026-07-17

The encryption release: excise now WRITES password-protected PDFs — the full
#624 epic (#639–#644) landed and closed in one pass, every piece verified
against independent readers (qpdf, mutool, Ghostscript, pdftoppm), never
against excise itself. Alongside it: a sweep of redaction-trust extraction
fixes that emptied the #651 adversarial-corpus allowlist, the "Make
Searchable" OCR feature reached the GUI, and a rendering positioning bug
that had been misattributed to font scaling for months was root-caused and
fixed.

Note: the `[2.29.0]` section below was documented on 2026-07-13 but never
tagged — v2.30.0 is the first tagged release containing those changes too.

### Security
- **Empty owner password no longer grants passwordless full authority** (found and fixed pre-release, during #644 verification — no released build was ever affected).

### Added
- **Encryption writer: AES-256 (V5 R6, PDF 2.0 native) and AES-128 (V4 R4, CFM=AESV2)** (#639, #640, part of the #624 encryption epic).
- **Password management: Document > Security dialog and `excise encrypt` / `excise decrypt`** (#641).
- **Multi-reader encryption interop gate** (#644). 37 assertions covering both algorithms × four independent tools (mutool, qpdf, Ghostscript, and a new pdftoppm oracle) × correct/owner/wrong/absent password, plus semantic `/P` verification via qpdf and an anti-vacuity guard (`EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS=1` makes an all-tools-missing run a hard failure).
- **Make Searchable in the GUI** (#658, completing #627).
- **Encryption is preserved across redact/edit/save round-trips** (#643, part of the #624 encryption epic).
- **Document permissions (`/P`) are surfaced and enforced** (#642, part of the #624 encryption epic).

### Fixed
- **Redaction-trust extraction sweep — the #651 adversarial-corpus allowlist is now empty.** The #648 gate's original finding (11 pdf.js fixtures where excise catastrophically under-extracted vs.
- **Embedded-CFF text ignored character/word spacing when drawing** (#652).
- **Trust PDF `/Widths` over embedded-font `hmtx` for inter-glyph advance** (#584) and **ShadingType 5/7 wired into pattern-fill dispatch** (#633).
- **PDF string-literal line-continuation escape handled** (#637).
- **Continuous-view cache is byte-budgeted** (#615): the page cache is bounded by memory (200 MB) instead of a flat page count, so mixed-size documents can't blow past intended memory use.
- **Four flaky/incorrect UI tests root-caused** (#653): a view-mode default mismatch, not layout timing — and the investigation found link click/hover has no continuous-mode implementation at all (filed #667).
- Test-infrastructure hardening: skip-budget gates extended to every test project with real CI-log-verified allowlists (#655, #663, #664, #654); corpus-resilience and adversarial-extraction gates (#648) plus the corpus-wide extraction-parity floor gate (#645); test tiers T0–T3 with one entry point (#646); per-OS CI jobs (#647); Excise.Core coverage gate restored to 93% (#603); font-parser fuzzing closed a real hang and crash (#648).

### Changed
- **`--allow-decrypt` flipped meaning** with #643 (see Added): #638 had made "saving an encrypted document decrypts it" loud and fail-closed because excise could not write encryption; now that it can, preservation is the default and `--allow-decrypt` is the explicit plaintext opt-out.
- Printing removed from the roadmap as an intentional decision (#621, #622).

## [2.29.0] - 2026-07-13

User-facing: continuous scroll is the default again, and "go to page N" now
actually goes there. Under the hood: the test suite can no longer lose coverage
silently, and a performance change can no longer quietly rewrite what a
correctness test considers correct.

### Fixed
- **Continuous mode swallowed programmatic navigation.** "Go to page N" — an outline click, the page-number box, a jump to a search hit — could be silently discarded and land the user on page 1.

### Changed
- **Continuous scroll is the default view mode again**, now that the navigation race above is fixed.

### Test integrity (#617, #618, #619, #620)
- **Coverage can no longer vanish silently.** `scripts/check-skip-budget.sh` fails the build when the set of skipped tests changes in either direction.
- **A perf change can no longer rewrite a correctness assertion quietly.** `scripts/check-gate-asymmetry.sh` (in CI) fails a change that touches a performance-sensitive path *and* rewrites a test's expected values, unless the commit says so explicitly.
- **The 144-page display sweep no longer fails on machine load.** It owns its deadline and reports what actually happened; `scripts/run-gui-display-sweep.sh` shards it (one shard of four: 1m24s, vs 5–20min).
- **Geometry tests state invariants, not pinned numbers**, so they survive a legal optimization and still fail an illegal one.
- **CLAUDE.md corrected**: it was pointing contributors at a redaction directory that does not exist, listing closed issues as current, and — worst — prescribing a redaction test assertion that is **blind** to three of the leaks fixed in 2.28.0.

## [2.28.0] - 2026-07-13

**Security release. Two redaction leaks are fixed. Upgrading is recommended for
anyone using excise to redact sensitive documents.**

Both fixed leaks share one root cause: redaction was verified by asking excise's
own text extractor whether excise had removed the text. That extractor reads the
content stream and nothing else, so text surviving in any other carrier was
reported as a clean redaction — by a fully green test suite.

### Security

- **Fixed: redacted text survived in the structure tree of tagged PDFs (#636).** `/ActualText` and `/Alt` restate the text of a marked-content span.
- **Fixed: redacted text survived in document-level carriers (#608).** The XMP `/Metadata` packet, outline (bookmark) titles, and annotation `/Contents` were never scrubbed — only `/Info` was, and only in the GUI.
- **Verified (was only asserted in a comment): a full save garbage-collects the previous revision**, so an incremental-update PDF cannot retain the un-redacted page.
- **New: redaction is now verified by tools that are not excise** (#606, #607, #609) — independent extraction (mutool), independent rendering (Ghostscript) as a before/after ink differential, and the full corpus.

### Known security limitations (unchanged from 2.27.1 — not introduced here)

- **Redaction is silently incomplete where text extraction is blind (#637).** Where excise cannot read text, it cannot redact it, and it reports success anyway.
- **Redacting an encrypted PDF returns an unencrypted copy (#638).** The writer cannot emit `/Encrypt`.
- **`/P` permissions are parsed but never enforced (#642).**

### Added
- Continuous scroll can now be enabled from View > Continuous Scroll and the choice is remembered across sessions (`ContinuousScrollEnabled`).
- `PdfDocumentSanitizer.ScrubTerms` (public API, additive) — removes redacted terms from `/Info`, XMP `/Metadata`, outline titles, and annotation `/Contents`.

### Deferred to 2.29.0
- **Continuous scroll as the default view mode.** Enabling it by default surfaced a pre-existing navigation race in the viewer: a programmatic "go to page N" (outline click, page-number box, search hit) issued before layout settles is swallowed by the scroll→page sync and silently lands on page 1.

### Changed
- Continuous-scroll page rendering now coalesces render passes and de-duplicates in-flight tile requests, so fast scrolling through large documents no longer queues and cancels a render for every intermediate scroll position.

### Fixed
- View and Tools menu checkmarks (Show Outline, Show Thumbnails, Show Clipboard History, Continuous Scroll, Reveal Hidden Text, Reveal Rasterized Hidden Text) stayed permanently checked and did nothing when clicked.
- Leaving an editing mode (redaction, text selection, form authoring, typewriter) now restores the saved continuous-scroll preference.
- Suppressed the tooltip on the status-bar page arrows, whose popup made the small footer targets hard to click while the status bar was re-measuring.

## [2.27.1] - 2026-07-08

macOS bundle identity correction release. No intended public API break.

### Changed
- Changed the macOS app bundle identifier from `com.marcjones.excise` to `cl.skpt.excise` so LaunchServices, Finder/Open With, and packaged GUI smoke target the skpt-owned excise app identity.
- Updated packaged GUI smoke shutdown to address the new bundle identifier.

### Tests
- Release smoke passed for `2.27.1` with the quick, package, and packaged-GUI gates: `logs/release-smoke_20260708_021515`.

## [2.27.0] - 2026-07-08

GUI search responsiveness and release-gate hardening release. No intended
public API break.

### Changed
- **Search and indexing hot paths.** Reused the page letter cache for page text and word extraction, made document text-index builds single-flight, skipped annotation search work on pages without annotations, and removed per-match word list allocations from search result bounds calculation.
- **Background indexing responsiveness.** Delayed search-index startup after document open and page mutations so first-page interaction stays responsive, while keeping the index available for fast repeated searches.
- **Search result publication.** Batched search-match publication to the UI, deferred first-match navigation behind the result update, and recorded worker, UI queue, UI publish, and total search timings for hotspot reports.
- **Status-message accuracy.** Cleared `Opening PDF…` once the document is usable and hardened search cancellation/close paths so stale `Searching…` status and inline progress text do not remain visible.

### Added
- **Status-message regression audit.** Added UI tests that verify document-open and cleared-search status transitions remain accurate.
- **Icon resource regression audit.** Added a main-shell `PathIcon` `StaticResource` sweep so toolbar and menu icon references fail tests if an icon resource is missing.
- **Search subphase hotspot reporting.** Added `gui.search.worker`, `gui.search.ui-queue`, `gui.search.ui-publish`, and `gui.search.total` to the GUI workflow performance reports.

### Tests
- Stabilized headless GUI fixture checks and encrypted redaction fixture skips on machines where optional encrypted fixtures are unavailable.
- Aligned the core coverage gate with the current baseline so CI fails on real regressions instead of stale thresholds.

## [2.26.0] - 2026-07-07

Native AOT and GUI hot-path responsiveness release. Additive public API change
in `Excise.Avalonia`; no intended breaking change.

### Added
- **Native AOT release lane (#590-#595).** Added `scripts/run-aot-smoke.sh` and wired `scripts/release-smoke.sh --only=aot` so the GUI AOT build can be published, packaged, warning-audited, and optionally exercised with packaged GUI smoke evidence.
- **GUI hotspot regression reporting (#596, #601).** Added structured GUI workflow hotspot reports for document open, continuous scroll, page jumps, search, annotation, forms, redaction, save, and close workflows.
- **Full GUI responsiveness coverage (#601).** Added end-to-end responsiveness tests and catalog coverage for the long-document and broad workflow phases that should stay below human-visible interaction budgets.

### Changed
- **Viewer-owned display rendering (#601).** Shifted display rendering ownership into the viewer, cached rendered pages as bitmaps, and exposed the additive `PdfViewerControl.RenderVersion` API so hosts can explicitly invalidate viewer caches after visual document changes.
- **Continuous-view hot path (#601).** Cached continuous page layout positions and optimized visible-page lookup for long-document scrolling.

### Tests
- Regenerated the `Excise.Avalonia` public API approval baseline for the intentional `RenderVersion` addition.
- Redaction gates remain required for this release line: `dotnet test ...

## [2.25.0] - 2026-07-04

Benchmarking and renderer-performance release. No intended public API break.

### Added
- **Benchmark suite (#344, #357).** Added `Excise.RenderTools benchmark-suite` and wired `scripts/run-benchmarks.sh` so one command emits `benchmark-report.json`, `benchmark-pages.csv`, and `benchmark-report.md` covering excise parse/text/render speed, external-reference fidelity, RMSE/SSIM metrics, tool availability, and subprocess-only license isolation.
- **Benchmark regression gate (#344, #357).** Added a release-smoke benchmark gate plus a deterministic CI gate that runs the benchmark suite in synthetic no-oracle mode and fails on excise parse/render/redaction regressions.
- **Redaction-completeness signal (#357).** The benchmark report now includes a synthetic glyph-level redaction check so speed reporting does not drift away from excise's security-critical differentiator.

### Changed
- **Benchmark wrapper (#344).** `scripts/run-benchmarks.sh` now runs the benchmark suite by default, keeps `corpus-hotspots` and `gui-display-hotspots`, and exposes `benchmarkdotnet` for the isolated `Excise.Benchmarks` microbenchmark project.
- **RenderTools exit codes (#344).** Utility commands now normalize handler `Environment.ExitCode` the same way the public CLI does, so failed benchmark gates return a non-zero process exit.

### Tests
- `BenchmarkSuiteTests` covers oracle parsing, report generation, license metadata, redaction-completeness reporting, and non-zero regression exits.
- Local reference smoke passed with MuPDF, Poppler, and Ghostscript available: `logs/benchmarks/v2.25-reference-smoke`.

## [2.24.0] - 2026-07-04

UX, icon, and visual-polish audit release. No intended public API break.

### Changed
- **Vector shell icons (#559).** Replaced the main menu, toolbar, and empty state emoji icon affordances with local vector `StreamGeometry` resources so the shell no longer depends on platform emoji fonts for core commands.
- **Toolbar layout (#559).** Reserved the right side of the toolbar for zoom controls and placed the main action strip in a horizontal scroll region.

### Added
- **Screenshot-backed UX/icon audit (#559).** Added `VisualPolishAuditTests` and `scripts/run-ux-icon-audit.sh`, which capture headless screenshots for empty/open, document navigation/page organization, search, redaction, forms, typewriter/annotation, and preferences states and write `ux-icon-audit.json` plus a markdown report.
- **UX release gate (#559).** Added `scripts/release-smoke.sh --quick --only=ux` and release-checklist coverage so design-quality review stays separate from renderer/display parity.

### Tests
- v2.24 UX/icon audit passed: `logs/ux-icon-audit/v2.24-local` (`VisualPolishAuditTests`, screenshots, and manifest).
- Full Debug build passed: `dotnet build excise.sln -c Debug`.

## [2.23.0] - 2026-07-04

Automation API and platform integration release. Additive public API change in
`Excise.Core.Automation`; no intended breaking change.

### Added
- **Stable CLI automation contract (#561).** Added `excise batch` for JSON workflows with structured final reports, optional report files, progress NDJSON on stderr, documented exit codes, relative-path resolution, and password-aware document open without writing passwords to reports.
- **JSON CLI output (#561).** Added `--json` output to `excise info`, `excise text`, and `excise render`, and added `--password` handling to `info` and `text` to match the render command.
- **Automation command metadata (#561).** Added `automation.batch` to the shared command registry and corrected hidden-text audit metadata to point at the existing `audit` CLI command.
- **Platform examples (#564, #567, #568, #574).** Added AppleScript, Shortcuts, PowerShell, Power Automate Desktop, and Linux/GNOME examples that call the CLI/batch JSON contract instead of clicking the GUI.
- **Automation release gate (#561, #574).** Added `scripts/run-automation-smoke.sh` and wired it into `scripts/release-smoke.sh --only=automation`.

### Security
- **Automation boundary (#565).** Documented the CLI-first threat model: no background GUI automation listener is enabled by default, Release builds still exclude Roslyn GUI scripting unless explicitly enabled, mutating batch commands require explicit output paths, in-place overwrite is refused, and redaction requires `confirmDestructive: true`.

### Tests
- Focused gates passed: `BatchAutomationCommandTests`, `CommandMetadataCommandTests`, `PdfCommandRegistryTests`, and `PublicApiApprovalTests`.
- Full Debug build passed: `dotnet build excise.sln -c Debug`.

## [2.22.0] - 2026-07-04

Accessibility and assistive-technology readiness release. Additive public API
change in `Excise.Core.Automation`; no intended breaking change.

### Added
- **Shared semantic command metadata (#562).** Added `Excise.Core.Automation` with stable command IDs, labels, descriptions, shortcuts, CLI verbs, parameters, result fields, disabled reasons, and destructive/security flags.
- **CLI command metadata (#562).** Added `excise commands` and `excise commands <id> --json` so automation and batch workflows can query the same command model used by the GUI.
- **Accessibility command binding (#569).** Added the Avalonia `CommandAccessibility.CommandId` attached property, binding command metadata into accessible names, help text, unavailable status, and tooltips across the main menu, toolbar, search bar, page controls, redaction controls, and status surfaces.
- **Accessibility release gate (#570, #573).** Added `scripts/run-accessibility-smoke.sh` and wired it into `scripts/release-smoke.sh --only=accessibility`, producing a JSON report with automated check status and platform accessibility-tree probe status.
- **Accessibility checklist (#566, #570).** Added `docs/ACCESSIBILITY_RELEASE_CHECKLIST.md` for macOS AX/VoiceOver, Windows UI Automation, and Linux/GNOME AT-SPI verification on dedicated runners.

### Changed
- **Keyboard-only and dialog semantics (#572).** Preferences, Save Redacted Version, About, and dynamically-created message/prompt dialogs now expose accessible names/help text plus default/cancel button semantics.
- **Release checklist.** Accessibility is now reported separately from GUI display parity and packaged-app smoke.

### Tests
- v2.22 accessibility smoke passed: `logs/release-smoke_20260704_135843` (`accessibility` gate PASS).
- Focused gates passed: `PdfCommandRegistryTests`, `CommandMetadataCommandTests`, `AccessibilityRegressionTests`, `GuiWorkflowCoverageMatrixTests`, `DocumentationClaimTests`, and `PublicApiApprovalTests`.
- Full Debug build passed: `dotnet build excise.sln -c Debug`.

## [2.21.0] - 2026-07-04

GUI responsiveness and packaged-app release-gate hardening release. No intended
API break.

### Added
- **GUI responsiveness reporting (#577, #581, #582).** The desktop app records open-to-first-page-visible timing, background phase ordering, render cache stats, and PASS/WARN/FAIL budget status in a JSON report that release smoke can consume.
- **Packaged app responsiveness smoke (#582).** `scripts/release-smoke.sh` now supports a packaged-GUI direct-exec mode that launches the built macOS app with a real PDF, captures app stdout/stderr, validates the first-page report, and avoids taking keyboard or mouse focus by default.
- **Interaction latency coverage (#578, #583).** Focused GUI tests cover direct input paths for search typing, text selection feedback, redaction preview, form authoring, and form edits, plus first-page-before-background-work ordering.

### Changed
- **Render scheduling and cache behavior (#575, #579).** Visible page renders cancel/drop stale work, adjacent-page prefetch is sequenced behind the visible page, lazy thumbnail placeholders avoid front-loading all thumbnail renders, and responsiveness reports include cache-hit/miss and cache-size signals.
- **macOS packaged smoke stability.** The packaged-GUI smoke now wakes the active display briefly before launching the app, avoiding Avalonia native render-timer startup failures when the laptop display is asleep.
- **Benchmark wrapper cleanup (#536).** `scripts/run-benchmarks.sh` now routes through the maintained render-tooling entry points so corpus hotspot reports can separate excise render cost from reference-render and comparison overhead.

### Tests
- Focused responsiveness and scheduling gate passed: `dotnet test Excise.App.Tests/Excise.App.Tests.csproj -c Debug --filter "FullyQualifiedName~GuiResponsivenessBudgetTests|FullyQualifiedName~MainWindowRenderSchedulingTests|FullyQualifiedName~PdfRenderServiceCacheTests|FullyQualifiedName~ResponsivenessReportTests|FullyQualifiedName~GuiWorkflowCoverageMatrixTests"`.
- Packaged release smoke passed: `logs/release-smoke_20260704_133123` (package and packaged-GUI direct-exec gate; app first-page visible in `108ms` on the generated six-page smoke PDF).
- Broader cross-library benchmark epics (#344, #357) remain open; this release ships the GUI responsiveness gate and hotspot aggregation cleanup, not the full future benchmarking system.

## [2.20.0] - 2026-07-04

GUI interaction and redaction hardening release. No intended API break.

### Added
- **Adversarial redaction regression coverage (#555).** Added generated tests for AcroForm values and appearances, annotations and appearance streams, partial glyph overlaps, rotated text, hidden optional-content layers, password-protected fixtures with documented passwords, incremental-update previous revisions, and OCR/scanned-image recovery cases.
- **Packaged GUI smoke evidence (#558, #571).** Added `scripts/run-packaged-gui-smoke.sh` and wired it into `scripts/release-smoke.sh --packaged-gui`, producing JSON/markdown reports, launch logs, and screenshot artifacts for the packaged macOS `.app`.

### Changed
- **Redaction save safety.** Saved redacted copies now serialize only objects reachable from the current trailer roots, which prevents stale previous revisions, annotation appearances, and orphaned image/form content from being re-emitted.
- **Scanned-image redaction.** Named image XObjects removed from redacted page content are pruned from page resources when no surviving page content uses them, so object bytes do not remain reachable after save.
- **Redacted-copy safety report.** The GUI safety report now includes a raster redaction audit that warns/fails closed when raster image content still overlaps requested redaction areas.
- **GUI input coverage.** Previously skipped headless keyboard/mouse tests now use Avalonia Headless input injection, and release docs distinguish those routed-event tests from packaged-app launch evidence and opt-in native System Events key/mouse smoke.

### Tests
- Required redaction gate passed after redaction changes: `dotnet test --no-restore --filter "FullyQualifiedName~Redaction"`.
- Focused OCR/image redaction and redacted-copy safety tests passed.
- v2.20 release smoke passed: `logs/release-smoke_20260704_124540` (docs, build, redaction, signature, UI workflow, macOS package, packaged-GUI evidence, and diffcheck).

## [2.19.0] - 2026-07-04

Everyday PDF workbench final release gate. No intended API break.

### Changed
- **Release rendering dashboard (#491, #535, #546).** The current full contract-driven rendering report classifies `14,979/14,979` scanned pages as release `PASS`, with `0` missing contract pages, `0` failed expectations, and `0` unreviewed or rejected `PASS_ONE` rows.
- **CMYK, ICC, and transparency rendering.** DeviceCMYK transparency-group preview now uses document output-intent information where available, ICCBased CMYK and `/DefaultCMYK` paths use the managed ICC preview evaluator, and CMYK soft-mask/screen-blend and knockout cases from the release corpus are classified against accepted reference targets.
- **GUI display parity (#537, #541).** The headless GUI display suite now checks that the displayed Avalonia bitmap matches the renderer output, including the ACC compensation-report cover page.
- **Corpus tooling and progress reporting.** Long rendering runs write incremental/progress JSON, support large-PDF page sharding, use documented passwords from rendering contracts, and reclassify existing raw reports against current contract expectations without rerendering reference pages.
- **Release scope.** Broad font-model completion (#512, #513, #514, #515, #532), renderer performance optimization (#536), and narrower future renderer-quality issues remain tracked, but are explicitly deferred from this tag because the current release dashboard is clean.

### Tests
- Rendering quality reclassification: `logs/render-quality/release-prep-20260704/full-current-quality.json` reports `14,979 PASS`, `0` missing contracts, and `14,979` expectation passes.
- `dotnet test Excise.Cli.Tests/Excise.Cli.Tests.csproj --filter "FullyQualifiedName~CorpusScanClassificationTests"` passed: `42` passed, `0` failed.
- Release smoke evidence: signature, UI workflow, and PDF 2.0 renderer-conformance gates passed.

## [2.15.0] - 2026-06-11

Form workflow hardening release. Additive; no breaking changes.

### Added
- **Explicit flattened form copy workflow (#457, #459, #460).** The desktop app now exposes **Flatten Form** / **Save Flattened Form Copy...** so users can choose between preserving interactive form fields and baking values into static page content.
- **Form widget metadata API (#459).** `PdfField` now exposes effective `/Ff` flags, checkbox/radio/choice helpers, and `PdfFieldWidget` metadata so consumers can distinguish checkboxes, radio groups, combo boxes, push buttons, and per-widget export values.

### Changed
- **Filled-form saves now persist the edited values (#460).** The desktop form overlay synchronizes edits and authored fields into the service-owned document before save, so interactive filled forms round-trip correctly through Save As.
- **Form field keyboard workflow is more deterministic (#458).** Fields are ordered top-to-bottom/left-to-right for tab traversal, focus styling is clearer, single-line fields commit on Enter, multiline fields commit on Ctrl+Enter, focus loss commits, and Escape restores the last committed value.
- **Flattened form appearances are stronger (#459).** Text is clipped/wrapped within widget bounds, radio groups draw only the selected widget, and `/NeedAppearances` is parsed using the spec key while remaining compatible with older pluralized fixtures.
- **Save labeling is clearer for original documents (#460).** Original PDFs with form edits now advertise **Save Filled Copy** rather than the generic **Save a Copy** label.

### Tests
- Build remains warning-free.
- Focused core AcroForm/public-API tests passed: 44 passed.
- Focused desktop form/viewmodel workflow tests passed: 157 passed.
- Required redaction filter passed after touching shared save workflow code.
- Full built test suite passed locally: 7034 passed, 53 skipped.

## [2.14.0] - 2026-06-11

Flat-PDF typewriter editing release. Additive; no breaking changes.

### Added
- **Typewriter flat text editing (#453, #454, #455, #456).** The desktop app now has a Typewriter mode for placing, editing, moving, resizing, and deleting pending text boxes on ordinary PDF pages.
- **Core typewriter operation model.** `PdfTypewriterTextOperation`, `PdfTypewriterTextStyle`, and `PdfTypewriterTextApplier` provide a small immutable operation model and flattening service on top of `PdfGraphics`.
- **Viewer typewriter overlay API.** `PdfViewerControl` exposes `TypewriterTextOperations` plus created/edited/bounds/deleted events so hosts can keep pending flat-text edits in their own view models.

### Changed
- **Save state distinguishes redaction from ordinary edits.** Original files with pending redactions still use the redacted-copy workflow; original files with typewriter/form/page edits now advertise **Save a Copy** instead of the redaction-specific save label.
- The macOS native menu and in-window Edit menu now include Typewriter Mode.

### Tests
- Build remains warning-free.
- Core typewriter/edit/public-API tests passed: 11 passed.
- Avalonia public-API tests passed: 2 passed.
- Focused desktop viewmodel/viewer/typewriter workflow tests passed: 172 passed.
- Required redaction filter passed after touching the redaction save path.
- Full built test suite passed locally: 7025 passed, 53 skipped.

## [2.13.0] - 2026-06-10

Architecture hardening checkpoint release. No intended PDF behavior changes.

### Changed
- **MainWindowViewModel workflow split (#449).** Command initialization, form-authoring, hidden-text reveal, and redaction workflow code now live in focused partial modules, reducing the size and review risk of the main desktop view model while keeping the existing command and binding surface intact.
- **Renderer component split (#450).** `SkiaRenderer` path rendering and rendering state types were moved into focused renderer files without changing the public rendering API.
- **Viewer-control type split (#451).** `PdfViewerControl` event argument types and view/interaction enums now live in a separate partial file, keeping the control implementation more focused while preserving API compatibility.
- **Edit-operation foundation (#452).** Added a small immutable `PdfEditOperation` model for future typewriter, form, page-organization, redaction, and annotation workflows without enabling new editing behavior yet.
- **Dictionary optional-read helpers (#427).** `PdfDictionary` now exposes explicit `TryGetString` and `TryGetArray` helpers, and the document writer uses `TryGetArray` when preserving trailer `/ID` values.

### Tests
- Build remains warning-free.
- Focused core public API/edit/dictionary tests passed: 87 passed.
- Avalonia public API tests passed: 7 passed.
- Focused desktop viewmodel/keyboard/redaction tests passed: 238 passed, 4 skipped.
- Focused rendering/operator/differential tests passed: 222 passed, 2 skipped.
- Full built test suite passed locally: 7011 passed, 53 skipped.

## [2.12.2] - 2026-06-10

macOS integration checkpoint release. No PDF behavior changes.

### Fixed
- **macOS native menu integration (#447).** The desktop app now installs a native macOS menu bar and hides the in-window menu on macOS, while keeping the in-window menu visible on Windows and Linux.
- **macOS titlebar spacing (#447).** The custom title label is shifted away from the traffic-light window controls on macOS so the title text no longer overlaps the close/minimize/zoom buttons.

### Tests
- Build remains warning-free.
- Focused GUI/viewmodel slice passed: 176 passed, 3 skipped.
- Full built test suite passed locally: 7001 passed, 53 skipped.

## [2.11.0] — 2026-06-08

Archival conformance + viewer-quality release. Additive; no breaking changes.

### Added
- **PDF/A-1b conformance (#425).** Embedded subset CID fonts now emit a `/CIDSet` in the FontDescriptor (covering all glyph slots of the retain-gid subset, as PDF/A-2 §6.2.11.4.2 requires it be complete).
- **Sharp high-zoom in the continuous reading view (#371 pt1).** Continuous mode now renders each page at a zoom-aware DPI (scaling with zoom, capped to bound memory) and caches by `(page, dpi)`, so zoomed reading stays crisp instead of upscaling a fixed-DPI bitmap.

### Developer tooling
- **`Excise.Benchmarks` (#344).** A BenchmarkDotNet project measuring parse / render / text-extract (replacing the orphaned `run-benchmarks.sh` target); kept out of the shippable graph.

## [2.10.0] — 2026-06-08

Library DX + authoring-correctness release. Additive; no breaking changes
(public-API gates confirmed).

### Added
- **Public-API gate for the viewer libraries (#384).** A new lightweight, non-GUI `Excise.Avalonia.Tests` project snapshots the public surface of `Excise.Avalonia` and `Excise.Rendering` against committed baselines (same treatment `Excise.Core` got in #383) — any API change now fails CI until the baseline is intentionally regenerated.
- **`PdfField.ButtonExportValues` (#424).** For a Button field (e.g.

### Fixed
- **Base-14 text encoding mojibake (#426).** `PdfFont.EncodeString` formatted the Unicode code point in decimal as a `\ddd` escape, but PDF reads `\ddd` as octal — so `é`, `—`, `·`, curly quotes etc.

## [2.9.0] — 2026-06-08

Viewer + macOS-reader + archival release. Additive; no breaking changes
(public-API gate confirmed for `Excise.Core`).

### Added
- **Continuous (reading) view mode for `Excise.Avalonia` (#371).** New `PdfViewerControl.ViewMode` (`PdfViewMode.SinglePage` default | `Continuous`).
- **macOS: open PDFs from Finder / be a default reader (#420).** The app handles the macOS file-activation event (Finder double-click, Dock, `open -a`), and the generated `.app` `Info.plist` declares `CFBundleDocumentTypes` for `com.adobe.pdf` so excise registers as a PDF handler.
- **PDF/A archival output.** `PdfDocumentBuilder.PdfA(PdfAConformance.PdfA2B)` adds the document structures PDF/A requires at save time — an XMP metadata packet with the `pdfaid` identifier and an sRGB OutputIntent (embedded ICC profile).
- **Trailer `/ID`.** Newly authored documents now always get a file-identifier array in the trailer (ISO 32000-1 §14.4) — required by PDF/A and recommended generally; an existing `/ID` is preserved.

### Fixed
- **Chronic headless GUI test host-crash (#363), part 2.** The headless test runner now closes each test's windows afterward (tracked via Avalonia's global routed-event streams), bounding the shared dispatcher's live-window set, and the heavy `*_MatchesBaseline` visual-regression tests are excluded from the PR gate (owned by the nightly job).

## [2.8.0] — 2026-06-08

Operator render-coverage release (#350). Additive; no breaking changes.

### Added
- **Dash pattern (`d`) rendering.** The dash operator was parsed but ignored by the renderer, so dashed strokes drew solid.
- **Authoritative operator inventory test.** One stream exercising every standard content-stream operator, each asserted to parse **and** survive a parse→write→parse round-trip through `ContentStreamWriter`.

### Tests
- **Shading (`sh`) render output is now actually verified.** Earlier shading tests referenced a `/Shading` resource the test PDFs never contained, so the axial/radial gradient code path ran as a no-op.
- Dash render tests assert real behavior (a dash leaves measurable gaps vs.

## [2.7.0] — 2026-06-06

Fillable-table authoring + PDF/UA accessibility hardening. Additive; no breaking
changes (public-API gate confirmed).

### Added
- **`PdfDocumentBuilder.FillableTable(...)`.** Renders a table whose body cells are interactive AcroForm fields (text input, checkbox, or dropdown per cell) — a fillable grid.
- **PDF/UA hardening for tagged output (#407).** is wrapped in `/Artifact` so every piece of page content is tagged or an artifact.

## [2.6.0] — 2026-06-06

Font, accessibility, and image-filter additions. All additive; the public-API
gate confirms no breaking changes.

### Added
- **Font subsetting + CFF/OpenType embedding (#393).** Embedded TrueType fonts are now subsetted to the glyphs actually drawn (retain-GID `glyf`/`loca`, composite-glyph closure, subset tag) — e.g.
- **Embedded fonts in the high-level builder (#398).** `TextStyle.WithFont(...)` and `PdfDocumentBuilder.DefaultFont(...)` let the friendly facade render arbitrary Unicode (not just base-14); the same typeface across sizes/weights embeds as one subset.
- **Tagged-PDF authoring / PDF-UA (#275).** `PdfDocumentBuilder.Tagged()` emits a logical structure tree (StructTreeRoot + Document→H1-H4/P/Table), marked content (`BDC`/`EMC` + MCID, `/MCR` with `/Pg`, `/ParentTree`), and catalog `/MarkInfo`, `/ViewerPreferences /DisplayDocTitle`.
- **Image filters: JBIG2 + JPEG2000 (#325).** Pure-managed JBIG2 decoder (MQ arithmetic + generic region, template 0) wired into the stream decompressor with strict decode-or-passthrough fallback (no silently-wrong images).

### Notes
- Remaining tracked follow-ups: full PDF/UA conformance (artifacts, TR/TD, form-field tagging), CFF glyph subsetting, JBIG2 symbol/text regions, full JPEG2000 decode.

## [2.5.0] — 2026-06-06

Completes the **PromptResponse writer epic (#382)** — excise can now author
accessible, fillable, Unicode PDFs from structured content. All additive; the
public-API gate confirms no breaking changes.

### Added
- **Unicode text + embedded fonts (#378).** `PdfFont.FromFile(path, size)` / `FromTrueType(bytes|Stream, size)` embed a TrueType font as a Type0 / Identity-H composite font with a ToUnicode CMap, so arbitrary Unicode (CJK, Arabic, accented Latin, Greek, Cyrillic, …) both renders and stays extractable.
- **High-level text layout (#379).** `PdfGraphics.DrawText(text, font, brush, PdfRectangle, …)` word-wraps into a box and returns a `TextLayoutResult` (used height + overflow) for flowing across boxes/pages; `MeasureText(...)` returns wrapped size.
- **AcroForm field options (#380).** `/TU` tooltip (accessible name) on all field types; `/MaxLen` + comb for text fields; `AddDateField` (Acrobat `AFDate` format/keystroke actions); `SetTabOrder` (page `/Tabs`).
- **Document metadata (#381).** `PdfDocument.SetTitle/SetAuthor/SetSubject/ SetKeywords/SetCreator/SetProducer` (creates the `/Info` dict on demand) and a read/write `Language` property (catalog `/Lang`, required by PDF/UA).
- **`PdfDocumentBuilder`** gains `Title/Author/Subject/Keywords/Language`, `DateField`, and `tooltip`/`maxLength`/`comb` passthrough on fields (with `/TU` defaulting to the visible label for screen readers).

### Changed
- `PdfFont` text-encoding/measurement/metrics members are now `virtual` so embedded fonts can override them; standard-font behavior is unchanged.
- Dependencies: bumped `FluentAvaloniaUI` to the latest preview (#340; full de-preview is blocked on an upstream FluentAvalonia 3.x stable for Avalonia 12).

### Tests / CI
- Raised `Excise.Core` CI line coverage to ~93% and ratcheted the gate to 92.5% (#351); CI installs `fonts-dejavu-core` so the embedding tests run deterministically.

## [2.4.1] — 2026-06-06

Packaging, API-stability, and CI hardening on top of v2.4.0. No public-API
changes (enforced by the new gate) — a pure patch.

### Added
- **Public-API gate (#383).** `PublicApiApprovalTests` snapshots the full `Excise.Core` public surface against a committed baseline (`Excise.Core.Tests/PublicApi/Excise.Core.approved.txt`); any public-API change fails CI until intentionally re-approved (`APPROVE_PUBLIC_API=1`).
- **SourceLink + symbols.** The three publishable libraries (`Excise.Core`, `Excise.Rendering`, `Excise.Avalonia`) now ship portable `.snupkg` symbol packages with SourceLink and deterministic CI builds (shared `Packaging.props`), so consumers can step into the source while debugging.
- README "Versioning & API stability" section documenting the SemVer policy, the `Excise.Core.Authoring.*` stable writer surface, and local-feed (not nuget.org) distribution.

### Fixed
- **Release pipeline cold-cache restore (#387).** `release.yml` now sets `DOTNET_NUGET_SIGNATURE_VERIFICATION=false` (matching `ci.yml`) so a version-bump cache miss no longer fails the license-manifest step with NU3012 (revoked ReactiveUI/Splat signing cert).
- `generate-license-manifest.sh` no longer hard-fails on a cold NuGet cache and no longer suppresses restore output.

### CI / dev
- Headless GUI tests (`Excise.App.Tests`) now run only when GUI-relevant paths change (or on `main`), so library-only PRs aren't gated on the slow GUI suite.
- Quarantined the flaky `KeyboardShortcutTests.CtrlS_SavesFile` on headless CI (#363) — it intermittently deadlocked the Avalonia dispatcher and crashed the test host.

## [2.4.0] — 2026-06-05

Adds a friendly, high-level **PDF authoring** API so third-party .NET apps can
generate PDFs from structured content without touching coordinates — the
writer-side facade tracked by #383 (PromptResponse writer epic #382).

### Added
- **`Excise.Core.Authoring.PdfDocumentBuilder` — high-level writer facade (#383).** A fluent, flow-layout builder over the existing `PdfGraphics` / `AcroFormAuthoring` API.
- **Authoring value types.** `PageSize` (Letter/Legal/A4/A3/A5 + `Landscape()`/`Portrait()`), `PageMargins` (`All`/`Symmetric`/`Default`), immutable `TextStyle` record (family/size/bold/italic/color/alignment/ line-spacing/space-after with `With…` helpers), `FontFamily`, `LayoutContext`.
- README: a copy-paste "Authoring PDFs from scratch (high-level)" sample.

### Notes
- Targets the base-14 fonts and Latin text available today; Unicode / embedded TrueType-OpenType fonts (#378), richer text layout (#379), more AcroForm field options (#380), and document metadata setters (#381) extend the facade.
- Verified against external readers: generated forms pass `qpdf --check`, `pdfinfo` reports a live `AcroForm`, content auto-paginates, and `pdftotext` extracts all text. 17 new tests; full `Excise.Core` suite green (2744 passing).

## [2.3.1] — 2026-06-04

### Fixed
- **Thread-safe object resolution (#376).** A single `PdfDocument` resolved indirect objects through one shared lexer with a mutable stream position, so concurrent reads — e.g.

## [2.3.0] — 2026-06-04

Turns excise's engine into reusable libraries for the wider .NET/Avalonia ecosystem.

### Added
- **`Excise.Avalonia` — reusable Avalonia PDF viewer control (#365).** The `PdfViewerControl` (zoom/pan, navigation, text selection, search highlights, annotations, links, form-field overlays) is extracted from the `Excise.App` app into a standalone, dependency-light library (depends only on `Excise.Core` + `Excise.Rendering` + Avalonia + SkiaSharp).
- **Framework-neutral render API (#366).** `Excise.Rendering.SkiaRenderer` gains `RenderPage(page, options, CancellationToken)` (cancellable between content-stream operators, companion to #346) and `RenderPageToPng(page, Stream, …)` for non-Skia consumers.
- **NuGet-packable trio.** `Excise.Core`, `Excise.Rendering`, and `Excise.Avalonia` carry package metadata + per-package READMEs; `dotnet pack` produces three valid `.nupkg`s (attached to this release; not pushed to nuget.org).

### Changed
- `Excise.App` now consumes `Excise.Avalonia` rather than embedding the control; behavior is unchanged.

## [2.2.2] — 2026-06-03

### Fixed
- **Outline and page-preview (thumbnail) sidebars are now independently toggleable (#369).** The outline panel was nested inside the thumbnails sidebar, so "Show Outline" did nothing unless "Show Thumbnails" was also on, and hiding thumbnails hid the outline too.

### Added
- **Toolbar toggle buttons** for the outline (📑) and page previews (🗐), plus **keyboard shortcuts** Ctrl+Shift+O (outline) and Ctrl+Shift+T (thumbnails) — the toggles were previously buried as View-menu checkboxes only.

## [2.2.1] — 2026-06-03

Maintenance release: parser-robustness hardening, a rotated-page render fix,
CI test-flake fixes, and a documentation refresh. No new user-facing features;
closes the remaining open **bug/fix** issues on top of v2.2.0 (the v2.2.0
release shipped the redaction-security trio; this release adds the
parser-hardening / known-issues batch that landed afterward).

### Fixed
- **Rotated PDFs render unrotated** — `SkiaRenderer` now honours the page `/Rotate` entry (0/90/180/270), sizing the bitmap in visual dimensions, so rotated pages display the right way up.
- **Writer re-emitted cross-reference plumbing** — `/ObjStm` and `/XRef` streams are no longer copied into the rewritten body, so a Form XObject flattened out of a compressed object stream can't survive redaction.
- **Inline-image `EI` scan was unbounded** on malformed image data lacking a `/L` length, causing O(n²) blowup; the scan is now bounded.
- **Parser hardening against hostile input** — content-stream array recursion is depth-bounded and a `CancellationToken` is threaded through parsing so a malicious/degenerate document can't hang or stack-overflow.
- **Exception-swallowing audit** — best-effort `catch` blocks no longer swallow `OutOfMemoryException` (and other critical failures) during the ToUnicode-CMap parse and related paths.
- Added an end-to-end CID/Type0 (CJK) redaction regression test on a real Identity-H PDF, locking in the v2.1.0 `RawBytes` reconstruction fix.

### Security / robustness
- **Malformed-PDF fuzz / property tests** for the parsers (`ParserFuzzTests`): on hostile or malformed bytes the parser must parse them or fail with a *typed* `PdfParseException` — never a raw CLR crash.

### CI / tests
- Removed a redundant 15s `OperationStatus` wait in the AcroForm overlay test and raised over-tight GUI timeouts (3s → 15s) that masked CI slowness as a hang; raised the cold-CI first-render budget (15s → 60s) in the headless render baseline test, which renders in ~2s locally but can exceed 15s on a cold CI runner (JIT + xvfb + SkiaSharp native init).

### Docs
- Refreshed stale `CLAUDE.md` notes: the redaction-engine architecture now points at `Excise.Core` (not the removed `Excise.App/Services/Redaction/`), and the frozen "Current Status (v1.4.0)" block now points at `CHANGELOG.md` / GitHub Releases so the version no longer goes stale in-file.

## [2.2.0] — 2026-06-03

Redaction-security release: closes the remaining content-type and
coordinate gaps so redaction reliably removes — not merely covers — every
way content can land under the redaction area. Also restores a working CI
gate (it had been silently broken) and raises Excise.Core coverage.

### Added / Security
- **Inline-image redaction** (`BI…ID…EI`) — the parser now retains the embedded pixel bytes and the writer re-emits valid inline-image syntax, so an inline image overlapping the redaction area is removed, not just covered.
- **Form XObject redaction** — overlapping forms are flattened into the page (Matrix/BBox-correct, resources merged with collision renaming, nested forms recursed) and redacted; the now-orphaned form objects are pruned so the writer can't re-emit the removed content.

### Fixed
- **Rotation-aware redaction** — `PdfPage.ToContentStreamCoordinates` maps a visual-space rectangle into content space for `/Rotate` 0/90/180/270; the GUI no longer mis-targets redactions on rotated pages.
- **Outline / text-string decoding** — `PdfString` now decodes the PDFDocEncoding 0x80–0x9F / 0x18–0x1F / 0xA0 ranges (em/en dash, curly quotes, ligatures, €, …) instead of rendering C1 control characters as tofu boxes (e.g.

### CI / tests
- Restored the Build/Test/Coverage gate, which had been masked by a failing veraPDF-install step: best-effort veraPDF, NuGet signature-verification workaround (revoked ReactiveUI cert), refreshed the redaction-architecture check, and fixed the coverage-report path.
- Raised Excise.Core coverage and set the enforced gate to the level CI meets.

## [2.1.0] — 2026-06-01

Graduates the `v2.1.0-rc1..rc8` line to a final release. v2.1 builds out the
pure-.NET stack with encryption, forms, advanced transparency, full CJK, and a
much broader content-stream operator set, then this release caps it with a
performance pass, dependency hygiene, and a round of stability/security
hardening.

### Added
- PDF **encryption/decryption** — RC4 (V1/V2) and AES-128/256 (V4/V5).
- **AcroForm** read, edit, and authoring — fill, flatten, create fields.
- **Advanced transparency** — soft masks, transparency groups, full blend-mode set.
- **Type0 / CID (CJK)** fonts — Identity-H/V, ToUnicode CMap, vertical writing, CFF wiring.
- **Optional content groups** (OCGs) + **XMP** metadata extraction.
- **Embedded-file** extraction.
- Full content-stream **operator coverage** — text-state ops, color spaces, marked content, shading.
- veraPDF / corpus **conformance harness**.

### Changed / Performance
- GUI **Release startup profile** — ReadyToRun + TieredPGO + concurrent GC; **~36% faster cold start** (1.18 s → 0.75 s).
- ReadyToRun for `Excise.Cli`.
- Moved off preview packages and bumped to latest stable: **Avalonia 12.0.4, ReactiveUI 23.2.27, SkiaSharp 3.119.4, .NET 10.0.8**.
- Removed the IdlerGear integration; refreshed stale docs (versions/architecture) and archived obsolete plan docs.

### Fixed (stability & security hardening)
- Parser **recursion-depth guard** — deeply nested hostile PDFs throw instead of StackOverflow.
- Inline-image **`/L` length** used to avoid false-positive `EI` in binary data.
- **Redaction re-encodes kept CID/CJK text** with original codes instead of unrenderable Unicode.
- ToUnicode CMap parse no longer swallows fatal exceptions.
- Headless test harness wires ReactiveUI to the Avalonia dispatcher — fixes a cross-thread `CanExecute` crash.

### Tests
- +18 tests: parser recursion limits, inline-image `/L`, CID-redaction pipeline, and previously-untested operators (`sh`, marked content, `BX`/`EX`, `d0`/`d1`).

### Known limitations / deferred
- Inline-image redaction round-trip (#354), Form XObject redaction (#355, flatten-then-redact), and rotated-page redaction (#356) remain open.

## [2.0.0] — 2026-04-25

The headline of v2.0 is **a complete rewrite of the PDF stack**. v1.0 sat on
top of PdfPig + PDFsharp + PDFtoImage (PDFium) + Tesseract.NET; v2.0 ships a
pure-.NET stack of excise-owned libraries — Excise.Core (parser/writer),
Excise.Rendering (SkiaSharp renderer), and Excise.Ocr (system tesseract shell) —
with no external PDF dependencies remaining. Same redaction guarantee, same
GUI, fewer moving parts, and the renderer now handles real-world PDFs from
WeasyPrint, Word, XEP, and CJK toolchains without falling back to garbage.

### Added

#### Excise.Core — pure-.NET PDF parser, writer, and content-stream library
- M1: parser for objects, indirect references, xref, encrypted streams.
- M2: text extraction with letter-level positions, replacing PdfPig.
- M3: document writing — incremental save, full rewrite, object streams.
- M4: graphics API — `PdfGraphics` with path, text, image, and state ops.
- Content-stream parsing + serialization (`ContentStreamReader` / `ContentStreamWriter`) backing redaction.
- Glyph-level text segmentation: `LetterFinder`, `OperationReconstructor`, `GlyphRemover`, plus `PdfPageRedactionExtensions.RedactArea` / `RedactAreas` / `RedactText`.
- Image redaction: `ImageRedactor` tracks the CTM through `q`/`Q`/`cm` and removes Image XObject `Do` ops that overlap the redaction area.
- Hidden-text detection: `HiddenTextDetector` finds text occluded by later opaque obstructions (the classic "black box on top of text" bad-redaction pattern).
- Document authoring: `PdfDocument.CreateNew()`, `Pages.AddBlank(w, h)`, `page.GetGraphics()` — synthesize PDFs in-memory without the legacy stack.
- Page manipulation APIs: `Pages.Add`/`Insert`/`RemoveAt`, `page.Rotation`.
- Indirect /Length stream resolution via parser callback (XEP, LibreOffice, and other toolchains routinely use this).
- `PdfPage.GetFont` resolves indirect /Font references (WeasyPrint, Word, Office, and almost every browser-derived PDF).

- M5: full renderer covering text, paths, images, transparency, clipping paths, soft masks, ExtGState, color spaces, shading, and inline images.
- Embedded font support: OpenType container with a Unicode cmap derived from /Differences.
- Type0 / CIDFontType2 (Identity-H) — full CJK rendering pipeline.
- Browser-style flipped text matrix (`Tm = 1 0 0 -1 e f`) handled correctly in both the simple-font and Type0 paths — fixes upside-down rendering found in the IRS-1040 footer, every WeasyPrint-produced page, and all CJK.
- Layout-correct text advance for non-embedded fonts via the PDF's `/Widths` table (instead of the system fallback's `MeasureText`).
- Tc / Tw scaled by the text-matrix X-scale, per PDF spec 9.4.4 (fixes the "Word-derived government form mid-word gap" pattern).
- TJ array kerning routed through the text-matrix X-scale, not Y-scale — fixes 6%-per-glyph drift in non-uniform Tm headers (SCOTUS opinions).
- Td/TD offsets transformed through the text matrix per PDF spec 9.4.2.
- Wingdings / dingbat fallback: when an embedded CFF subset wraps cleanly but Skia can't extract any glyph outlines, fall back to a system symbol font (Noto Sans Symbols2) so the user sees a glyph instead of `⊠`.
- Visual regression test infrastructure with PNG baselines.
- Dropped `PDFtoImage` / `PDFium` native dependency.

- New project.
- Differential OCR auditor: render the page twice (once with overlays stripped, once without), OCR both, diff the word sets — surfaces text hidden inside rasters by overlay, the rasterized analogue of structural redaction.
- Replaces the previous Tesseract.NET nuget binding (which pinned to a leptonica version no longer shipping on modern Linux).

- `excise render <file> -o out.png [--page N] [--dpi N]`
- `excise redact <file> -o out.pdf --text "PHRASE"` — glyph-level removal.
- `excise audit <file> [--deep] [--json]` — structural and (with `--deep`) differential-OCR audit of hidden text.
- `excise ocr <file>` — OCR the page and emit TSV.

- New reusable `PdfViewerControl` (Avalonia UserControl) with overlay layers for selection, search highlights, redaction marquee, and hidden-text reveal.
- `MainWindow` rewritten on top of `PdfViewerControl`.
- Reveal Hidden Text — Tools → "Reveal Hidden Text" toggle.
- Open PDF from command-line argument on startup.

### Changed

- All seven GUI services migrated from PdfPig / PDFsharp / PDFtoImage to Excise.Core / Excise.Rendering: `PdfRenderService`, `PdfTextExtractionService`, `PdfSearchService`, `SignatureVerificationService`, `PdfDocumentService`, `BatesNumberingService`, `RedactionService`.
- `RedactionService` unified — `RedactArea` (mouse marquee) and `RedactText` (find-and-redact) now share a single Excise.Core pipeline; the previous parallel PdfSharp+PdfPig path is gone.
- The legacy `Excise.App.Redaction` library (and its `pdfer` CLI) deleted — glyph-level redaction lives in Excise.Core; the Excise.Cli `redact` command replaces `pdfer`.
- System-font fallback widened: strip the 6-letter PDF subset prefix, match by family prefix instead of exact name, and recognize Semibold / Medium as Bold.
- Build is clean — 0 warnings, 0 errors across all projects.

### Removed

- **PdfPig 0.1.11** — replaced by `Excise.Core.Text`.
- **PDFsharp 6.2.2** — replaced by `Excise.Core.Document` + `Excise.Core.Writing`.
- **PDFtoImage 4.0.2** + native PDFium — replaced by `Excise.Rendering` (Skia).
- **Tesseract.NET nuget** — replaced by `Excise.Ocr` (CLI shell).
- **Excise.App.Redaction** project + **`pdfer` CLI** — replaced by Excise.Core glyph-level redaction + `excise redact`.
- **Excise.App.Demo** + Validator tools — superseded by Excise.Cli + the new visual regression suite.

### Fixed

- Stream `/Length` as an indirect reference no longer rejected (XEP, LibreOffice).
- `\<EOL>` line continuations in literal strings (PDF spec 7.3.4.2) stripped correctly — fixes the `⊠` placeholders that appeared at the end of long underline runs in Word-derived government forms.
- Embedded-font /Widths loaded *before* the CFF→OpenType wrapper runs; previously every embedded font got hmtx widths from the previously-active font (or zero for the first font), producing visibly broken layout on multi-font pages — every page after the cover of any XEP-produced book.
- AGL reverse lookup synthesizes `uniXXXX` names for BMP codepoints not in the named-glyph table — required for CFF subsets keyed on uniXXXX names.
- Post-wrap outline probe: if a wrapped CFF resolves cmap entries but produces no glyph outlines, fall back to a system font instead of rendering empty space (catches a class of XEP-produced ZapfDingbats subsets where Skia's CFF interpreter can't extract charstrings).
- Y-flip applied conditionally on the sign of `Tm.d`, fixing upside-down text in browser-flipped Tm content (CJK, WeasyPrint, IRS-1040 footer).
- Effective font size computed from the text matrix Y-scale (handles the common `1 Tf` + scaled `Tm` idiom).
- Cursor advance honors text-matrix non-uniform scaling.
- `CodePagesEncodingProvider` registered for Windows-1252 / WinAnsi support.
- Search highlights refresh when the user changes pages manually.
- Birth-cert form layout: routes non-embedded fonts through the PDF's `/Widths` array for cursor advance instead of the substituted system typeface's metrics — fixes mid-word gaps in TJ-kerning-heavy PDFs.

- `PdfViewerControl_PageChanged_FiresEvent` deflaked.

### Verified rendering

### Migration

- `Excise.App.Redaction` (library) → `Excise.Core.Text.Segmentation` — use `page.RedactArea(rect)` / `page.RedactAreas(rects)` / `document.RedactText("phrase")` from `PdfPageRedactionExtensions` / `PdfDocumentRedactionExtensions`.
- `pdfer` CLI → `excise redact` — same options.
- PdfPig text extraction → `Excise.Core.Text` — `PdfDocument.GetText(page)` and `PdfDocument.GetLetters(page)`.
- PDFsharp `PdfDocument` → `Excise.Core.Document.PdfDocument` — note that `PdfDocument.Open(stream)` now takes ownership semantics via `Open(stream, ownsStream)`.
- PDFtoImage → `Excise.Rendering.SkiaRenderer.RenderPage(page, options)`.

### Known gaps deferred to v2.1+

- PDF encryption / password handling (#237) — v2.1.
- Partial glyph rasterization for redaction cuts that bisect a glyph (#278).
- PDF Annotations (#271), Interactive Forms (#272), Tagged PDF (#275), Advanced Transparency (#274), Multimedia (#273) — v2.2.
- Compass-image-style inline-image-with-Smask cases that still fall back to placeholder rendering (covered indirectly by #274).

### Test counts at release

- Excise.Core.Tests: 442 passing, 2 skipped
- Excise.Rendering.Tests: 175 passing
- Excise.Cli.Tests: 7 passing
- Excise.App.Tests: 221 passing, 2 skipped (require Tesseract installed)

## [1.0.0] — 2026-01-11

First major stable release. Cross-platform PDF editor with **true
glyph-level redaction** — content removed from the PDF structure, not just
visually covered. Built on PdfPig + PDFsharp + PDFtoImage + Tesseract.NET.

See the GitHub release for full v1.0.0 notes:
https://github.com/marctjones/excise/releases/tag/v1.0.0

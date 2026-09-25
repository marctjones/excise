# Scope and known limitations

What excise is for, and where it stops. Current limitations are tracked in GitHub Issues.

excise targets an everyday PDF workbench: open, read, search/copy, annotate,
organize pages, fill/flatten forms, add flat typewriter text, audit hidden
content, and perform true content-level redaction. It is not an Acrobat Pro
replacement for prepress, color-managed print production, JavaScript workflows,
portfolio workflows, or certificate-authority trust decisions.

Current release-quality limitations are tracked in GitHub Issues and surfaced in
release notes:

- **Printing — macOS, Windows and Linux** (#1545, superseding #621; #1546; #1710).
  File → Print… (⌘P / Ctrl+P) prints the document **as currently edited**:
  unsaved page changes, filled form fields, pending type-over text, and
  pending redactions, which are *removed* from the printed copy rather than
  covered. Page scaling (shrink oversized / fit to page / actual size) is in
  Preferences → Printing, and each page is turned to the paper orientation
  that fits it. Print… is disabled when the document's permissions deny
  printing (`/P` bit 3), and also when they allow only degraded printing
  (bit 12 clear), because excise cannot produce degraded output. To print,
  excise writes a temporary plaintext copy (owner-only, under the app's cache
  folder) and deletes it when the print operation ends.
  - **macOS** opens the standard print sheet — printer, copies, page range,
    paper, orientation, scale, duplex where the driver supports it, preview,
    and the PDF menu (Save as PDF). Pages are drawn by macOS's own PDF
    renderer, not excise's, so small visual differences from the viewer are
    possible.
  - **Windows** opens the standard Windows print dialog (printer, page ranges,
    copies and collation, and the printer's own Preferences for paper,
    orientation and duplex). There is no print preview. Pages are rasterised by
    excise's own renderer at the printer's resolution, capped at 600 DPI, and
    sent through the Windows print spooler, so the printout matches the viewer
    and no Acrobat or other PDF handler is needed. "Print to file" is hidden;
    choose *Microsoft Print to PDF* instead, or use Save As. Each annotation's
    *print* flag decides whether it reaches paper (#1573, ISO 32000-2 §12.5.3),
    as in Acrobat and macOS: review markup without that flag is not printed
    even though the viewer shows it, and a print-only stamp or watermark
    (*NoView* + *Print*) is printed even though the viewer does not show it.
    ⚠️ The Windows path was built and unit-tested on macOS and
    has not yet been checked on a Windows machine.
  - **Linux** prints through CUPS (#1710). Linux gives excise no system print
    dialog — Avalonia has no printing support, and GTK's and Qt's dialogs
    belong to their own toolkits — so excise shows its own small chooser:
    the CUPS queues, with your system default preselected, copies with
    collation, and a page range. The PDF is handed to `lp` unchanged, which is
    CUPS's native spool format, so the printout is your document as the
    printer's own filters render it. Paper, orientation and duplex come from
    the queue's defaults (set them in your desktop's printer settings, or with
    `lpoptions`); *Fit to page* is passed through as CUPS's `fit-to-page` and
    the other two scaling modes leave placement to the queue, because CUPS has
    no shrink-only mode. When CUPS is not installed, the scheduler is not
    running, there are no queues, or `lp` refuses the job, Print… says so and
    quotes CUPS's own message — it never reports a job it did not submit.
    **What is tested:** the queue parsing, the `lp` command line, permission
    gating and every failure path run on every platform in the normal test
    suite; `scripts/run-linux-print-test.sh` prints a real multi-page PDF to a
    real `cupsd` in a container and checks the page count with qpdf/mutool, not
    with excise. Printing to physical hardware has not been checked, and there
    is no print preview.
- **Digital signatures** — excise checks ByteRange structure, verifies the
  detached CMS signature/digest over the signed bytes, and evaluates the signer
  certificate chain against the OS trust store, reporting a consolidated state
  (valid+trusted / valid-but-untrusted / invalid / indeterminate). Certificate
  revocation (CRL/OCSP) is deliberately not checked — it would require network
  access — and timestamp/LTV (long-term validation) material is not evaluated
  (#466).
- **Rendering fidelity** — the current release dashboard classifies every
  contracted page as release `PASS`. Remaining non-exact rows are issue-linked
  accepted-reference matches, malformed-input/refusal classifications, or named
  accepted limitations rather than unclassified `DIFF` blockers (#491).
  Niche color/shading residuals and deeper font-model work remain tracked for
  future releases (#512, #513, #514, #515, #532).
- **Color-managed print preview** — excise renders DeviceCMYK through a
  deterministic screen-preview conversion, resolves `/DefaultCMYK` and ICCBased
  CMYK through managed ICC preview support, and uses document output-intent data
  in the CMYK transparency-preview paths covered by the release corpus. It is
  still not a prepress proofing engine; shade/tone-only differences are tracked
  below missing content, geometry, and unreadable-output defects.
- **Encrypted and malformed PDFs** — PDFs requiring a non-empty user password,
  some owner-password-only flows, unsupported compression filters, invalid
  geometry, and intentionally malformed xref/stream structures are classified by
  the corpus scanner rather than treated as everyday-release blockers.
- **Text-extraction parity** — redaction completeness is bounded by extraction
  coverage: `RedactText` cannot remove what excise cannot read, and reports
  success anyway (#637). This is a dated measurement, not a standing property.
  Measured against `mutool` (1.27.2) on 2026-09-08 across 332 pages / 13
  fixtures (real-world government PDFs plus checked-in edge-case fixtures
  covering CJK/Type0 text and scrambled glyph order), excise extracted **100.0%**
  of mutool's Unicode letter/digit count in aggregate, counted per-script and
  not ASCII-folded so CJK/accented-text loss cannot cancel out on both sides.
  The worst per-page coverage was 0.946 and the worst per-page content
  similarity 0.923; no page fell below 0.92. The earlier 102.6% over-extraction
  figure and the marked-content `/Artifact` leak it was attributed to (#649,
  closed) no longer reproduce. A green gate means "no worse than the checked-in
  floors", not "no blindness": the floors were set at whatever the behaviour was.
  The per-page floors are checked in at `tests/extraction-parity/baseline.json`
  and ratchet; regenerate with `scripts/check-extraction-parity.sh --update`
  (requires `mutool`), and see #645/#513. Re-run the gate before restating any
  of these numbers.

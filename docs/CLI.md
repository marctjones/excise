# Command line

The `excise` command. The stable automation contract (JSON output, batch workflows, exit codes) is in [AUTOMATION_API.md](AUTOMATION_API.md).

## CLI (`excise`)
```bash
excise info              <file>           [--json] [--password P]
excise text              <file>           [--json] [--password P]
excise letters           <file>
excise render            <file>           -o out.png  [--page N] [--dpi N] [--password P] [--json]
excise commands          [id]             [--json]
excise batch             <workflow.json>  [--json] [--progress] [--output report.json]
excise draw              <file>                                 # graphics-API demo
excise redact            <input> <output> <text>  [--case-sensitive]
excise optimize          <input> <output> [--preset lossless|high|standard|screen] [--password P] [--json]
excise fill-form         <input> <output> --field Name=Value [...] [--flatten]
excise add-field         <input> <output> --type T --name N --page P --rect "l,b,r,t" [--value v] [--option o]...
excise autodetect-fields <input> [output] [--apply]
excise audit             <file>           [--deep] [--json]
excise unredact          <file>           [--mode certain|residue|both] [--dictionary w.txt]
                                          [--ocr] [--include-deferred] [--restore out.pdf] [--json]
excise ocr               <file>
excise demo
```

`audit --deep` runs differential OCR — renders the page twice (once with overlays stripped) and diffs the OCR text — to catch words hidden inside rasterized images by an opaque overlay (the rasterized analogue of a black-box redaction).

See [`docs/AUTOMATION_API.md`](AUTOMATION_API.md) for the supported
automation contract, exit codes, batch workflow schema, security boundary, and
platform examples. The public automation path is CLI-first; Release builds do
not enable a background GUI automation listener.


## CLI examples

```bash
# Render page 1 of a PDF at 200 DPI
excise render report.pdf -o report-p1.png --page 1 --dpi 200

# Glyph-level redact a phrase
excise redact report.pdf report-redacted.pdf "ACCOUNT 9876"

# Write a smaller copy for email: downsample images above 188 dpi to 150 dpi
excise optimize scan.pdf scan-small.pdf --preset standard

# Audit a "redacted" PDF for hidden text leftovers — both structural and rasterized
excise audit purportedly-redacted.pdf --deep --json

# Fill an AcroForm and flatten so the result is no longer interactive
excise fill-form blank-w9.pdf w9-filled.pdf --field Name=Acme --field EIN=12-3456789 --flatten

# Add a text field to an existing PDF
excise add-field invoice.pdf invoice-with-form.pdf \
  --type Text --name CustomerNote --page 1 --rect "72,200,540,260"

# Auto-detect form fields on a Word-exported PDF and apply them
excise autodetect-fields exported-from-word.pdf form-ready.pdf --apply

# Audit somebody else's redaction: what did each black box actually keep hidden?
excise unredact purportedly-redacted.pdf --json

# ...including the deferred image channels and the OCR differential
excise unredact purportedly-redacted.pdf --include-deferred --ocr

# Extract text from a scanned PDF (requires system tesseract)
excise ocr scan.pdf
```

## De-redaction audit (`excise unredact`)

Points the redaction engine backwards: given a PDF somebody says is redacted, it
finds the redaction **marks** (black boxes, `/Redact` annotations, emptied
regions) and reports, per mark, how much of what they cover came back.

The marks are the denominator, and that is the point. "12 findings" says nothing
about whether a document is safe; "3 of 11 marks recovered, 8 held" does.

```bash
excise unredact purportedly-redacted.pdf                # what survived, per mark
excise unredact filing.pdf --json                       # machine-readable
excise unredact filing.pdf --mode residue --dictionary names.txt
excise unredact filing.pdf --restore reconstructed.pdf  # draw it back in place
```

Exit status is scriptable, and keys on **what kind of evidence** turned up
**under a mark** — not on whether it happened to be text:

| code | meaning |
|---:|---|
| **0** | nothing survives under any redaction mark |
| **3** | verbatim text was recovered |
| **4** | the value under a mark is constrained (width-residue candidates, OCR) |
| **5** | material survives under a mark and nothing decoded it — image pixels, a vector drawing, an opaque attachment |
| **6** | *reserved* — page text held by a carrier with no mark breached (not yet emitted; see #1703) |
| 1 / 2 | the run failed: 1 I/O or unhandled error, 2 usage or a missing dependency |

`--fail-on any|text|constrained|present` picks the threshold: `any` is the
default, and `--fail-on text` restores the older, narrower behaviour of failing
only on a recovered string.

⚠️ Present-only material that **no mark covers** — a leftover page thumbnail, an
attachment elsewhere in the file — stays **0**. It is reported, but it is
furniture, not a breach, and failing a pipeline over it would make the status
useless. Before #1707 that reasoning was applied to *every* present-only
finding, so an intact image under an opaque black box — the commonest
viewer-based "redaction" — was reported on screen and then graded clean with
exit 0.

⚠️ **The reconstruction `--restore` writes CONTAINS the recovered text by
design.** It is watermarked, and it refuses to overwrite the input. Handle it as
you would the unredacted original.

**It focuses on TEXT recovery** (#1690). A recovered string either matches or it
does not, and mutool and pdftotext can confirm it independently; nothing else
here can be graded that honestly. Two channels are therefore **deferred — still
implemented, still tested, opt-in, and not counted in the score**:

| deferred channel | flag | why |
|---|---|---|
| raster content under a mark, and orphaned or fully masked image originals | `--include-deferred` | reported present-only, never as a value — there is no exact answer to grade a recovery against |
| the OCR differential | `--ocr` | an OCR reading carries its own error rate, so no independent extractor can confirm it the way one confirms a string read from the file |

⚠️ **What a default run does not cover, stated narrowly:** a page whose redaction
box covers **pixels, with no surviving text layer beneath them**. That document
class is not in the score, and a run that skipped a deferred channel prints so
next to its own result rather than in a footnote. It is *not* "blind to scanned
documents" — a scanned page whose invisible OCR text layer survives under the box
**is** recovered, by the ordinary hidden-text channel.

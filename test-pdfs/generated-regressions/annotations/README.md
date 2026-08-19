# Annotation appearance-synthesis fixtures

Two hand-built PDFs whose annotations carry **no `/AP`**, so a viewer has to
synthesise the appearance itself. That is the code path RC8/RC9 built, and the
one where excise and the reference renderers can legitimately differ — which is
exactly why these need pinning rather than describing.

| file | what it probes |
|---|---|
| `visible-annotation-demo.pdf` | one of each visible subtype: Highlight, Underline, StrikeOut, Squiggly, Square, Circle, Line, Ink, Polygon, PolyLine, Text, FreeText, Link |
| `annotation-property-probe.pdf` | `/Name` icon selection (7 values), `/CA` opacity (1.0 / 0.5 / 0.2), `/BS` solid vs dashed, Highlight with no `/C` |

The `.py` generators are checked in beside the PDFs so a fixture can be
regenerated or extended without reverse-engineering the bytes.

## Defects these found (2026-08-19)

Each confirmed by an oracle MAJORITY — mutool and pdftocairo, never excise
refereeing excise:

- **#1069** Polygon: path not closed, `/IC` fill ignored
- **#1070** FreeText: `/Contents` text and border not drawn, background only
- **#1071** `/Name` ignored — all seven Text icons draw the same glyph
- **#1072** `/CA` opacity ignored — everything paints fully opaque
- **#1073** `/BS` dash ignored, and the border is centred on `/Rect` rather than
  inset by half its width (§12.5.6.8)

NOT defects, recorded so they are not re-triaged:

- **Link border** — excise and pdftocairo draw it, mutool and Ghostscript do
  not. A 2-2 split is viewer policy, not a bug.
- **Highlight with no `/C`** — both oracles paint BLACK; excise paints yellow,
  deliberately. A black highlighter hides the text it is meant to emphasise.
  This is a product decision, not a divergence to fix.
- **Highlight end-cap radius** — excise's rounded cap is marginally tighter than
  the oracles'. Skia-origin, out of scope per the rasterisation register.

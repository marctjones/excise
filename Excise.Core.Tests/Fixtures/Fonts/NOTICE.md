# Font fixtures — attribution

## Inconsolata.cff

The raw `CFF ` table extracted (unmodified) from **Inconsolata** by Raph Levien,
used here solely as a test fixture for the CFF parser/subsetter.

> Created by Raph Levien using his own tools and FontForge.
> Copyright 2006 Raph Levien.
> Released under the SIL Open Font License, http://scripts.sil.org/OFL

Inconsolata is licensed under the **SIL Open Font License, Version 1.1**, which
permits bundling and redistribution. "Inconsolata" is a Reserved Font Name; this
fixture is the original font data and is not a modified derivative.

## DejaVuSans.ttf

**DejaVu Sans** (Regular), used as a test fixture for the TrueType/sfnt reader
(#378, #351), the embedded-Unicode-font authoring path (`PdfTrueTypeFont`,
`TrueTypeSubsetter`), and font-parser fuzz coverage (#648). Unmodified,
obtained via `brew install --cask font-dejavu`.

> Fonts are (c) Bitstream (see below). DejaVu changes are in public domain.
>
> Bitstream Vera Fonts Copyright: Copyright (c) 2003 by Bitstream, Inc. All
> Rights Reserved. Bitstream Vera is a trademark of Bitstream, Inc.

Licensed under the **Bitstream Vera License** (with the DejaVu public-domain
modifications), which explicitly permits embedding fonts in documents and
redistributing them as part of software — see
https://dejavu-fonts.github.io/License.html. This is why CI installed it
system-wide (`fonts-dejavu-core`) in the first place; bundling it here as a
fixture removes the dependency on a Linux-only system font path that made
these tests silently skip on macOS and Windows dev machines (discovered
while restoring the coverage gate, #603).

## SymbolDejaVu30.ttf

A **derivative** of DejaVu Sans (above), generated as a test fixture for the
symbolic-TrueType `(3,0)` "Microsoft Symbol" cmap glyph-selection path
(ISO 32000-1 §9.6.6.4, epic #512). Built by subsetting DejaVu Sans to the
glyphs `A`–`F` and replacing its cmap with a single platform-`(3,0)` format-4
subtable that maps the classic PUA symbol range `0xF041`–`0xF046` to those
glyph outlines (via `fontTools`). No other checked-in fixture carries a
`(3,0)` subtable, so this exercises the required `0xF000`-offset lookup for
simple symbolic fonts.

Because the DejaVu modifications are released into the **public domain** (see
above) and the underlying Bitstream Vera license permits modification,
embedding, and redistribution, this derivative may be bundled and
redistributed. It is a modified derivative and is not the original DejaVu font
data.

## LibertinusSerif-Regular.otf

**Libertinus Serif** (Regular), a CFF-flavored OpenType (`OTTO`) font — used
as a test fixture for the CFF-OpenType embedding path (`TrueTypeFontFile.IsCff`,
`PdfTrueTypeFont`'s `FontFile3`/CIDFontType0 branch, #603, #648). The Linux
Libertine successor project; unmodified, obtained via
`brew install --cask font-libertinus`.

> Copyright (c) 2003-2024 Philipp H. Poll, and others (see full font metadata
> for the complete contributor list). Released under the SIL Open Font
> License, Version 1.1, http://scripts.sil.org/OFL

Licensed under the **SIL Open Font License, Version 1.1**, which permits
bundling and redistribution. This fixture is the original font data and is
not a modified derivative. Previously this coverage was tested only against
a hard-coded Linux system font path
(`/usr/share/fonts/opentype/linux-libertine/LinLibertine_RB.otf` or
Cantarell), so it silently skipped on macOS and Windows dev machines —
bundling removes that dependency the same way DejaVuSans.ttf does above.

# excise Vision

## Purpose
A cross-platform PDF editor focused on **true content-level redaction** for privacy and security.

## Core Principles

1. **Security-First Redaction** - Remove text/graphics from PDF structure, not just visual covering. Text extraction tools cannot recover redacted content.

2. **Cross-Platform** - Runs on Windows, Linux, and macOS via .NET 10 + Avalonia 12 (UI).

3. **Permissive Licensing** - All dependencies use MIT, Apache 2.0, or BSD-3 licenses. No copyleft restrictions.

4. **Self-Contained** - As of v2.0, zero external *PDF* library dependencies: Excise.Core (parsing/writing) and Excise.Rendering (SkiaSharp-based rendering) replace PdfPig/PDFsharp/PDFtoImage. SkiaSharp (rasterization) and BouncyCastle (crypto) remain as permissively-licensed support libraries.

## v2.0 Goals

- **Excise.Core**: Pure .NET PDF parser/writer supporting PDF 1.4-2.0
- **Excise.Rendering**: SkiaSharp-based renderer replacing PDFium dependency
- **Glyph-Level Redaction**: Surgical removal of individual characters
- **veraPDF Compliance**: Pass validation on all saved files

## Success Metrics

- 600+ tests passing (598 currently)
- veraPDF corpus compatibility (2,694 test files)
- Visual rendering 90%+ match with PDFium
- Real-world document support (birth certificates, government forms)

## Non-Goals (v2.0)

- PDF form filling/creation
- Digital signature creation (verification only)
- Multimedia features (sounds, movies, 3D)
- Full accessibility/tagged PDF support
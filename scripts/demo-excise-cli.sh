#!/bin/bash
# Demo script for Excise.CLI v2 capabilities
# Run from: /home/marc/Projects/excise/
# Usage: ./scripts/demo-excise-cli.sh

set -e
cd /home/marc/Projects/excise

echo "=========================================="
echo "EXCISE.CLI v2 DEMO"
echo "=========================================="

# Test PDFs
SIMPLE_PDF="/home/marc/Projects/excise/test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-1b/6.1 File structure/6.1.2 File header/veraPDF test suite 6-1-2-t02-pass-a.pdf"
BIRTH_CERT="/home/marc/Projects/excise/test-pdfs/sample-pdfs/birth-certificate-request-scrambled.pdf"
GRAPHICS_PDF="/home/marc/Projects/excise/test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_UA-1/7.3 Graphics/7.3-t01-pass-c.pdf"
CJK_PDF="/home/marc/Projects/excise/test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_UA-1/7.21 Fonts/7.21.3 Composite fonts/7.21.3.2 CIDFonts/7.21.3.2-t01-pass-a.pdf"

pause() {
    echo ""
    echo "Press Enter to continue..."
    read -r
}

# 1. INFO COMMAND
echo ""
echo "=========================================="
echo "1. PDF INFO - Document metadata"
echo "=========================================="
echo "Command: dotnet run --project Excise.Cli -- info \"$SIMPLE_PDF\""
echo ""
dotnet run --project Excise.Cli -- info "$SIMPLE_PDF"
pause

# 2. TEXT EXTRACTION
echo ""
echo "=========================================="
echo "2. TEXT EXTRACTION - Simple PDF"
echo "=========================================="
echo "Command: dotnet run --project Excise.Cli -- text \"$SIMPLE_PDF\""
echo ""
dotnet run --project Excise.Cli -- text "$SIMPLE_PDF"
pause

# 3. LETTER POSITIONS
echo ""
echo "=========================================="
echo "3. LETTER POSITIONS - Glyph-level data"
echo "=========================================="
echo "Command: dotnet run --project Excise.Cli -- letters \"$SIMPLE_PDF\""
echo ""
dotnet run --project Excise.Cli -- letters "$SIMPLE_PDF"
pause

# 4. RENDER TO PNG
echo ""
echo "=========================================="
echo "4. RENDER TO PNG - SkiaSharp renderer"
echo "=========================================="
echo "Command: dotnet run --project Excise.Cli -- render \"$SIMPLE_PDF\" -o /tmp/excise_demo_page.png"
echo ""
dotnet run --project Excise.Cli -- render "$SIMPLE_PDF" -o /tmp/excise_demo_page.png
echo ""
echo "Output: /tmp/excise_demo_page.png"
ls -la /tmp/excise_demo_page.png
pause

# 5. DRAW SHAPES
echo ""
echo "=========================================="
echo "5. DRAW SHAPES - Graphics API demo"
echo "=========================================="
echo "Command: dotnet run --project tools/Excise.RenderTools -- draw \"$SIMPLE_PDF\" -o /tmp/excise_demo_shapes.pdf"
echo ""
dotnet run --project tools/Excise.RenderTools -- draw "$SIMPLE_PDF" -o /tmp/excise_demo_shapes.pdf
echo ""
echo "Output: /tmp/excise_demo_shapes.pdf"
ls -la /tmp/excise_demo_shapes.pdf
pause

# 6. REAL-WORLD PDF
echo ""
echo "=========================================="
echo "6. REAL-WORLD PDF - Birth Certificate Form"
echo "=========================================="
echo "Command: dotnet run --project Excise.Cli -- info \"$BIRTH_CERT\""
echo ""
dotnet run --project Excise.Cli -- info "$BIRTH_CERT"
echo ""
echo "Extracting text (truncated)..."
dotnet run --project Excise.Cli -- text "$BIRTH_CERT" | head -20
echo "..."
pause

# 7. GRAPHICS PDF
echo ""
echo "=========================================="
echo "7. GRAPHICS PDF - Complex paths/shapes"
echo "=========================================="
echo "Command: dotnet run --project Excise.Cli -- render \"$GRAPHICS_PDF\" -o /tmp/excise_demo_graphics.png"
echo ""
dotnet run --project Excise.Cli -- info "$GRAPHICS_PDF"
echo ""
dotnet run --project Excise.Cli -- render "$GRAPHICS_PDF" -o /tmp/excise_demo_graphics.png
echo ""
echo "Output: /tmp/excise_demo_graphics.png"
ls -la /tmp/excise_demo_graphics.png
pause

# 8. CJK/CID FONTS (partial support)
echo ""
echo "=========================================="
echo "8. CJK/CID FONTS - Partial support (issue #281)"
echo "=========================================="
echo "Command: dotnet run --project Excise.Cli -- text \"$CJK_PDF\""
echo ""
echo "Note: CJK text extraction may show empty/garbled output"
dotnet run --project Excise.Cli -- text "$CJK_PDF"
echo "(Empty output expected - CJK support is incomplete)"
pause

# 9. INTERACTIVE DEMOS
echo ""
echo "=========================================="
echo "9. INTERACTIVE DEMOS"
echo "=========================================="
echo "Command: dotnet run --project tools/Excise.RenderTools -- demo"
echo ""
echo "This will run interactive demos. Press Ctrl+C to exit."
pause
dotnet run --project tools/Excise.RenderTools -- demo

echo ""
echo "=========================================="
echo "DEMO COMPLETE"
echo "=========================================="
echo ""
echo "Output files created:"
echo "  - /tmp/excise_demo_page.png"
echo "  - /tmp/excise_demo_shapes.pdf"
echo "  - /tmp/excise_demo_graphics.png"
echo ""
echo "To run the GUI demo app:"
echo "  dotnet run --project Excise.Demo"

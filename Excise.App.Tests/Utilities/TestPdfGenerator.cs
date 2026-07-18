using System;
using System.IO;
using Excise.Core.Document;
using Excise.Core.Graphics;

namespace Excise.App.Tests.Utilities;

/// <summary>
/// Generates small PDF fixtures for tests.
/// </summary>
/// <remarks>
/// Thin wrapper over <c>PdfDocument.CreateNew()</c> +
/// <c>page.GetGraphics().DrawString()</c>. Method signatures match the
/// older helper so existing tests needed no changes.
/// </remarks>
public static class TestPdfGenerator
{
    /// <summary>
    /// Single-page PDF with <paramref name="text"/> drawn at (100, 100)
    /// in top-left-origin (Avalonia) coordinates.
    /// </summary>
    public static string CreateSimpleTextPdf(string outputPath, string text = "Test Content")
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        DrawAt(page, text, x: 100, pdfY: page.Height - 100);
        doc.Save(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Multi-page PDF — N pages, <paramref name="pageCount"/> of them.
    /// Each page carries "Page N Content" plus "Secret on Page N".
    /// Used by tests that search/redact text repeated across pages.
    /// </summary>
    public static string CreateSimpleTextPdf(string outputPath, int pageCount)
        => CreateMultiPagePdf(outputPath, pageCount);

    /// <summary>
    /// Single-page PDF drawing the given line at PDF-top position (100,100).
    /// </summary>
    public static string CreateTextOnlyPdf(string outputPath, string text)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        DrawAt(page, text, x: 100, pdfY: page.Height - 100);
        doc.Save(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Single-page PDF with multiple lines (vertically stacked, 20-point
    /// spacing, starting near the top of the page).
    /// </summary>
    public static string CreateTextOnlyPdf(string outputPath, string[] lines)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        double y = page.Height - 100;
        foreach (var line in lines)
        {
            DrawAt(page, line, x: 100, pdfY: y);
            y -= 20;
        }
        doc.Save(outputPath);
        return outputPath;
    }

    /// <summary>
    /// N-page PDF, every page carries a "Page N Content" line and a
    /// "Secret on Page N" line.
    /// </summary>
    public static string CreateMultiPagePdf(string outputPath, int pageCount = 3)
    {
        using var doc = PdfDocument.CreateNew();
        for (int i = 0; i < pageCount; i++)
        {
            var page = doc.Pages.AddBlank();
            DrawAt(page, $"Page {i + 1} Content", x: 100, pdfY: page.Height - 100);
            DrawAt(page, $"Secret on Page {i + 1}", x: 100, pdfY: page.Height - 200);
        }
        doc.Save(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Single-page PDF with text at an exact position.
    /// <paramref name="x"/> is measured from the left, <paramref name="y"/>
    /// from the PDF BOTTOM-left origin (PDF native convention).
    /// </summary>
    public static string CreatePdfWithTextAt(string outputPath, string text, double x, double y, double fontSize = 12)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        DrawAt(page, text, x, pdfY: y, fontSize: fontSize);
        doc.Save(outputPath);
        return outputPath;
    }

    /// <summary>Bytes-returning variant for in-memory tests.</summary>
    public static byte[] CreateSimplePdf(string text = "Test Content")
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        DrawAt(page, text, x: 100, pdfY: page.Height - 100);
        return doc.SaveToBytes();
    }

    /// <summary>Bytes-returning variant at a specific position.</summary>
    public static byte[] CreatePdfWithTextAtPosition(string text, double x, double y, double fontSize = 12)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        DrawAt(page, text, x, pdfY: y, fontSize: fontSize);
        return doc.SaveToBytes();
    }

    /// <summary>
    /// Page with non-Letter dimensions (for unusual-page-size tests).
    /// </summary>
    public static string CreateCustomSizePdf(string outputPath, double widthPoints, double heightPoints, string text = "Test Document")
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(widthPoints, heightPoints);
        DrawAt(page, text, x: widthPoints / 4, pdfY: heightPoints / 2);
        doc.Save(outputPath);
        return outputPath;
    }

    /// <summary>Delete a file if it exists; swallow errors.</summary>
    public static void CleanupTestFile(string filePath)
    {
        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
    }

    private static void DrawAt(PdfPage page, string text, double x, double pdfY, double fontSize = 12)
    {
        using var g = page.GetGraphics();
        g.DrawString(text, PdfFont.Helvetica(fontSize), PdfBrush.Black, x, pdfY);
        g.Flush();
    }
}

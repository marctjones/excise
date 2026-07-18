using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Rendering;
using SkiaSharp;

namespace Excise.Demo;

public partial class MainWindow : Window
{
    private PdfDocument? _document;
    private int _currentPage = 1;
    private int _dpi = 150;
    private readonly SkiaRenderer _renderer = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF Files") { Patterns = new[] { "*.pdf" } }
            }
        });

        if (files.Count > 0)
        {
            var file = files[0];
            try
            {
                // Dispose previous document
                _document?.Dispose();

                // Open new document
                var path = file.Path.LocalPath;
                _document = PdfDocument.Open(path);
                _currentPage = 1;

                FileLabel.Text = Path.GetFileName(path);
                UpdateNavigation();
                RenderCurrentPage();
            }
            catch (Exception ex)
            {
                FileLabel.Text = $"Error: {ex.Message}";
            }
        }
    }

    private void PrevPage_Click(object? sender, RoutedEventArgs e)
    {
        if (_document != null && _currentPage > 1)
        {
            _currentPage--;
            UpdateNavigation();
            RenderCurrentPage();
        }
    }

    private void NextPage_Click(object? sender, RoutedEventArgs e)
    {
        if (_document != null && _currentPage < _document.PageCount)
        {
            _currentPage++;
            UpdateNavigation();
            RenderCurrentPage();
        }
    }

    private void DpiComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var item = DpiComboBox.SelectedItem as ComboBoxItem;
        if (item?.Content is string dpiStr && int.TryParse(dpiStr, out var dpi))
        {
            _dpi = dpi;
            if (_document != null)
            {
                RenderCurrentPage();
            }
        }
    }

    private void DemoButton_Click(object? sender, RoutedEventArgs e)
    {
        RunDemo();
    }

    private void UpdateNavigation()
    {
        if (_document != null)
        {
            PageLabel.Text = $"Page {_currentPage} of {_document.PageCount}";
            PrevPageButton.IsEnabled = _currentPage > 1;
            NextPageButton.IsEnabled = _currentPage < _document.PageCount;
        }
        else
        {
            PageLabel.Text = "No document";
            PrevPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
        }
    }

    private void RenderCurrentPage()
    {
        if (_document == null) return;

        try
        {
            var page = _document.GetPage(_currentPage);
            var options = new RenderOptions { Dpi = _dpi };

            var sw = Stopwatch.StartNew();
            using var skBitmap = _renderer.RenderPage(page, options);
            sw.Stop();

            // Convert SKBitmap to Avalonia Bitmap
            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(data.ToArray());
            var bitmap = new Bitmap(stream);

            PageImage.Source = bitmap;
            SizeLabel.Text = $"{skBitmap.Width} x {skBitmap.Height} px";
            RenderTimeLabel.Text = $"Rendered in {sw.ElapsedMilliseconds}ms";
        }
        catch (Exception ex)
        {
            FileLabel.Text = $"Render error: {ex.Message}";
        }
    }

    private void RunDemo()
    {
        try
        {
            // Create a demo PDF in memory
            var pdfBytes = CreateDemoPdf();

            // Open and display it
            _document?.Dispose();
            _document = PdfDocument.Open(pdfBytes);
            _currentPage = 1;

            FileLabel.Text = "Demo PDF (created in memory)";
            UpdateNavigation();
            RenderCurrentPage();
        }
        catch (Exception ex)
        {
            FileLabel.Text = $"Demo error: {ex.Message}";
        }
    }

    private byte[] CreateDemoPdf()
    {
        // Create PDF with various shapes to demonstrate rendering
        var content = new StringBuilder();

        // Black text
        content.AppendLine("BT /F1 24 Tf 72 720 Td (Excise.Core Rendering Demo) Tj ET");
        content.AppendLine("BT /F1 12 Tf 72 690 Td (This PDF was created and rendered using Excise.Core) Tj ET");

        // Red filled rectangle
        content.AppendLine("q");
        content.AppendLine("1 0 0 rg");  // Red fill
        content.AppendLine("72 600 150 50 re f");  // Rectangle at (72, 600) size 150x50
        content.AppendLine("Q");

        // Green filled rectangle
        content.AppendLine("q");
        content.AppendLine("0 0.7 0 rg");  // Green fill
        content.AppendLine("250 600 150 50 re f");
        content.AppendLine("Q");

        // Blue filled rectangle
        content.AppendLine("q");
        content.AppendLine("0 0 1 rg");  // Blue fill
        content.AppendLine("428 600 150 50 re f");
        content.AppendLine("Q");

        // Stroked rectangle (outline only)
        content.AppendLine("q");
        content.AppendLine("0 G 2 w");  // Black stroke, 2pt width
        content.AppendLine("72 500 200 80 re S");
        content.AppendLine("Q");

        // Gray filled rectangle
        content.AppendLine("q");
        content.AppendLine("0.5 g");  // 50% gray
        content.AppendLine("300 500 200 80 re f");
        content.AppendLine("Q");

        // Diagonal line
        content.AppendLine("q");
        content.AppendLine("1 0 0 RG 3 w");  // Red stroke, 3pt width
        content.AppendLine("72 450 m 540 450 l S");  // Horizontal line
        content.AppendLine("Q");

        // Triangle (path)
        content.AppendLine("q");
        content.AppendLine("0.8 0.4 0 rg");  // Orange fill
        content.AppendLine("150 350 m 250 350 l 200 420 l h f");  // Triangle
        content.AppendLine("Q");

        // Circle approximation (using bezier curves)
        content.AppendLine("q");
        content.AppendLine("0.5 0 0.5 rg");  // Purple fill
        // Approximate circle at (400, 380) radius 40
        content.AppendLine("440 380 m");  // Start at right
        content.AppendLine("440 402 422 420 400 420 c");  // Top right curve
        content.AppendLine("378 420 360 402 360 380 c");  // Top left curve
        content.AppendLine("360 358 378 340 400 340 c");  // Bottom left curve
        content.AppendLine("422 340 440 358 440 380 c");  // Bottom right curve
        content.AppendLine("f");
        content.AppendLine("Q");

        // Labels
        content.AppendLine("BT /F1 10 Tf 100 570 Td (Red) Tj ET");
        content.AppendLine("BT /F1 10 Tf 295 570 Td (Green) Tj ET");
        content.AppendLine("BT /F1 10 Tf 480 570 Td (Blue) Tj ET");
        content.AppendLine("BT /F1 10 Tf 130 470 Td (Stroked) Tj ET");
        content.AppendLine("BT /F1 10 Tf 365 470 Td (Gray Fill) Tj ET");
        content.AppendLine("BT /F1 10 Tf 170 320 Td (Triangle) Tj ET");
        content.AppendLine("BT /F1 10 Tf 375 320 Td (Circle) Tj ET");

        // Features list
        content.AppendLine("BT /F1 14 Tf 72 250 Td (Rendering Features:) Tj ET");
        content.AppendLine("BT /F1 11 Tf 72 230 Td (- RGB and Grayscale colors) Tj ET");
        content.AppendLine("BT /F1 11 Tf 72 215 Td (- Filled and stroked shapes) Tj ET");
        content.AppendLine("BT /F1 11 Tf 72 200 Td (- Lines with variable width) Tj ET");
        content.AppendLine("BT /F1 11 Tf 72 185 Td (- Bezier curves) Tj ET");
        content.AppendLine("BT /F1 11 Tf 72 170 Td (- Graphics state save/restore) Tj ET");
        content.AppendLine("BT /F1 11 Tf 72 155 Td (- Transformation matrices) Tj ET");

        var contentStr = content.ToString();

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentStr.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentStr);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    protected override void OnClosed(EventArgs e)
    {
        _document?.Dispose();
        base.OnClosed(e);
    }
}

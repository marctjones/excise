using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;

namespace Excise.Demo;

public partial class SpecCoverageWindow : Window
{
    private const string WikiBaseUrl = "https://github.com/marctjones/excise/wiki";
    private const string PdfSpecBaseUrl = "https://pdfa.org/resource/pdf-specification-index/"; // PDF 2.0 spec reference

    private int _dpi = 150;
    private readonly SkiaRenderer _renderer = new();
    private readonly List<FeatureInfo> _features = new();

    // VS Code-inspired color palette
    private static readonly IBrush ImplementedColor = new SolidColorBrush(Color.Parse("#4EC9B0"));  // Teal
    private static readonly IBrush InProgressColor = new SolidColorBrush(Color.Parse("#DCDCAA"));   // Yellow
    private static readonly IBrush NotStartedColor = new SolidColorBrush(Color.Parse("#6E6E6E"));   // Gray
    private static readonly IBrush LinkColor = new SolidColorBrush(Color.Parse("#569CD6"));         // Blue link
    private static readonly IBrush TextLight = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#808080"));

    public SpecCoverageWindow()
    {
        InitializeComponent();
        InitializeFeatures();
        PopulateSidebar();
        RenderDemo();
    }

    private void InitializeFeatures()
    {
        // Graphics State - PDF 2.0 Spec Section 8.4
        _features.Add(new FeatureInfo("q", "Save graphics state", FeatureCategory.GraphicsState, FeatureStatus.Implemented,
            specSection: "8.4.2", wikiPage: "Graphics-State", specTable: "Table 56"));
        _features.Add(new FeatureInfo("Q", "Restore graphics state", FeatureCategory.GraphicsState, FeatureStatus.Implemented,
            specSection: "8.4.2", wikiPage: "Graphics-State", specTable: "Table 56"));
        _features.Add(new FeatureInfo("cm", "Concat transformation matrix", FeatureCategory.GraphicsState, FeatureStatus.Implemented,
            specSection: "8.4.4", wikiPage: "Graphics-State#transformations", specTable: "Table 56"));
        _features.Add(new FeatureInfo("w", "Set line width", FeatureCategory.GraphicsState, FeatureStatus.Implemented,
            specSection: "8.4.3.2", wikiPage: "Graphics-State#line-width", specTable: "Table 56"));
        _features.Add(new FeatureInfo("J", "Set line cap style", FeatureCategory.GraphicsState, FeatureStatus.NotStarted,
            specSection: "8.4.3.3", wikiPage: "Graphics-State#line-cap", specTable: "Table 56"));
        _features.Add(new FeatureInfo("j", "Set line join style", FeatureCategory.GraphicsState, FeatureStatus.NotStarted,
            specSection: "8.4.3.4", wikiPage: "Graphics-State#line-join", specTable: "Table 56"));
        _features.Add(new FeatureInfo("M", "Set miter limit", FeatureCategory.GraphicsState, FeatureStatus.NotStarted,
            specSection: "8.4.3.5", wikiPage: "Graphics-State#miter-limit", specTable: "Table 56"));
        _features.Add(new FeatureInfo("d", "Set dash pattern", FeatureCategory.GraphicsState, FeatureStatus.NotStarted,
            specSection: "8.4.3.6", wikiPage: "Graphics-State#dash-pattern", specTable: "Table 56"));
        _features.Add(new FeatureInfo("ri", "Set rendering intent", FeatureCategory.GraphicsState, FeatureStatus.NotStarted,
            specSection: "8.4.5.5", wikiPage: "Graphics-State#rendering-intent", specTable: "Table 56"));
        _features.Add(new FeatureInfo("i", "Set flatness tolerance", FeatureCategory.GraphicsState, FeatureStatus.NotStarted,
            specSection: "8.4.3.8", wikiPage: "Graphics-State#flatness", specTable: "Table 56"));
        _features.Add(new FeatureInfo("gs", "Set from ExtGState dict", FeatureCategory.GraphicsState, FeatureStatus.NotStarted,
            specSection: "8.4.5", wikiPage: "Graphics-State#extgstate", specTable: "Table 56"));

        // Path Construction - PDF 2.0 Spec Section 8.5.2
        _features.Add(new FeatureInfo("m", "Move to", FeatureCategory.PathConstruction, FeatureStatus.Implemented,
            specSection: "8.5.2.1", wikiPage: "Path-Construction", specTable: "Table 58"));
        _features.Add(new FeatureInfo("l", "Line to", FeatureCategory.PathConstruction, FeatureStatus.Implemented,
            specSection: "8.5.2.1", wikiPage: "Path-Construction", specTable: "Table 58"));
        _features.Add(new FeatureInfo("c", "Cubic Bezier curve", FeatureCategory.PathConstruction, FeatureStatus.Implemented,
            specSection: "8.5.2.2", wikiPage: "Path-Construction#bezier-curves", specTable: "Table 58"));
        _features.Add(new FeatureInfo("v", "Bezier (initial pt = current)", FeatureCategory.PathConstruction, FeatureStatus.Implemented,
            specSection: "8.5.2.2", wikiPage: "Path-Construction#bezier-curves", specTable: "Table 58"));
        _features.Add(new FeatureInfo("y", "Bezier (final pt = control)", FeatureCategory.PathConstruction, FeatureStatus.Implemented,
            specSection: "8.5.2.2", wikiPage: "Path-Construction#bezier-curves", specTable: "Table 58"));
        _features.Add(new FeatureInfo("h", "Close subpath", FeatureCategory.PathConstruction, FeatureStatus.Implemented,
            specSection: "8.5.2.1", wikiPage: "Path-Construction#close-path", specTable: "Table 58"));
        _features.Add(new FeatureInfo("re", "Rectangle", FeatureCategory.PathConstruction, FeatureStatus.Implemented,
            specSection: "8.5.2.1", wikiPage: "Path-Construction#rectangle", specTable: "Table 58"));

        // Path Painting - PDF 2.0 Spec Section 8.5.3
        _features.Add(new FeatureInfo("S", "Stroke path", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#stroke", specTable: "Table 59"));
        _features.Add(new FeatureInfo("s", "Close and stroke", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#stroke", specTable: "Table 59"));
        _features.Add(new FeatureInfo("f", "Fill (nonzero winding)", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#fill", specTable: "Table 59"));
        _features.Add(new FeatureInfo("F", "Fill (nonzero) - obsolete", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#fill", specTable: "Table 59"));
        _features.Add(new FeatureInfo("f*", "Fill (even-odd rule)", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#fill-even-odd", specTable: "Table 59"));
        _features.Add(new FeatureInfo("B", "Fill and stroke (nonzero)", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#fill-stroke", specTable: "Table 59"));
        _features.Add(new FeatureInfo("B*", "Fill and stroke (even-odd)", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#fill-stroke", specTable: "Table 59"));
        _features.Add(new FeatureInfo("b", "Close, fill, stroke", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#close-fill-stroke", specTable: "Table 59"));
        _features.Add(new FeatureInfo("b*", "Close, fill, stroke (e-o)", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#close-fill-stroke", specTable: "Table 59"));
        _features.Add(new FeatureInfo("n", "End path (no paint)", FeatureCategory.PathPainting, FeatureStatus.Implemented,
            specSection: "8.5.3.1", wikiPage: "Path-Painting#no-op", specTable: "Table 59"));

        // Color Operators - PDF 2.0 Spec Section 8.6
        _features.Add(new FeatureInfo("g", "Set gray fill", FeatureCategory.Color, FeatureStatus.Implemented,
            specSection: "8.6.8", wikiPage: "Color-Operators#devicegray", specTable: "Table 73"));
        _features.Add(new FeatureInfo("G", "Set gray stroke", FeatureCategory.Color, FeatureStatus.Implemented,
            specSection: "8.6.8", wikiPage: "Color-Operators#devicegray", specTable: "Table 73"));
        _features.Add(new FeatureInfo("rg", "Set RGB fill", FeatureCategory.Color, FeatureStatus.Implemented,
            specSection: "8.6.8", wikiPage: "Color-Operators#devicergb", specTable: "Table 73"));
        _features.Add(new FeatureInfo("RG", "Set RGB stroke", FeatureCategory.Color, FeatureStatus.Implemented,
            specSection: "8.6.8", wikiPage: "Color-Operators#devicergb", specTable: "Table 73"));
        _features.Add(new FeatureInfo("k", "Set CMYK fill", FeatureCategory.Color, FeatureStatus.NotStarted,
            specSection: "8.6.8", wikiPage: "Color-Operators#devicecmyk", specTable: "Table 73"));
        _features.Add(new FeatureInfo("K", "Set CMYK stroke", FeatureCategory.Color, FeatureStatus.NotStarted,
            specSection: "8.6.8", wikiPage: "Color-Operators#devicecmyk", specTable: "Table 73"));
        _features.Add(new FeatureInfo("cs", "Set fill color space", FeatureCategory.Color, FeatureStatus.NotStarted,
            specSection: "8.6.8", wikiPage: "Color-Operators#color-spaces", specTable: "Table 73"));
        _features.Add(new FeatureInfo("CS", "Set stroke color space", FeatureCategory.Color, FeatureStatus.NotStarted,
            specSection: "8.6.8", wikiPage: "Color-Operators#color-spaces", specTable: "Table 73"));
        _features.Add(new FeatureInfo("sc", "Set fill color", FeatureCategory.Color, FeatureStatus.NotStarted,
            specSection: "8.6.8", wikiPage: "Color-Operators#set-color", specTable: "Table 73"));
        _features.Add(new FeatureInfo("SC", "Set stroke color", FeatureCategory.Color, FeatureStatus.NotStarted,
            specSection: "8.6.8", wikiPage: "Color-Operators#set-color", specTable: "Table 73"));
        _features.Add(new FeatureInfo("scn", "Set fill color (pattern)", FeatureCategory.Color, FeatureStatus.NotStarted,
            specSection: "8.6.8", wikiPage: "Color-Operators#patterns", specTable: "Table 73"));
        _features.Add(new FeatureInfo("SCN", "Set stroke color (pattern)", FeatureCategory.Color, FeatureStatus.NotStarted,
            specSection: "8.6.8", wikiPage: "Color-Operators#patterns", specTable: "Table 73"));

        // Text Operators - PDF 2.0 Spec Section 9
        _features.Add(new FeatureInfo("BT", "Begin text object", FeatureCategory.Text, FeatureStatus.InProgress,
            specSection: "9.4.1", wikiPage: "Text-Operators#text-objects", specTable: "Table 105"));
        _features.Add(new FeatureInfo("ET", "End text object", FeatureCategory.Text, FeatureStatus.InProgress,
            specSection: "9.4.1", wikiPage: "Text-Operators#text-objects", specTable: "Table 105"));
        _features.Add(new FeatureInfo("Tf", "Set font and size", FeatureCategory.Text, FeatureStatus.InProgress,
            specSection: "9.3", wikiPage: "Text-Operators#font-selection", specTable: "Table 104"));
        _features.Add(new FeatureInfo("Td", "Move text position", FeatureCategory.Text, FeatureStatus.InProgress,
            specSection: "9.4.2", wikiPage: "Text-Operators#positioning", specTable: "Table 106"));
        _features.Add(new FeatureInfo("TD", "Move + set leading", FeatureCategory.Text, FeatureStatus.InProgress,
            specSection: "9.4.2", wikiPage: "Text-Operators#positioning", specTable: "Table 106"));
        _features.Add(new FeatureInfo("Tm", "Set text matrix", FeatureCategory.Text, FeatureStatus.InProgress,
            specSection: "9.4.2", wikiPage: "Text-Operators#text-matrix", specTable: "Table 106"));
        _features.Add(new FeatureInfo("T*", "Move to next line", FeatureCategory.Text, FeatureStatus.InProgress,
            specSection: "9.4.2", wikiPage: "Text-Operators#next-line", specTable: "Table 106"));
        _features.Add(new FeatureInfo("Tj", "Show text string", FeatureCategory.Text, FeatureStatus.InProgress,
            specSection: "9.4.3", wikiPage: "Text-Operators#show-text", specTable: "Table 107"));
        _features.Add(new FeatureInfo("TJ", "Show with positioning", FeatureCategory.Text, FeatureStatus.InProgress,
            specSection: "9.4.3", wikiPage: "Text-Operators#show-text-positioned", specTable: "Table 107"));
        _features.Add(new FeatureInfo("'", "Move + show text", FeatureCategory.Text, FeatureStatus.NotStarted,
            specSection: "9.4.3", wikiPage: "Text-Operators#convenience-operators", specTable: "Table 107"));
        _features.Add(new FeatureInfo("\"", "Set spacing + show", FeatureCategory.Text, FeatureStatus.NotStarted,
            specSection: "9.4.3", wikiPage: "Text-Operators#convenience-operators", specTable: "Table 107"));
        _features.Add(new FeatureInfo("Tc", "Set character spacing", FeatureCategory.Text, FeatureStatus.NotStarted,
            specSection: "9.3.2", wikiPage: "Text-Operators#character-spacing", specTable: "Table 104"));
        _features.Add(new FeatureInfo("Tw", "Set word spacing", FeatureCategory.Text, FeatureStatus.NotStarted,
            specSection: "9.3.3", wikiPage: "Text-Operators#word-spacing", specTable: "Table 104"));
        _features.Add(new FeatureInfo("Tz", "Set horizontal scaling", FeatureCategory.Text, FeatureStatus.NotStarted,
            specSection: "9.3.4", wikiPage: "Text-Operators#horizontal-scaling", specTable: "Table 104"));
        _features.Add(new FeatureInfo("TL", "Set leading", FeatureCategory.Text, FeatureStatus.NotStarted,
            specSection: "9.3.5", wikiPage: "Text-Operators#leading", specTable: "Table 104"));
        _features.Add(new FeatureInfo("Tr", "Set rendering mode", FeatureCategory.Text, FeatureStatus.NotStarted,
            specSection: "9.3.6", wikiPage: "Text-Operators#rendering-mode", specTable: "Table 104"));
        _features.Add(new FeatureInfo("Ts", "Set text rise", FeatureCategory.Text, FeatureStatus.NotStarted,
            specSection: "9.3.7", wikiPage: "Text-Operators#text-rise", specTable: "Table 104"));

        // XObjects & Images - PDF 2.0 Spec Section 8.8 & 8.9
        _features.Add(new FeatureInfo("Do", "Invoke XObject", FeatureCategory.XObject, FeatureStatus.NotStarted,
            specSection: "8.8", wikiPage: "XObjects", specTable: "Table 85"));
        _features.Add(new FeatureInfo("BI", "Begin inline image", FeatureCategory.XObject, FeatureStatus.NotStarted,
            specSection: "8.9.7", wikiPage: "XObjects#inline-images", specTable: "Table 91"));
        _features.Add(new FeatureInfo("ID", "Inline image data", FeatureCategory.XObject, FeatureStatus.NotStarted,
            specSection: "8.9.7", wikiPage: "XObjects#inline-images", specTable: "Table 91"));
        _features.Add(new FeatureInfo("EI", "End inline image", FeatureCategory.XObject, FeatureStatus.NotStarted,
            specSection: "8.9.7", wikiPage: "XObjects#inline-images", specTable: "Table 91"));

        // Clipping - PDF 2.0 Spec Section 8.5.4
        _features.Add(new FeatureInfo("W", "Clip (nonzero winding)", FeatureCategory.Clipping, FeatureStatus.NotStarted,
            specSection: "8.5.4", wikiPage: "Clipping-Paths", specTable: "Table 60"));
        _features.Add(new FeatureInfo("W*", "Clip (even-odd)", FeatureCategory.Clipping, FeatureStatus.NotStarted,
            specSection: "8.5.4", wikiPage: "Clipping-Paths#even-odd", specTable: "Table 60"));

        // Shading & Patterns - PDF 2.0 Spec Section 8.7
        _features.Add(new FeatureInfo("sh", "Paint shading", FeatureCategory.Shading, FeatureStatus.NotStarted,
            specSection: "8.7.4.3", wikiPage: "Shading-Patterns", specTable: "Table 76"));
        _features.Add(new FeatureInfo("Pattern", "Tiling/shading pattern", FeatureCategory.Shading, FeatureStatus.NotStarted,
            specSection: "8.7", wikiPage: "Shading-Patterns#tiling", specTable: "Table 74"));

        // Transparency - PDF 2.0 Spec Section 11
        _features.Add(new FeatureInfo("ca", "Set fill alpha", FeatureCategory.Transparency, FeatureStatus.NotStarted,
            specSection: "11.3.5.2", wikiPage: "Transparency#alpha", specTable: "Table 135"));
        _features.Add(new FeatureInfo("CA", "Set stroke alpha", FeatureCategory.Transparency, FeatureStatus.NotStarted,
            specSection: "11.3.5.2", wikiPage: "Transparency#alpha", specTable: "Table 135"));
        _features.Add(new FeatureInfo("BM", "Set blend mode", FeatureCategory.Transparency, FeatureStatus.NotStarted,
            specSection: "11.3.5.2", wikiPage: "Transparency#blend-modes", specTable: "Table 135"));
        _features.Add(new FeatureInfo("SMask", "Set soft mask", FeatureCategory.Transparency, FeatureStatus.NotStarted,
            specSection: "11.5", wikiPage: "Transparency#soft-masks", specTable: "Table 141"));
    }

    private void PopulateSidebar()
    {
        PopulateCategoryPanel(GraphicsStatePanel, FeatureCategory.GraphicsState, "8.4");
        PopulateCategoryPanel(PathConstructionPanel, FeatureCategory.PathConstruction, "8.5.2");
        PopulateCategoryPanel(PathPaintingPanel, FeatureCategory.PathPainting, "8.5.3");
        PopulateCategoryPanel(ColorOperatorsPanel, FeatureCategory.Color, "8.6");
        PopulateCategoryPanel(TextOperatorsPanel, FeatureCategory.Text, "9");
        PopulateCategoryPanel(XObjectPanel, FeatureCategory.XObject, "8.8-8.9");
        PopulateCategoryPanel(ClippingPanel, FeatureCategory.Clipping, "8.5.4");
        PopulateCategoryPanel(ShadingPanel, FeatureCategory.Shading, "8.7");
        PopulateCategoryPanel(TransparencyPanel, FeatureCategory.Transparency, "11");

        UpdateOverallProgress();
    }

    private void PopulateCategoryPanel(StackPanel panel, FeatureCategory category, string specSection)
    {
        var features = _features.Where(f => f.Category == category).ToList();
        foreach (var feature in features)
        {
            var item = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 3),
                Margin = new Thickness(0, 1)
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto")
            };

            // Status indicator
            var indicator = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(2),
                Background = feature.Status switch
                {
                    FeatureStatus.Implemented => ImplementedColor,
                    FeatureStatus.InProgress => InProgressColor,
                    _ => NotStartedColor
                },
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(indicator, 0);

            // Operator name (monospace, clickable)
            var opLabel = new TextBlock
            {
                Text = feature.Operator,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 11,
                Width = 36,
                Foreground = feature.Status == FeatureStatus.Implemented ? ImplementedColor :
                             feature.Status == FeatureStatus.InProgress ? InProgressColor : TextDim,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = feature
            };
            opLabel.PointerPressed += OnFeatureClicked;
            Grid.SetColumn(opLabel, 1);

            // Description (clickable)
            var descLabel = new TextBlock
            {
                Text = feature.Description,
                FontSize = 10,
                Foreground = feature.Status == FeatureStatus.NotStarted ? TextDim : TextLight,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = feature
            };
            descLabel.PointerPressed += OnFeatureClicked;
            Grid.SetColumn(descLabel, 2);

            // Spec section link
            var specLink = new TextBlock
            {
                Text = $"[{feature.SpecSection}]",
                FontSize = 9,
                Foreground = LinkColor,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = feature,
                TextDecorations = TextDecorations.Underline
            };
            specLink.PointerPressed += OnSpecLinkClicked;
            ToolTip.SetTip(specLink, $"PDF 2.0 Spec Section {feature.SpecSection}\n{feature.SpecTable}\nClick to open wiki page");
            Grid.SetColumn(specLink, 3);

            grid.Children.Add(indicator);
            grid.Children.Add(opLabel);
            grid.Children.Add(descLabel);
            grid.Children.Add(specLink);

            item.Child = grid;
            panel.Children.Add(item);
        }
    }

    private void OnFeatureClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is FeatureInfo feature)
        {
            OpenWikiPage(feature.WikiPage);
        }
    }

    private void OnSpecLinkClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is FeatureInfo feature)
        {
            OpenWikiPage(feature.WikiPage);
        }
    }

    private void OpenWikiPage(string wikiPage)
    {
        var url = $"{WikiBaseUrl}/{wikiPage}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open URL: {ex.Message}");
        }
    }

    private void UpdateOverallProgress()
    {
        var implemented = _features.Count(f => f.Status == FeatureStatus.Implemented);
        var inProgress = _features.Count(f => f.Status == FeatureStatus.InProgress);
        var notStarted = _features.Count(f => f.Status == FeatureStatus.NotStarted);
        var total = _features.Count;

        // Count implemented + half of in-progress for progress bar
        var progressValue = (implemented + inProgress * 0.5) / total * 100;

        OverallProgressLabel.Text = $"Overall: {implemented}/{total} ({(int)progressValue}%)";
        OverallProgressBar.Value = progressValue;

        ImplementedCount.Text = $"{implemented} done";
        InProgressCount.Text = $"{inProgress} active";
        NotStartedCount.Text = $"{notStarted} todo";
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        RenderDemo();
    }

    private void DpiComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Guard against events during initialization
        if (!IsInitialized || PageImage == null) return;

        var item = DpiComboBox.SelectedItem as ComboBoxItem;
        if (item?.Content is string dpiStr && int.TryParse(dpiStr, out var dpi))
        {
            _dpi = dpi;
            RenderDemo();
        }
    }

    private void RenderDemo()
    {
        try
        {
            var pdfBytes = CreateSpecDemoPdf();
            using var doc = PdfDocument.Open(pdfBytes);
            var page = doc.GetPage(1);
            var options = new Excise.Rendering.RenderOptions { Dpi = _dpi };

            var sw = Stopwatch.StartNew();
            using var skBitmap = _renderer.RenderPage(page, options);
            sw.Stop();

            // Convert to Avalonia Bitmap
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
            RenderTimeLabel.Text = $"Error: {ex.Message}";
        }
    }

    private byte[] CreateSpecDemoPdf()
    {
        var content = new StringBuilder();
        double y = 780;
        const double col1 = 50;
        const double col2 = 320;

        // Title
        content.AppendLine("BT /F1 18 Tf 50 800 Td (Excise.Rendering - Visual Test Page) Tj ET");

        // ===== LEFT COLUMN: IMPLEMENTED FEATURES =====
        y = 750;
        content.AppendLine($"BT /F1 14 Tf {col1} {y} Td (IMPLEMENTED) Tj ET");
        y -= 25;

        // Section: Graphics State (8.4)
        content.AppendLine($"BT /F1 11 Tf {col1} {y} Td (Graphics State \\(8.4\\): q/Q, cm, w) Tj ET");
        y -= 20;

        // Demo: Save/Restore + Transform
        content.AppendLine("q");
        content.AppendLine("0.31 0.78 0.69 rg");  // Teal (#4EC9B0)
        content.AppendLine($"{col1} {y - 30} 80 30 re f");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 9 Tf {col1 + 5} {y - 20} Td (q/Q save) Tj ET");

        content.AppendLine("q");
        content.AppendLine("0.31 0.78 0.69 rg");
        content.AppendLine($"1 0 0 1 {col1 + 90} {y - 30} cm");
        content.AppendLine("0.6 0 0 1 0 0 cm");  // Scale
        content.AppendLine("0 0 130 30 re f");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 9 Tf {col1 + 95} {y - 20} Td (cm transform) Tj ET");
        y -= 50;

        // Demo: Line width
        content.AppendLine("q");
        content.AppendLine("0.31 0.78 0.69 RG");
        content.AppendLine("1 w");
        content.AppendLine($"{col1} {y} m {col1 + 60} {y} l S");
        content.AppendLine("3 w");
        content.AppendLine($"{col1 + 70} {y} m {col1 + 130} {y} l S");
        content.AppendLine("6 w");
        content.AppendLine($"{col1 + 140} {y} m {col1 + 200} {y} l S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 9 Tf {col1} {y - 15} Td (w: 1pt, 3pt, 6pt) Tj ET");
        y -= 40;

        // Section: Path Construction (8.5.2)
        content.AppendLine($"BT /F1 11 Tf {col1} {y} Td (Path Construction \\(8.5.2\\)) Tj ET");
        y -= 25;

        // Lines (m, l)
        content.AppendLine("q");
        content.AppendLine("0.31 0.78 0.69 RG 2 w");
        content.AppendLine($"{col1} {y - 20} m {col1 + 40} {y} l {col1 + 80} {y - 20} l S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1} {y - 35} Td (m/l lines) Tj ET");

        // Bezier (c)
        content.AppendLine("q");
        content.AppendLine("0.31 0.78 0.69 RG 2 w");
        content.AppendLine($"{col1 + 100} {y - 20} m");
        content.AppendLine($"{col1 + 120} {y + 10} {col1 + 160} {y + 10} {col1 + 180} {y - 20} c S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1 + 100} {y - 35} Td (c bezier) Tj ET");
        y -= 55;

        // Rectangle and Triangle
        content.AppendLine("q");
        content.AppendLine("0.31 0.78 0.69 rg");
        content.AppendLine($"{col1} {y - 25} 50 25 re f");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1 + 5} {y - 18} Td (re) Tj ET");

        content.AppendLine("q");
        content.AppendLine("0.31 0.78 0.69 rg");
        content.AppendLine($"{col1 + 70} {y - 25} m {col1 + 120} {y - 25} l {col1 + 95} {y} l h f");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1 + 80} {y - 18} Td (h close) Tj ET");
        y -= 50;

        // Section: Path Painting (8.5.3)
        content.AppendLine($"BT /F1 11 Tf {col1} {y} Td (Path Painting \\(8.5.3\\)) Tj ET");
        y -= 30;

        // Stroke only
        content.AppendLine("q 0.31 0.78 0.69 RG 2 w");
        content.AppendLine($"{col1} {y - 20} 40 25 re S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1 + 8} {y - 12} Td (S) Tj ET");

        // Fill only
        content.AppendLine("q 0.31 0.78 0.69 rg");
        content.AppendLine($"{col1 + 55} {y - 20} 40 25 re f");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1 + 68} {y - 12} Td (f) Tj ET");

        // Fill and stroke
        content.AppendLine("q 0.2 0.6 0.5 rg 0.1 0.4 0.35 RG 2 w");
        content.AppendLine($"{col1 + 110} {y - 20} 40 25 re B");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1 + 120} {y - 12} Td (B) Tj ET");

        // Even-odd fill
        content.AppendLine("q 0.31 0.78 0.69 rg");
        content.AppendLine($"{col1 + 165} {y - 20} 40 25 re f*");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1 + 175} {y - 12} Td (f*) Tj ET");
        y -= 55;

        // Section: Colors (8.6)
        content.AppendLine($"BT /F1 11 Tf {col1} {y} Td (Colors \\(8.6\\): g/G, rg/RG) Tj ET");
        y -= 30;

        // Grayscale
        content.AppendLine("q 0.2 g");
        content.AppendLine($"{col1} {y - 20} 30 25 re f");
        content.AppendLine("0.5 g");
        content.AppendLine($"{col1 + 35} {y - 20} 30 25 re f");
        content.AppendLine("0.8 g");
        content.AppendLine($"{col1 + 70} {y - 20} 30 25 re f");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1} {y - 35} Td (g: grayscale) Tj ET");

        // RGB
        content.AppendLine("q 1 0.2 0.2 rg");
        content.AppendLine($"{col1 + 120} {y - 20} 30 25 re f");
        content.AppendLine("0.2 0.8 0.2 rg");
        content.AppendLine($"{col1 + 155} {y - 20} 30 25 re f");
        content.AppendLine("0.2 0.4 1 rg");
        content.AppendLine($"{col1 + 190} {y - 20} 30 25 re f");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 8 Tf {col1 + 120} {y - 35} Td (rg: RGB) Tj ET");

        // ===== RIGHT COLUMN: NOT YET IMPLEMENTED =====
        y = 750;
        content.AppendLine($"BT /F1 14 Tf {col2} {y} Td (NOT YET IMPLEMENTED) Tj ET");
        y -= 25;

        // Text Rendering (Section 9)
        content.AppendLine($"BT /F1 11 Tf {col2} {y} Td (Text Rendering \\(Section 9\\)) Tj ET");
        y -= 20;
        content.AppendLine("q 0.95 0.95 0.95 rg");
        content.AppendLine($"{col2} {y - 50} 220 50 re f");
        content.AppendLine("0.86 0.86 0.67 RG 2 w");  // Yellow border for in-progress
        content.AppendLine($"{col2} {y - 50} 220 50 re S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 9 Tf {col2 + 10} {y - 20} Td (Text operators are parsed but) Tj ET");
        content.AppendLine($"BT /F1 9 Tf {col2 + 10} {y - 35} Td (glyphs not yet rendered.) Tj ET");
        content.AppendLine($"BT /F1 8 Tf {col2 + 130} {y - 55} Td (Status: In Progress) Tj ET");
        y -= 80;

        // Images & XObjects (8.8-8.9)
        content.AppendLine($"BT /F1 11 Tf {col2} {y} Td (Images / XObjects \\(8.8-8.9\\)) Tj ET");
        y -= 20;
        content.AppendLine("q 0.95 0.95 0.95 rg");
        content.AppendLine($"{col2} {y - 35} 220 35 re f");
        content.AppendLine("0.43 0.43 0.43 RG 1 w");
        content.AppendLine($"{col2} {y - 35} 220 35 re S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 9 Tf {col2 + 10} {y - 22} Td (Do, BI/ID/EI operators) Tj ET");
        y -= 60;

        // CMYK Colors (8.6)
        content.AppendLine($"BT /F1 11 Tf {col2} {y} Td (CMYK Colors \\(8.6.8\\)) Tj ET");
        y -= 20;
        content.AppendLine("q 0.95 0.95 0.95 rg");
        content.AppendLine($"{col2} {y - 35} 220 35 re f");
        content.AppendLine("0.43 0.43 0.43 RG 1 w");
        content.AppendLine($"{col2} {y - 35} 220 35 re S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 9 Tf {col2 + 10} {y - 22} Td (k/K CMYK operators) Tj ET");
        y -= 60;

        // Clipping (8.5.4)
        content.AppendLine($"BT /F1 11 Tf {col2} {y} Td (Clipping Paths \\(8.5.4\\)) Tj ET");
        y -= 20;
        content.AppendLine("q 0.95 0.95 0.95 rg");
        content.AppendLine($"{col2} {y - 35} 220 35 re f");
        content.AppendLine("0.43 0.43 0.43 RG 1 w");
        content.AppendLine($"{col2} {y - 35} 220 35 re S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 9 Tf {col2 + 10} {y - 22} Td (W/W* clipping operators) Tj ET");
        y -= 60;

        // Transparency (Section 11)
        content.AppendLine($"BT /F1 11 Tf {col2} {y} Td (Transparency \\(Section 11\\)) Tj ET");
        y -= 20;
        content.AppendLine("q 0.95 0.95 0.95 rg");
        content.AppendLine($"{col2} {y - 35} 220 35 re f");
        content.AppendLine("0.43 0.43 0.43 RG 1 w");
        content.AppendLine($"{col2} {y - 35} 220 35 re S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 9 Tf {col2 + 10} {y - 22} Td (ca/CA, BM, SMask) Tj ET");
        y -= 60;

        // Shading (8.7)
        content.AppendLine($"BT /F1 11 Tf {col2} {y} Td (Shading / Patterns \\(8.7\\)) Tj ET");
        y -= 20;
        content.AppendLine("q 0.95 0.95 0.95 rg");
        content.AppendLine($"{col2} {y - 35} 220 35 re f");
        content.AppendLine("0.43 0.43 0.43 RG 1 w");
        content.AppendLine($"{col2} {y - 35} 220 35 re S");
        content.AppendLine("Q");
        content.AppendLine($"BT /F1 9 Tf {col2 + 10} {y - 22} Td (sh, Pattern operators) Tj ET");

        // Build PDF
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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
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
}

public enum FeatureCategory
{
    GraphicsState,
    PathConstruction,
    PathPainting,
    Color,
    Text,
    XObject,
    Clipping,
    Shading,
    Transparency
}

public enum FeatureStatus
{
    NotStarted,
    InProgress,
    Implemented
}

public class FeatureInfo
{
    public string Operator { get; }
    public string Description { get; }
    public FeatureCategory Category { get; }
    public FeatureStatus Status { get; }
    public string SpecSection { get; }
    public string WikiPage { get; }
    public string SpecTable { get; }

    public FeatureInfo(string op, string desc, FeatureCategory category, FeatureStatus status,
        string specSection = "", string wikiPage = "", string specTable = "")
    {
        Operator = op;
        Description = desc;
        Category = category;
        Status = status;
        SpecSection = specSection;
        WikiPage = wikiPage;
        SpecTable = specTable;
    }
}

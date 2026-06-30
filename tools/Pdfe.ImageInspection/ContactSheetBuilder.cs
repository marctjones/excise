using SkiaSharp;

namespace Pdfe.ImageInspection;

public static class ContactSheetBuilder
{
    public static SKBitmap Build(
        IReadOnlyList<ContactSheetItem> items,
        int columns,
        int cellWidth,
        int cellHeight)
    {
        if (items.Count == 0)
            throw new ArgumentException("At least one image is required.", nameof(items));
        if (columns < 1)
            throw new ArgumentOutOfRangeException(nameof(columns));

        var rows = (items.Count + columns - 1) / columns;
        var bitmap = new SKBitmap(columns * cellWidth, rows * cellHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var tilePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(210, 214, 220),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true,
        };
        using var labelPaint = new SKPaint { Color = new SKColor(20, 24, 32), IsAntialias = true };
        using var imagePaint = new SKPaint { IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 12);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

        canvas.Clear(new SKColor(244, 246, 248));
        const int padding = 10;
        const int labelHeight = 24;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var column = i % columns;
            var row = i / columns;
            var cellLeft = column * cellWidth;
            var cellTop = row * cellHeight;
            var tileRect = new SKRect(cellLeft + 4, cellTop + 4, cellLeft + cellWidth - 4, cellTop + cellHeight - 4);
            canvas.DrawRect(tileRect, tilePaint);
            canvas.DrawRect(tileRect, borderPaint);

            var label = FitLabel(item.Label, font, labelPaint, cellWidth - (padding * 2));
            canvas.DrawText(label, cellLeft + padding, cellTop + padding + 12, font, labelPaint);

            var maxWidth = cellWidth - (padding * 2);
            var maxHeight = cellHeight - labelHeight - (padding * 2);
            var scale = Math.Min((float)maxWidth / item.Bitmap.Width, (float)maxHeight / item.Bitmap.Height);
            scale = Math.Min(scale, 1.0f);
            var drawWidth = Math.Max(1, item.Bitmap.Width * scale);
            var drawHeight = Math.Max(1, item.Bitmap.Height * scale);
            var left = cellLeft + (cellWidth - drawWidth) / 2;
            var top = cellTop + labelHeight + padding + (maxHeight - drawHeight) / 2;
            var dest = new SKRect(left, top, left + drawWidth, top + drawHeight);
            using var image = SKImage.FromBitmap(item.Bitmap);
            canvas.DrawImage(image, dest, sampling, imagePaint);
        }

        canvas.Flush();
        return bitmap;
    }

    private static string FitLabel(string label, SKFont font, SKPaint paint, int maxWidth)
    {
        if (font.MeasureText(label, paint) <= maxWidth)
            return label;

        const string ellipsis = "...";
        var available = Math.Max(0, maxWidth - font.MeasureText(ellipsis, paint));
        var end = label.Length;
        while (end > 0 && font.MeasureText(label.AsSpan(0, end), paint) > available)
            end--;

        return end <= 0 ? ellipsis : label[..end] + ellipsis;
    }
}

public sealed record ContactSheetItem(string Label, string Path, SKBitmap Bitmap);

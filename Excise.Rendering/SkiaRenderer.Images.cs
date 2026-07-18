using System.Globalization;
using BitMiracle.LibJpeg.Classic;
using Excise.Core.ColorSpaces;
using Excise.Core.Filters.Jpx;
using Excise.Core.Primitives;
using SkiaSharp;

namespace Excise.Rendering;

internal partial class RenderContext
{
    private void RenderImageXObject(Excise.Core.Primitives.PdfStream imageStream)
    {
        var width = imageStream.GetInt("Width", 0);
        var height = imageStream.GetInt("Height", 0);
        if (width <= 0 || height <= 0)
            return;

        if (imageStream.GetBool("ImageMask") &&
            _state.FillPatternName != null &&
            TryDrawImageMaskWithPattern(imageStream, width, height))
        {
            return;
        }

        var bitsPerComponent = imageStream.GetInt("BitsPerComponent", 8);
        var colorSpace = ResolveImageColorSpaceFamilyName(imageStream);

        SKBitmap? mutableBitmap = null;
        try
        {
            var bitmap = GetOrDecodeImageBitmap(imageStream, width, height, bitsPerComponent, colorSpace);
            if (bitmap == null)
                return;

            // Draw the image at unit square (0,0)-(1,1), the CTM handles positioning
            _canvas.Save();

            // Images are drawn into a 1x1 unit square, scaled by the CTM
            // We need to flip Y because images have origin at top-left
            _canvas.Scale(1.0f / width, -1.0f / height);
            _canvas.Translate(0, -height);

            using var paint = new SKPaint
            {
                BlendMode = _state.BlendMode,
                IsAntialias = _options.AntiAlias
            };
            if (_state.FillAlpha < 1.0f)
            {
                paint.Color = paint.Color.WithAlpha((byte)(_state.FillAlpha * 255));
            }

            if (!TryDrawImageWithSoftMask(bitmap, imageStream, width, height, paint) &&
                !TryDrawImageWithExplicitMask(bitmap, imageStream, width, height, paint))
            {
                var bitmapToDraw = bitmap;
                if (imageStream.GetOptional("SMask") != null)
                {
                    mutableBitmap = bitmap.Copy(SKColorType.Rgba8888);
                    if (mutableBitmap != null)
                    {
                        ApplySoftMask(mutableBitmap, imageStream);
                        bitmapToDraw = mutableBitmap;
                    }
                }

                _canvas.DrawBitmap(bitmapToDraw, new SKRect(0, 0, width, height), paint);
                CompositeImageIntoDeviceCmykBackdrop(bitmapToDraw, width, height, paint);
            }
            _canvas.Restore();
        }
        finally
        {
            mutableBitmap?.Dispose();
        }
    }

    private bool TryDrawImageMaskWithPattern(
        Excise.Core.Primitives.PdfStream imageStream,
        int width,
        int height)
    {
        if (_state.FillPatternName == null)
            return false;

        SKBitmap? stencil = null;
        try
        {
            stencil = CreateImageMaskStencilBitmap(imageStream.DecodedData, width, height, imageStream);
            if (stencil == null)
                return false;

            _canvas.Save();
            try
            {
                // Image XObjects paint into the unit square in the current
                // user space. The source pixel grid is only the stencil
                // sampler; pattern colors must still be evaluated in that
                // current user space, not in source-pixel coordinates.
                var dest = new SKRect(0, 0, 1, 1);
                using var clipPath = new SKPath();
                clipPath.AddRect(dest);

                _canvas.SaveLayer();
                try
                {
                    if (!RenderFillPattern(clipPath))
                        return false;

                    using var maskPaint = new SKPaint
                    {
                        BlendMode = SKBlendMode.DstIn,
                        IsAntialias = _options.AntiAlias
                    };
                    DrawImageMaskStencil(stencil, dest, maskPaint);
                }
                finally
                {
                    _canvas.Restore();
                }

                return true;
            }
            finally
            {
                _canvas.Restore();
            }
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            stencil?.Dispose();
        }
    }

    private void DrawImageMaskStencil(SKBitmap stencil, SKRect dest, SKPaint maskPaint)
    {
        if (TryCreateDeviceSpaceAreaResampledStencil(stencil, dest, out var coverage, out var deviceDest) &&
            coverage != null)
        {
            using (coverage)
            {
                _canvas.Save();
                try
                {
                    _canvas.SetMatrix(SKMatrix.Identity);
                    _canvas.DrawBitmap(coverage, deviceDest, maskPaint);
                }
                finally
                {
                    _canvas.Restore();
                }
            }

            return;
        }

        using var stencilImage = SKImage.FromBitmap(stencil);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        _canvas.Save();
        try
        {
            _canvas.Translate(dest.Left, dest.Bottom);
            _canvas.Scale(dest.Width / stencil.Width, -dest.Height / stencil.Height);
            _canvas.DrawImage(
                stencilImage,
                new SKRect(0, 0, stencil.Width, stencil.Height),
                sampling,
                maskPaint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private bool TryCreateDeviceSpaceAreaResampledStencil(
        SKBitmap stencil,
        SKRect dest,
        out SKBitmap? coverage,
        out SKRect deviceDest)
    {
        coverage = null;
        deviceDest = default;
        var matrix = _canvas.TotalMatrix;
        const float epsilon = 0.001f;
        if (Math.Abs(matrix.SkewX) > epsilon ||
            Math.Abs(matrix.SkewY) > epsilon ||
            Math.Abs(matrix.ScaleX) <= epsilon ||
            Math.Abs(matrix.ScaleY) <= epsilon)
            return false;

        var deviceBounds = MapAxisAlignedRect(matrix, dest);
        var deviceLeft = (int)Math.Floor(deviceBounds.Left);
        var deviceTop = (int)Math.Floor(deviceBounds.Top);
        var deviceRight = (int)Math.Ceiling(deviceBounds.Right);
        var deviceBottom = (int)Math.Ceiling(deviceBounds.Bottom);
        var destWidth = deviceRight - deviceLeft;
        var destHeight = deviceBottom - deviceTop;
        if (destWidth <= 0 || destHeight <= 0)
            return false;
        if (destWidth >= stencil.Width && destHeight >= stencil.Height)
            return false;

        const int maxCoveragePixels = 16_000_000;
        if ((long)destWidth * destHeight > maxCoveragePixels)
            return false;

        coverage = CreateDeviceSpaceAreaResampledStencil(
            stencil,
            dest,
            matrix,
            deviceLeft,
            deviceTop,
            destWidth,
            destHeight);
        if (coverage == null)
            return false;

        deviceDest = new SKRect(deviceLeft, deviceTop, deviceRight, deviceBottom);
        return true;
    }

    private static SKRect MapAxisAlignedRect(SKMatrix matrix, SKRect rect)
    {
        var x0 = matrix.ScaleX * rect.Left + matrix.TransX;
        var x1 = matrix.ScaleX * rect.Right + matrix.TransX;
        var y0 = matrix.ScaleY * rect.Top + matrix.TransY;
        var y1 = matrix.ScaleY * rect.Bottom + matrix.TransY;
        return new SKRect(
            Math.Min(x0, x1),
            Math.Min(y0, y1),
            Math.Max(x0, x1),
            Math.Max(y0, y1));
    }

    private static SKRect MapRect(SKMatrix matrix, SKRect rect)
    {
        var p0 = MapPoint(matrix, rect.Left, rect.Top);
        var p1 = MapPoint(matrix, rect.Right, rect.Top);
        var p2 = MapPoint(matrix, rect.Right, rect.Bottom);
        var p3 = MapPoint(matrix, rect.Left, rect.Bottom);
        return new SKRect(
            Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)),
            Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)),
            Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)),
            Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)));
    }

    private static SKPoint MapPoint(SKMatrix matrix, float x, float y)
        => new(
            (matrix.ScaleX * x) + (matrix.SkewX * y) + matrix.TransX,
            (matrix.SkewY * x) + (matrix.ScaleY * y) + matrix.TransY);

    private bool TryGetLayerBounds(SKRect? preferredBounds, out SKRect bounds)
    {
        bounds = preferredBounds ?? _canvas.LocalClipBounds;
        var clipBounds = _canvas.LocalClipBounds;
        if (bounds.Width > 0 && bounds.Height > 0 && clipBounds.Width > 0 && clipBounds.Height > 0)
        {
            bounds.Intersect(clipBounds);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = clipBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = _canvas.DeviceClipBounds;

        return bounds.Width > 0 && bounds.Height > 0;
    }

    private (int Width, int Height) EstimateSoftMaskBitmapSize(SKRect localBounds)
    {
        var deviceBounds = MapRect(_canvas.TotalMatrix, localBounds);
        var targetWidth = Math.Max(1, (int)Math.Ceiling(deviceBounds.Width));
        var targetHeight = Math.Max(1, (int)Math.Ceiling(deviceBounds.Height));
        return ClampSoftMaskTargetSize(targetWidth, targetHeight, targetWidth, targetHeight);
    }

    private static SKBitmap? CreateDeviceSpaceAreaResampledStencil(
        SKBitmap source,
        SKRect dest,
        SKMatrix deviceFromLocal,
        int deviceLeft,
        int deviceTop,
        int destWidth,
        int destHeight)
    {
        if (destWidth <= 0 || destHeight <= 0)
            return null;

        var pixels = new byte[destWidth * destHeight * 4];
        var sourceScaleX = source.Width / (double)dest.Width;
        var sourceScaleY = source.Height / (double)dest.Height;
        var localPixelWidth = Math.Abs(1.0 / deviceFromLocal.ScaleX);
        var localPixelHeight = Math.Abs(1.0 / deviceFromLocal.ScaleY);
        var localPixelArea = localPixelWidth * localPixelHeight;
        var dst = 0;

        for (var y = 0; y < destHeight; y++)
        {
            var localY0 = (deviceTop + y - deviceFromLocal.TransY) / deviceFromLocal.ScaleY;
            var localY1 = (deviceTop + y + 1 - deviceFromLocal.TransY) / deviceFromLocal.ScaleY;
            var localTop = Math.Min(localY0, localY1);
            var localBottom = Math.Max(localY0, localY1);
            var clippedTop = Math.Max(dest.Top, localTop);
            var clippedBottom = Math.Min(dest.Bottom, localBottom);

            for (var x = 0; x < destWidth; x++)
            {
                var localX0 = (deviceLeft + x - deviceFromLocal.TransX) / deviceFromLocal.ScaleX;
                var localX1 = (deviceLeft + x + 1 - deviceFromLocal.TransX) / deviceFromLocal.ScaleX;
                var localLeft = Math.Min(localX0, localX1);
                var localRight = Math.Max(localX0, localX1);
                var clippedLeft = Math.Max(dest.Left, localLeft);
                var clippedRight = Math.Min(dest.Right, localRight);

                byte alpha = 0;
                if (clippedRight > clippedLeft && clippedBottom > clippedTop)
                {
                    var sourceLeft = (clippedLeft - dest.Left) * sourceScaleX;
                    var sourceRight = (clippedRight - dest.Left) * sourceScaleX;
                    // PDF image space maps the first image sample row to the
                    // top of the unit-square image area. Normal image drawing
                    // applies this as a Y flip; the device-space stencil path
                    // must do the same while sampling coverage directly.
                    var sourceTop = (dest.Bottom - clippedBottom) * sourceScaleY;
                    var sourceBottom = (dest.Bottom - clippedTop) * sourceScaleY;
                    var averageAlpha = AverageAlphaOverSourceRect(
                        source,
                        sourceLeft,
                        sourceTop,
                        sourceRight,
                        sourceBottom);
                    var coveredLocalArea = (clippedRight - clippedLeft) * (clippedBottom - clippedTop);
                    var devicePixelCoverage = localPixelArea > 0
                        ? Math.Clamp(coveredLocalArea / localPixelArea, 0, 1)
                        : 0;
                    alpha = (byte)Math.Clamp(
                        (int)Math.Round(averageAlpha * devicePixelCoverage),
                        0,
                        255);
                }

                // The bitmap is declared premultiplied, so partial stencil
                // pixels must use premultiplied white rather than RGB 255.
                pixels[dst++] = alpha;
                pixels[dst++] = alpha;
                pixels[dst++] = alpha;
                pixels[dst++] = alpha;
            }
        }

        return CreateBitmapFromRgbaBytes(destWidth, destHeight, pixels);
    }

    private static double AverageAlphaOverSourceRect(
        SKBitmap source,
        double left,
        double top,
        double right,
        double bottom)
    {
        left = Math.Clamp(left, 0, source.Width);
        right = Math.Clamp(right, 0, source.Width);
        top = Math.Clamp(top, 0, source.Height);
        bottom = Math.Clamp(bottom, 0, source.Height);
        if (right <= left || bottom <= top)
            return 0;

        var firstSourceX = Math.Max(0, (int)Math.Floor(left));
        var lastSourceX = Math.Min(source.Width - 1, (int)Math.Ceiling(right) - 1);
        var firstSourceY = Math.Max(0, (int)Math.Floor(top));
        var lastSourceY = Math.Min(source.Height - 1, (int)Math.Ceiling(bottom) - 1);
        var weightedAlpha = 0.0;
        var coveredArea = 0.0;

        for (var sy = firstSourceY; sy <= lastSourceY; sy++)
        {
            var yCoverage = Math.Min(sy + 1, bottom) - Math.Max(sy, top);
            if (yCoverage <= 0)
                continue;

            for (var sx = firstSourceX; sx <= lastSourceX; sx++)
            {
                var xCoverage = Math.Min(sx + 1, right) - Math.Max(sx, left);
                if (xCoverage <= 0)
                    continue;

                var area = xCoverage * yCoverage;
                weightedAlpha += source.GetPixel(sx, sy).Alpha * area;
                coveredArea += area;
            }
        }

        return coveredArea > 0 ? weightedAlpha / coveredArea : 0;
    }

    private SKBitmap? GetOrDecodeImageBitmap(
        Excise.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        int bitsPerComponent,
        string colorSpace)
    {
        var key = CreateImageBitmapCacheKey(imageStream, width, height, bitsPerComponent, colorSpace);
        if (TryGetImageReferenceKey(imageStream, out var referenceKey))
        {
            var cacheKey = (referenceKey.ObjectNumber, referenceKey.Generation, key);
            if (!_imageBitmapByReference.TryGetValue(cacheKey, out var bitmap))
            {
                bitmap = DecodeImageBitmap(imageStream, width, height, bitsPerComponent, colorSpace);
                TrackCachedImageBitmap(bitmap);
                _imageBitmapByReference[cacheKey] = bitmap;
            }

            return bitmap;
        }

        if (!_imageBitmapByStream.TryGetValue(imageStream, out var streamCache))
        {
            streamCache = new Dictionary<ImageBitmapCacheKey, SKBitmap?>();
            _imageBitmapByStream[imageStream] = streamCache;
        }

        if (!streamCache.TryGetValue(key, out var streamBitmap))
        {
            streamBitmap = DecodeImageBitmap(imageStream, width, height, bitsPerComponent, colorSpace);
            TrackCachedImageBitmap(streamBitmap);
            streamCache[key] = streamBitmap;
        }

        return streamBitmap;
    }

    private SKBitmap? DecodeImageBitmap(
        Excise.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        int bitsPerComponent,
        string colorSpace)
    {
        try
        {
            var filters = imageStream.Filters;
            if (IsTerminalDctFilter(filters))
            {
                var (targetWidth, targetHeight) = EstimateImageDecodeSize(width, height);
                var dctData = GetTerminalDctData(imageStream, filters);
                if (ResolveDctColorTransform(imageStream, filters, dctData, colorSpace) is { } colorTransform)
                {
                    var decoded = DecodeDctImageWithColorTransform(
                        dctData,
                        width,
                        height,
                        colorSpace,
                        targetWidth,
                        targetHeight,
                        colorTransform,
                        imageStream);
                    if (decoded != null)
                        return decoded;
                }

                return SafeDecode(
                    dctData,
                    GetDecodeSize(width, height, targetWidth, targetHeight));
            }

            if (filters.Contains("JPXDecode"))
            {
                var bitmap = DecodeJpxImage(imageStream, width, height);
                bitmap ??= SafeDecode(imageStream.EncodedData);
                return bitmap;
            }

            return CreateBitmapFromRawData(
                imageStream.DecodedData,
                width,
                height,
                bitsPerComponent,
                colorSpace,
                imageStream);
        }
        catch
        {
            return null;
        }
    }

    private ImageBitmapCacheKey CreateImageBitmapCacheKey(
        Excise.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        int bitsPerComponent,
        string colorSpace)
    {
        var filters = imageStream.Filters;
        var (targetWidth, targetHeight) = filters.Contains("JPXDecode") || ContainsDctFilter(filters)
            ? EstimateImageDecodeSize(width, height)
            : (width, height);
        var isImageMask = imageStream.GetBool("ImageMask");
        var fillAlpha = isImageMask
            ? (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255)
            : (byte)0;
        var dctColorTransform = IsTerminalDctFilter(filters)
            ? ResolveDctColorTransform(
                imageStream,
                filters,
                GetTerminalDctData(imageStream, filters),
                colorSpace)
            : null;

        return new ImageBitmapCacheKey(
            width,
            height,
            bitsPerComponent,
            colorSpace,
            targetWidth,
            targetHeight,
            isImageMask,
            isImageMask ? _state.FillColor.Red : (byte)0,
            isImageMask ? _state.FillColor.Green : (byte)0,
            isImageMask ? _state.FillColor.Blue : (byte)0,
            fillAlpha,
            dctColorTransform);
    }

    private void TrackCachedImageBitmap(SKBitmap? bitmap)
    {
        if (bitmap != null)
            _cachedImageBitmaps.Add(bitmap);
    }

    private void DisposeImageBitmapCache()
    {
        foreach (var bitmap in _cachedImageBitmaps)
            bitmap.Dispose();
        _cachedImageBitmaps.Clear();
        _imageBitmapByReference.Clear();
        _imageBitmapByStream.Clear();
    }

    private void DisposeOwnedResources()
    {
        DisposeImageBitmapCache();
        foreach (var typeface in _embeddedTypefaces.Values)
            typeface.Dispose();
        _embeddedTypefaces.Clear();
    }

    private static bool TryGetImageReferenceKey(
        Excise.Core.Primitives.PdfStream imageStream,
        out (int ObjectNumber, int Generation) key)
    {
        if (imageStream.ObjectNumber.HasValue)
        {
            key = (imageStream.ObjectNumber.Value, imageStream.GenerationNumber ?? 0);
            return true;
        }

        key = default;
        return false;
    }

    private bool TryDrawImageWithSoftMask(
        SKBitmap bitmap,
        Excise.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        SKPaint imagePaint)
    {
        var maskObj = imageStream.GetOptional("SMask");
        if (maskObj == null)
            return false;

        var resolved = _page.Document.Resolve(maskObj);
        if (resolved is not Excise.Core.Primitives.PdfStream maskStream)
            return false;

        if (string.Equals(maskStream.GetNameOrNull("Subtype"), "Form", StringComparison.Ordinal))
            return false;

        var maskWidth = maskStream.GetInt("Width", 0);
        var maskHeight = maskStream.GetInt("Height", 0);
        if (maskWidth <= 0 || maskHeight <= 0)
            return false;

        if (IsFullyOpaqueSoftMask(maskStream, maskWidth, maskHeight))
        {
            var directDest = new SKRect(0, 0, width, height);
            _canvas.DrawBitmap(bitmap, directDest, imagePaint);
            CompositeImageIntoDeviceCmykBackdrop(bitmap, width, height, imagePaint);
            return true;
        }

        var (targetWidth, targetHeight) = EstimateImageSoftMaskTargetSize(
            width,
            height,
            bitmap.Width,
            bitmap.Height,
            maskWidth,
            maskHeight);
        var maskData = GetSoftMaskData(maskObj, maskStream, maskWidth, maskHeight, targetWidth, targetHeight);
        if (maskData == null || maskData.Data.Length == 0)
            return false;

        using var maskedBitmap = CreateSoftMaskedImageBitmap(bitmap, maskData);
        if (maskedBitmap == null)
            return false;

        var dest = new SKRect(0, 0, width, height);
        if ((maskedBitmap.Width != bitmap.Width || maskedBitmap.Height != bitmap.Height) &&
            TryDrawSoftMaskedImageInDeviceSpace(maskedBitmap, dest, imagePaint))
        {
            CompositeImageIntoDeviceCmykBackdrop(maskedBitmap, width, height, imagePaint);
            return true;
        }

        _canvas.DrawBitmap(maskedBitmap, dest, imagePaint);
        CompositeImageIntoDeviceCmykBackdrop(maskedBitmap, width, height, imagePaint);
        return true;
    }

    private (int Width, int Height) EstimateImageSoftMaskTargetSize(
        int imageWidth,
        int imageHeight,
        int decodedImageWidth,
        int decodedImageHeight,
        int maskWidth,
        int maskHeight)
    {
        if (decodedImageWidth == maskWidth && decodedImageHeight == maskHeight)
            return (decodedImageWidth, decodedImageHeight);

        var deviceDest = MapAxisAlignedRect(
            _canvas.TotalMatrix,
            new SKRect(0, 0, imageWidth, imageHeight));
        var targetWidth = Math.Max(1, (int)Math.Ceiling(deviceDest.Width));
        var targetHeight = Math.Max(1, (int)Math.Ceiling(deviceDest.Height));
        return ClampSoftMaskTargetSize(maskWidth, maskHeight, targetWidth, targetHeight);
    }

    private bool TryDrawSoftMaskedImageInDeviceSpace(SKBitmap bitmap, SKRect dest, SKPaint imagePaint)
    {
        var matrix = _canvas.TotalMatrix;
        const float epsilon = 0.001f;
        if (Math.Abs(matrix.SkewX) > epsilon ||
            Math.Abs(matrix.SkewY) > epsilon ||
            Math.Abs(matrix.ScaleX) <= epsilon ||
            Math.Abs(matrix.ScaleY) <= epsilon)
        {
            return false;
        }

        var deviceDest = MapAxisAlignedRect(matrix, dest);
        if (deviceDest.Width <= epsilon || deviceDest.Height <= epsilon)
            return false;

        _canvas.Save();
        try
        {
            _canvas.SetMatrix(SKMatrix.Identity);
            _canvas.DrawBitmap(bitmap, deviceDest, imagePaint);
        }
        finally
        {
            _canvas.Restore();
        }

        return true;
    }

    private static SKBitmap? CreateSoftMaskedImageBitmap(SKBitmap source, SoftMaskAlpha mask)
    {
        if (source.Width <= 0 || source.Height <= 0 || mask.Width <= 0 || mask.Height <= 0)
            return null;

        var pixels = new byte[checked(mask.Width * mask.Height * 4)];
        try
        {
            var sourcePixels = source.Pixels;
            if (sourcePixels.Length == 0)
                return null;

            var dst = 0;
            var maskIndex = 0;
            for (int y = 0; y < mask.Height; y++)
            {
                var sourceY = MapTargetToSource(y, mask.Height, source.Height);
                var sourceRow = sourceY * source.Width;
                for (int x = 0; x < mask.Width; x++)
                {
                    var sourceX = MapTargetToSource(x, mask.Width, source.Width);
                    var sourceIndex = sourceRow + sourceX;
                    var sourceColor = sourceIndex < sourcePixels.Length
                        ? sourcePixels[sourceIndex]
                        : SKColors.Transparent;
                    var maskAlpha = maskIndex < mask.Data.Length ? mask.Data[maskIndex] : (byte)0;
                    var alpha = (byte)((sourceColor.Alpha * maskAlpha + 127) / 255);
                    pixels[dst++] = sourceColor.Red;
                    pixels[dst++] = sourceColor.Green;
                    pixels[dst++] = sourceColor.Blue;
                    pixels[dst++] = alpha;
                    maskIndex++;
                }
            }

            return CreateBitmapFromRgbaBytes(mask.Width, mask.Height, pixels, SKAlphaType.Unpremul);
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private bool TryDrawImageWithExplicitMask(
        SKBitmap bitmap,
        Excise.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        SKPaint imagePaint)
    {
        var maskObj = imageStream.GetOptional("Mask");
        if (maskObj == null)
            return false;

        var resolved = _page.Document.Resolve(maskObj) ?? maskObj;
        if (resolved is not Excise.Core.Primitives.PdfStream maskStream)
            return false;

        var maskWidth = maskStream.GetInt("Width", 0);
        var maskHeight = maskStream.GetInt("Height", 0);
        if (maskWidth <= 0 || maskHeight <= 0)
            return false;

        using var maskBitmap = CreateExplicitImageMaskBitmap(maskStream, maskWidth, maskHeight);
        if (maskBitmap == null)
            return false;

        var dest = new SKRect(0, 0, width, height);
        using var layerPaint = new SKPaint
        {
            BlendMode = imagePaint.BlendMode,
            Color = imagePaint.Color,
            IsAntialias = imagePaint.IsAntialias
        };

        _canvas.SaveLayer(dest, layerPaint);
        _canvas.DrawBitmap(bitmap, dest);

        using var maskPaint = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            IsAntialias = _options.AntiAlias
        };
        _canvas.DrawBitmap(maskBitmap, dest, maskPaint);
        _canvas.Restore();

        using var maskedForBackdrop = CreateImageBitmapWithAlphaMask(bitmap, maskBitmap);
        CompositeImageIntoDeviceCmykBackdrop(maskedForBackdrop ?? bitmap, width, height, imagePaint);
        return true;
    }

    private static SKBitmap? CreateImageBitmapWithAlphaMask(SKBitmap source, SKBitmap mask)
    {
        if (source.Width <= 0 || source.Height <= 0 || mask.Width <= 0 || mask.Height <= 0)
            return null;

        var pixels = new byte[checked(source.Width * source.Height * 4)];
        var dst = 0;
        for (var y = 0; y < source.Height; y++)
        {
            var maskY = MapTargetToSource(y, source.Height, mask.Height);
            for (var x = 0; x < source.Width; x++)
            {
                var sourceColor = source.GetPixel(x, y);
                var maskX = MapTargetToSource(x, source.Width, mask.Width);
                var maskAlpha = mask.GetPixel(maskX, maskY).Alpha;
                var alpha = (byte)((sourceColor.Alpha * maskAlpha + 127) / 255);
                pixels[dst++] = sourceColor.Red;
                pixels[dst++] = sourceColor.Green;
                pixels[dst++] = sourceColor.Blue;
                pixels[dst++] = alpha;
            }
        }

        return CreateBitmapFromRgbaBytes(source.Width, source.Height, pixels, SKAlphaType.Unpremul);
    }

    private void CompositeImageIntoDeviceCmykBackdrop(SKBitmap image, int imageWidth, int imageHeight, SKPaint imagePaint)
    {
        if (_rootBitmap == null ||
            _deviceCmykBackdrop == null ||
            _deviceCmykTransparencyGroupDepth <= 0 ||
            image.Width <= 0 ||
            image.Height <= 0 ||
            imageWidth <= 0 ||
            imageHeight <= 0)
        {
            return;
        }

        var imageBounds = new SKRect(0, 0, imageWidth, imageHeight);
        var matrix = _canvas.TotalMatrix;
        var deviceBounds = MapRect(matrix, imageBounds);
        var clipBounds = _canvas.LocalClipBounds;
        if (clipBounds.Width > 0 && clipBounds.Height > 0)
        {
            var deviceClip = MapRect(matrix, clipBounds);
            deviceBounds.Intersect(deviceClip);
            if (deviceBounds.Width <= 0 || deviceBounds.Height <= 0)
                return;
        }

        var left = Math.Clamp((int)Math.Floor(deviceBounds.Left) - 1, 0, _rootBitmap.Width);
        var top = Math.Clamp((int)Math.Floor(deviceBounds.Top) - 1, 0, _rootBitmap.Height);
        var right = Math.Clamp((int)Math.Ceiling(deviceBounds.Right) + 1, 0, _rootBitmap.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(deviceBounds.Bottom) + 1, 0, _rootBitmap.Height);
        if (right <= left || bottom <= top)
            return;

        if (!TryInvertAffine(matrix, out var inverse))
            return;

        var paintAlpha = imagePaint.Color.Alpha / 255.0;
        var isNormalBlend = imagePaint.BlendMode == SKBlendMode.SrcOver;
        PdfSeparableBlendMode blend = default;
        if (!isNormalBlend && !TryMapSkiaBlendToPdfBlend(imagePaint.BlendMode, out blend))
            return;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var sourcePoint = MapPoint(inverse, x + 0.5f, y + 0.5f);
                if (sourcePoint.X < imageBounds.Left ||
                    sourcePoint.X >= imageBounds.Right ||
                    sourcePoint.Y < imageBounds.Top ||
                    sourcePoint.Y >= imageBounds.Bottom)
                {
                    continue;
                }

                var sourceX = Math.Clamp(
                    (int)((sourcePoint.X - imageBounds.Left) * image.Width / imageBounds.Width),
                    0,
                    image.Width - 1);
                var sourceY = Math.Clamp(
                    (int)((sourcePoint.Y - imageBounds.Top) * image.Height / imageBounds.Height),
                    0,
                    image.Height - 1);
                var pixel = image.GetPixel(sourceX, sourceY);
                var alpha = (pixel.Alpha / 255.0) * paintAlpha;
                if (alpha <= 0)
                    continue;

                var source = RgbToDeviceCmyk(
                    pixel.Red / 255.0,
                    pixel.Green / 255.0,
                    pixel.Blue / 255.0);
                var backdrop = _deviceCmykBackdrop.Get(x, y);
                var blended = isNormalBlend
                    ? source
                    : BlendDeviceCmykWithBackdropAlpha(
                        backdrop,
                        source,
                        blend,
                        _deviceCmykBackdrop.GetAlpha(x, y),
                        direct: false);
                _deviceCmykBackdrop.CompositeSourceOver(x, y, blended, alpha);
            }
        }
    }

    private static bool TryInvertAffine(SKMatrix matrix, out SKMatrix inverse)
    {
        var determinant = (matrix.ScaleX * matrix.ScaleY) - (matrix.SkewX * matrix.SkewY);
        if (Math.Abs(determinant) <= 1e-6f)
        {
            inverse = SKMatrix.Identity;
            return false;
        }

        var invDet = 1f / determinant;
        inverse = new SKMatrix(
            matrix.ScaleY * invDet,
            -matrix.SkewX * invDet,
            ((matrix.SkewX * matrix.TransY) - (matrix.ScaleY * matrix.TransX)) * invDet,
            -matrix.SkewY * invDet,
            matrix.ScaleX * invDet,
            ((matrix.SkewY * matrix.TransX) - (matrix.ScaleX * matrix.TransY)) * invDet,
            0,
            0,
            1);
        return true;
    }

    private SKBitmap? DecodeSoftMaskBitmap(
        Excise.Core.Primitives.PdfObject maskObj,
        Excise.Core.Primitives.PdfStream maskStream,
        int targetWidth,
        int targetHeight,
        SKRect? maskBounds = null)
    {
        if (string.Equals(maskStream.GetNameOrNull("Subtype"), "Form", StringComparison.Ordinal))
            return maskBounds.HasValue
                ? RenderFormSoftMaskBitmap(maskStream, targetWidth, targetHeight, maskBounds.Value)
                : null;

        var width = maskStream.GetInt("Width", 0);
        var height = maskStream.GetInt("Height", 0);
        if (width <= 0 || height <= 0)
            return null;

        var alpha = GetSoftMaskData(maskObj, maskStream, width, height, targetWidth, targetHeight);
        return alpha != null ? CreateSoftMaskLumaBitmap(alpha) : null;
    }

    private SKBitmap? RenderFormSoftMaskBitmap(
        Excise.Core.Primitives.PdfStream maskStream,
        int targetWidth,
        int targetHeight,
        SKRect maskBounds)
    {
        if (targetWidth <= 0 || targetHeight <= 0 || maskBounds.Width <= 0 || maskBounds.Height <= 0)
            return null;

        (targetWidth, targetHeight) = ClampSoftMaskTargetSize(
            Math.Max(1, targetWidth),
            Math.Max(1, targetHeight),
            targetWidth,
            targetHeight);

        var bitmap = new SKBitmap(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);

            var scaleX = targetWidth / maskBounds.Width;
            var scaleY = targetHeight / maskBounds.Height;
            canvas.SetMatrix(new SKMatrix(
                scaleX,
                0,
                -maskBounds.Left * scaleX,
                0,
                scaleY,
                -maskBounds.Top * scaleY,
                0,
                0,
                1));

            var child = new RenderContext(canvas, _page, _options, _cancellationToken);
            child._resourcesStack.Push(_page.Resources);
            child._state = _state.Clone();
            child._state.SoftMask = null;
            try
            {
                child.RenderFormXObject(maskStream);
            }
            finally
            {
                child._resourcesStack.Clear();
                child.DisposeOwnedResources();
            }

            return bitmap;
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            bitmap.Dispose();
            return null;
        }
    }

    private SKBitmap? DecodeJpxImage(Excise.Core.Primitives.PdfStream imageStream, int sourceWidth, int sourceHeight)
    {
        try
        {
            var colorSpaceObject = imageStream.GetOptional("ColorSpace");
            var colorSpace = colorSpaceObject != null
                ? ResolveImageColorSpace(colorSpaceObject)
                : PdfColorSpace.DeviceRGB;

            var desiredComponents = Math.Max(1, colorSpace.Components);
            if (colorSpace.Components >= 3 && imageStream.GetOptional("SMask") == null)
                desiredComponents++;

            var estimatedTarget = EstimateImageDecodeSize(sourceWidth, sourceHeight);
            var image = desiredComponents == 1 && imageStream.GetOptional("SMask") != null
                ? JpxDecoder.TryDecodeOpenJpegGray(imageStream.EncodedData)
                : TryDecodeJpxWithOpenJpeg(imageStream, sourceWidth, sourceHeight, estimatedTarget.Width, estimatedTarget.Height, desiredComponents);
            if (image == null && (long)sourceWidth * sourceHeight <= MaxExpandedSoftMaskPixels)
                image = JpxDecoder.TryDecodeManaged(imageStream.EncodedData, desiredComponents);
            if (image == null || sourceWidth <= 0 || sourceHeight <= 0 || image.Components <= 0)
                return null;

            var components = image.ComponentData;
            if (components.Length == 0)
                return null;

            var decodedWidth = image.Width > 0 ? image.Width : sourceWidth;
            var decodedHeight = image.Height > 0 ? image.Height : sourceHeight;
            var (targetWidth, targetHeight) = image.BitsPerComponent > 8
                ? ClampImageTargetSize(sourceWidth, sourceHeight, sourceWidth, sourceHeight)
                : ClampImageTargetSize(decodedWidth, decodedHeight, estimatedTarget.Width, estimatedTarget.Height);

            var pixels = new byte[checked(targetWidth * targetHeight * 4)];
            var dst = 0;
            var sourcePixelCount = (long)decodedWidth * decodedHeight;
            var hasExternalSoftMask = imageStream.GetOptional("SMask") != null;
            var hasEmbeddedAlpha = !hasExternalSoftMask &&
                                   components.Length > colorSpace.Components &&
                                   colorSpace.Components >= 1;
            for (int y = 0; y < targetHeight; y++)
            {
                var sourceY = MapTargetToSource(y, targetHeight, decodedHeight);
                var sourceRow = (long)sourceY * decodedWidth;
                for (int x = 0; x < targetWidth; x++)
                {
                    var sourceX = MapTargetToSource(x, targetWidth, decodedWidth);
                    var idx = sourceRow + sourceX;
                    if (idx >= sourcePixelCount)
                        continue;

                    double rd;
                    double gd;
                    double bd;
                    if (image.ComponentsAreDisplayRgb && components.Length >= 3)
                    {
                        rd = NormalizeJpxSampleToUnit(
                            idx < components[0].LongLength ? components[0][(int)idx] : 0,
                            image.BitsPerComponent);
                        gd = NormalizeJpxSampleToUnit(
                            idx < components[1].LongLength ? components[1][(int)idx] : 0,
                            image.BitsPerComponent);
                        bd = NormalizeJpxSampleToUnit(
                            idx < components[2].LongLength ? components[2][(int)idx] : 0,
                            image.BitsPerComponent);
                    }
                    else if (colorSpace.Type == PdfColorSpaceType.Indexed)
                    {
                        var values = new double[1];
                        values[0] = idx < components[0].Length ? components[0][idx] : 0;
                        (rd, gd, bd) = colorSpace.ToRgb(values);
                    }
                    else
                    {
                        var values = new double[Math.Max(1, colorSpace.Components)];
                        for (int c = 0; c < values.Length; c++)
                        {
                            var componentIndex = GetJpxColorComponentIndex(image, colorSpace, c, components.Length);
                            var sample = componentIndex < components.Length && idx < components[componentIndex].LongLength
                                ? components[componentIndex][(int)idx]
                                : 0;
                            values[c] = colorSpace.DecodeSampleByte(c, NormalizeJpxSampleToByte(sample, image.BitsPerComponent));
                        }

                        (rd, gd, bd) = colorSpace.ToRgb(values);
                    }

                    var alpha = 255;
                    if (hasEmbeddedAlpha)
                    {
                        var alphaComponentIndex = GetJpxAlphaComponentIndex(image, colorSpace.Components, components.Length);
                        var alphaComponent = components[alphaComponentIndex];
                        if (idx < alphaComponent.LongLength)
                            alpha = NormalizeJpxSampleToByte(alphaComponent[(int)idx], image.BitsPerComponent);
                    }

                    pixels[dst++] = (byte)Math.Clamp(rd * 255, 0, 255);
                    pixels[dst++] = (byte)Math.Clamp(gd * 255, 0, 255);
                    pixels[dst++] = (byte)Math.Clamp(bd * 255, 0, 255);
                    pixels[dst++] = (byte)alpha;
                }
            }

            return CreateBitmapFromRgbaBytes(targetWidth, targetHeight, pixels);
        }
        catch
        {
            return null;
        }
    }

    private JpxImage? TryDecodeJpxWithOpenJpeg(
        Excise.Core.Primitives.PdfStream imageStream,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        int desiredComponents)
    {
        var reduceFactor = ChooseOpenJpegReduceFactor(sourceWidth, sourceHeight, targetWidth, targetHeight);
        return JpxDecoder.TryDecodeOpenJpeg(imageStream.EncodedData, reduceFactor);
    }

    private static int ChooseOpenJpegReduceFactor(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        const int maxOpenJpegReduction = 5;
        var reduce = 0;
        while (reduce < maxOpenJpegReduction)
        {
            var next = reduce + 1;
            var nextWidth = Math.Max(1, sourceWidth >> next);
            var nextHeight = Math.Max(1, sourceHeight >> next);
            if (nextWidth < targetWidth || nextHeight < targetHeight)
            {
                var currentWidth = Math.Max(1, sourceWidth >> reduce);
                var currentHeight = Math.Max(1, sourceHeight >> reduce);
                if ((long)currentWidth * currentHeight > MaxExpandedSoftMaskPixels)
                {
                    reduce = next;
                    continue;
                }

                break;
            }

            reduce = next;
        }

        return reduce;
    }

    private static int GetJpxColorComponentIndex(
        JpxImage image,
        PdfColorSpace colorSpace,
        int requestedComponent,
        int decodedComponentCount)
    {
        if (image.ComponentDefinitions.Count > 0)
        {
            var association = requestedComponent + 1;
            foreach (var component in image.ComponentDefinitions)
            {
                if (component.Type == 0 &&
                    component.Association == association &&
                    component.ComponentIndex >= 0 &&
                    component.ComponentIndex < decodedComponentCount)
                {
                    return component.ComponentIndex;
                }
            }
        }

        if (decodedComponentCount >= 3 &&
            requestedComponent < 3 &&
            !image.ComponentsAreLogicalColorOrder &&
            colorSpace.Components == 3 &&
            (colorSpace.Type == PdfColorSpaceType.DeviceRGB ||
             colorSpace.Type == PdfColorSpaceType.CalRGB ||
             colorSpace.Type == PdfColorSpaceType.ICCBased))
        {
            // CSJ2K exposes decoded color components in bitmap BGR order for
            // RGB JP2 images. PDF color conversion expects logical RGB order.
            return 2 - requestedComponent;
        }

        return requestedComponent;
    }

    private static int GetJpxAlphaComponentIndex(JpxImage image, int fallbackIndex, int decodedComponentCount)
    {
        if (image.ComponentDefinitions.Count > 0)
        {
            foreach (var component in image.ComponentDefinitions)
            {
                if (component.Type is 1 or 2 &&
                    component.ComponentIndex >= 0 &&
                    component.ComponentIndex < decodedComponentCount)
                {
                    return component.ComponentIndex;
                }
            }
        }

        return Math.Clamp(fallbackIndex, 0, Math.Max(0, decodedComponentCount - 1));
    }

    private static byte NormalizeJpxSampleToByte(int sample, int bitsPerComponent)
    {
        if (bitsPerComponent <= 8)
            return (byte)Math.Clamp(sample, 0, 255);

        var maxSample = bitsPerComponent >= 31
            ? int.MaxValue
            : (1 << bitsPerComponent) - 1;
        if (maxSample <= 255)
            return (byte)Math.Clamp(sample, 0, 255);

        var normalized = (long)Math.Clamp(sample, 0, maxSample) * 255 + (maxSample / 2);
        return (byte)(normalized / maxSample);
    }

    private static double NormalizeJpxSampleToUnit(int sample, int bitsPerComponent)
    {
        if (bitsPerComponent <= 8)
            return Math.Clamp(sample, 0, 255) / 255.0;

        var maxSample = bitsPerComponent >= 31
            ? int.MaxValue
            : (1 << bitsPerComponent) - 1;
        return maxSample > 0
            ? Math.Clamp(sample, 0, maxSample) / (double)maxSample
            : 0;
    }

    private (int Width, int Height) EstimateImageDecodeSize(int sourceWidth, int sourceHeight)
    {
        var userWidth = Math.Sqrt(
            (_state.CurrentTransform.ScaleX * _state.CurrentTransform.ScaleX)
            + (_state.CurrentTransform.SkewY * _state.CurrentTransform.SkewY));
        var userHeight = Math.Sqrt(
            (_state.CurrentTransform.SkewX * _state.CurrentTransform.SkewX)
            + (_state.CurrentTransform.ScaleY * _state.CurrentTransform.ScaleY));

        var scale = Math.Max(1, _options.Dpi) / 72.0;
        var targetWidth = userWidth > 0
            ? Math.Clamp((int)Math.Round(userWidth * scale), 1, sourceWidth)
            : sourceWidth;
        var targetHeight = userHeight > 0
            ? Math.Clamp((int)Math.Round(userHeight * scale), 1, sourceHeight)
            : sourceHeight;

        return ClampImageTargetSize(sourceWidth, sourceHeight, targetWidth, targetHeight);
    }

    private static (int Width, int Height) ClampImageTargetSize(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        var width = Math.Clamp(targetWidth, 1, Math.Max(1, sourceWidth));
        var height = Math.Clamp(targetHeight, 1, Math.Max(1, sourceHeight));
        var pixels = (long)width * height;
        if (pixels <= MaxExpandedSoftMaskPixels)
            return (width, height);

        var scale = Math.Sqrt(MaxExpandedSoftMaskPixels / (double)pixels);
        return (
            Math.Max(1, (int)Math.Floor(width * scale)),
            Math.Max(1, (int)Math.Floor(height * scale)));
    }

    private bool ApplySoftMask(SKBitmap bitmap, Excise.Core.Primitives.PdfStream imageStream)
    {
        var maskObj = imageStream.GetOptional("SMask");
        if (maskObj == null)
            return false;

        var resolved = _page.Document.Resolve(maskObj);
        if (resolved is not Excise.Core.Primitives.PdfStream maskStream)
            return false;

        var maskWidth = maskStream.GetInt("Width", 0);
        var maskHeight = maskStream.GetInt("Height", 0);
        if (maskWidth <= 0 || maskHeight <= 0)
            return false;

        var targetWidth = Math.Max(1, bitmap.Width);
        var targetHeight = Math.Max(1, bitmap.Height);
        var maskData = GetSoftMaskData(maskObj, maskStream, maskWidth, maskHeight, targetWidth, targetHeight);
        if (maskData == null || maskData.Data.Length == 0)
            return false;

        for (int y = 0; y < bitmap.Height; y++)
        {
            var maskY = Math.Clamp((int)((long)y * maskData.Height / bitmap.Height), 0, maskData.Height - 1);
            for (int x = 0; x < bitmap.Width; x++)
            {
                var maskX = Math.Clamp((int)((long)x * maskData.Width / bitmap.Width), 0, maskData.Width - 1);
                var alphaIndex = maskY * maskData.Width + maskX;
                if (alphaIndex >= maskData.Data.Length)
                    continue;

                var color = bitmap.GetPixel(x, y);
                bitmap.SetPixel(x, y, color.WithAlpha(maskData.Data[alphaIndex]));
            }
        }

        return true;
    }

    private SoftMaskAlpha? GetSoftMaskData(
        Excise.Core.Primitives.PdfObject maskObj,
        Excise.Core.Primitives.PdfStream maskStream,
        int maskWidth,
        int maskHeight,
        int targetWidth,
        int targetHeight)
    {
        SoftMaskAlpha? maskData;
        if (TryGetSoftMaskReferenceKey(maskObj, maskStream, out var key))
        {
            var cacheKey = (key.ObjectNumber, key.Generation, targetWidth, targetHeight);
            if (!_softMaskAlphaByReference.TryGetValue(cacheKey, out maskData))
            {
                maskData = DecodeSoftMaskData(maskStream, maskWidth, maskHeight, targetWidth, targetHeight);
                _softMaskAlphaByReference[cacheKey] = maskData;
            }
        }
        else
        {
            if (!_softMaskAlphaByStream.TryGetValue(maskStream, out var streamCache))
            {
                streamCache = new Dictionary<(int TargetWidth, int TargetHeight), SoftMaskAlpha?>();
                _softMaskAlphaByStream[maskStream] = streamCache;
            }

            var cacheKey = (targetWidth, targetHeight);
            if (!streamCache.TryGetValue(cacheKey, out maskData))
            {
                maskData = DecodeSoftMaskData(maskStream, maskWidth, maskHeight, targetWidth, targetHeight);
                streamCache[cacheKey] = maskData;
            }
        }

        return maskData;
    }

    private static bool IsFullyOpaqueSoftMask(Excise.Core.Primitives.PdfStream maskStream, int width, int height)
    {
        if (width <= 0 ||
            height <= 0 ||
            maskStream.GetInt("BitsPerComponent", 8) != 8 ||
            DecodeSoftMaskSample(maskStream, 255, 8) != 255)
        {
            return false;
        }

        var filters = maskStream.Filters;
        if (filters.Count != 1 ||
            filters[0] is not ("RunLengthDecode" or "RL"))
        {
            return false;
        }

        return RunLengthDecodesToRepeatedByte(maskStream.EncodedData, (long)width * height, 0xff);
    }

    private static bool RunLengthDecodesToRepeatedByte(byte[] data, long expectedLength, byte expectedValue)
    {
        if (data.Length == 0 || expectedLength <= 0)
            return false;

        var decoded = 0L;
        var i = 0;
        while (i < data.Length)
        {
            var length = data[i++];
            if (length == 128)
                break;

            int count;
            if (length < 128)
            {
                count = length + 1;
                if (i + count > data.Length)
                    return false;

                for (var j = 0; j < count; j++)
                {
                    if (data[i + j] != expectedValue)
                        return false;
                }

                i += count;
            }
            else
            {
                count = 257 - length;
                if (i >= data.Length || data[i++] != expectedValue)
                    return false;
            }

            decoded += count;
            if (decoded > expectedLength)
                return false;
        }

        return decoded == expectedLength;
    }

    private static SoftMaskAlpha? DecodeSoftMaskData(
        Excise.Core.Primitives.PdfStream maskStream,
        int width,
        int height,
        int targetWidth,
        int targetHeight)
    {
        (targetWidth, targetHeight) = ClampSoftMaskTargetSize(width, height, targetWidth, targetHeight);

        var filters = maskStream.Filters;
        if (IsTerminalDctFilter(filters))
        {
            using var maskBitmap = SafeDecode(
                GetTerminalDctData(maskStream, filters),
                GetDecodeSize(width, height, targetWidth, targetHeight));
            if (maskBitmap != null)
                return new SoftMaskAlpha(
                    ExtractSoftMaskAlpha(maskBitmap, targetWidth, targetHeight, maskStream),
                    targetWidth,
                    targetHeight);

            return null;
        }

        if (filters.Contains("JPXDecode"))
        {
            var jpx = JpxDecoder.TryDecodeManaged(maskStream.EncodedData);
            if (jpx is { Components: > 0 } && jpx.ComponentData.Length > 0)
            {
                var component = jpx.ComponentData[0];
                if (component.Length >= width * height)
                {
                    var alpha = CreateSoftMaskAlphaFromSamples(component, width, height, targetWidth, targetHeight, maskStream);
                    return new SoftMaskAlpha(alpha, targetWidth, targetHeight);
                }
            }

            using var maskBitmap = SafeDecode(maskStream.EncodedData);
            if (maskBitmap != null)
                return new SoftMaskAlpha(ExtractSoftMaskAlpha(maskBitmap, targetWidth, targetHeight, maskStream), targetWidth, targetHeight);
        }

        var bitsPerComponent = maskStream.GetInt("BitsPerComponent", 8);
        if (bitsPerComponent == 8)
        {
            var data = maskStream.DecodedData;
            if (data.LongLength < (long)width * height)
                return null;

            var alpha = CreateSoftMaskAlphaFrom8Bit(data, width, height, targetWidth, targetHeight, maskStream);
            return new SoftMaskAlpha(alpha, targetWidth, targetHeight);
        }

        if (bitsPerComponent == 1)
        {
            var data = maskStream.DecodedData;
            var alpha = new byte[targetWidth * targetHeight];
            var dst = 0;
            for (int y = 0; y < targetHeight; y++)
            {
                var sourceY = MapTargetToSource(y, targetHeight, height);
                for (int x = 0; x < targetWidth; x++)
                {
                    var sourceX = MapTargetToSource(x, targetWidth, width);
                    alpha[dst++] = DecodeSoftMaskSample(
                        maskStream,
                        ReadOneBitImageSample(data, width, sourceX, sourceY),
                        bitsPerComponent);
                }
            }
            return new SoftMaskAlpha(alpha, targetWidth, targetHeight);
        }

        return null;
    }

    private static (int Width, int Height) ClampSoftMaskTargetSize(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        var width = Math.Clamp(targetWidth, 1, Math.Max(1, sourceWidth));
        var height = Math.Clamp(targetHeight, 1, Math.Max(1, sourceHeight));
        var pixels = (long)width * height;
        if (pixels <= MaxExpandedSoftMaskPixels)
            return (width, height);

        var scale = Math.Sqrt(MaxExpandedSoftMaskPixels / (double)pixels);
        return (
            Math.Max(1, (int)Math.Floor(width * scale)),
            Math.Max(1, (int)Math.Floor(height * scale)));
    }

    private static bool IsTerminalDctFilter(IReadOnlyList<string> filters)
        => filters.Count > 0 && IsDctFilter(filters[^1]);

    private static bool ContainsDctFilter(IReadOnlyList<string> filters)
        => filters.Any(IsDctFilter);

    private static bool IsDctFilter(string filter)
        => string.Equals(filter, "DCTDecode", StringComparison.Ordinal)
           || string.Equals(filter, "DCT", StringComparison.Ordinal);

    private static byte[] GetTerminalDctData(Excise.Core.Primitives.PdfStream stream, IReadOnlyList<string> filters)
        => filters.Count == 1 ? stream.EncodedData : stream.DecodedData;

    private int? ResolveDctColorTransform(
        PdfStream stream,
        IReadOnlyList<string> filters,
        byte[] dctData,
        string colorSpace)
    {
        var normalizedColorSpace = NormalizeDctColorSpaceName(colorSpace);
        if (TryGetAdobeDctColorTransform(dctData, out var markerColorTransform))
        {
            if (normalizedColorSpace == "DeviceCMYK" || markerColorTransform == 0)
                return markerColorTransform;

            if (normalizedColorSpace == "DeviceRGB")
                return null;
        }

        if (GetTerminalDctDecodeParmsColorTransform(stream, filters) is { } decodeParmsColorTransform)
            return decodeParmsColorTransform;

        return normalizedColorSpace == "DeviceCMYK" ? 0 : null;
    }

    private int? GetTerminalDctDecodeParmsColorTransform(PdfStream stream, IReadOnlyList<string> filters)
    {
        try
        {
            var parmsObject = stream.GetOptional("DecodeParms") ?? stream.GetOptional("DP");
            if (parmsObject == null || filters.Count == 0)
                return null;

            PdfDictionary? parms = null;
            var resolved = _page.Document.Resolve(parmsObject);
            if (resolved is PdfDictionary dictionary && filters.Count == 1)
            {
                parms = dictionary;
            }
            else if (resolved is PdfArray array)
            {
                var filterIndex = filters.Count - 1;
                if (filterIndex >= 0 && filterIndex < array.Count)
                    parms = _page.Document.Resolve(array[filterIndex]) as PdfDictionary;
            }

            if (parms == null)
                return null;

            var colorTransformObj = parms.GetOptional("ColorTransform");
            if (!TryGetResolvedNumber(colorTransformObj, out var colorTransform))
                return null;

            var value = (int)colorTransform;
            return value is 0 or 1 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private SKBitmap? DecodeDctImageWithColorTransform(
        byte[] data,
        int sourceWidth,
        int sourceHeight,
        string colorSpace,
        int targetWidth,
        int targetHeight,
        int colorTransform,
        Excise.Core.Primitives.PdfStream stream)
    {
        if (data.Length == 0 ||
            !TryGetDctColorSpaces(colorSpace, colorTransform, out var inputColorSpace, out var outputColorSpace))
        {
            return null;
        }

        var scaleDenominator = outputColorSpace == J_COLOR_SPACE.JCS_CMYK
            ? 1
            : ChooseDctScaleDenominator(sourceWidth, sourceHeight, targetWidth, targetHeight);
        var cinfo = new jpeg_decompress_struct();
        try
        {
            using var input = new MemoryStream(data, writable: false);
            cinfo.jpeg_stdio_src(input);
            cinfo.jpeg_read_header(true);
            cinfo.Jpeg_color_space = inputColorSpace;
            cinfo.Out_color_space = outputColorSpace;
            cinfo.Scale_num = 1;
            cinfo.Scale_denom = scaleDenominator;

            cinfo.jpeg_start_decompress();
            var width = cinfo.Output_width;
            var height = cinfo.Output_height;
            if (width <= 0 || height <= 0)
                return null;

            if (outputColorSpace == J_COLOR_SPACE.JCS_CMYK)
                return DecodeDctCmykBitmap(cinfo, width, height, sourceWidth, sourceHeight, targetWidth, targetHeight, stream);

            if (cinfo.Output_components != 3)
                return null;

            var pixels = new byte[checked(width * height * 4)];
            var scanline = new[] { new byte[checked(width * cinfo.Output_components)] };
            var dst = 0;
            while (cinfo.Output_scanline < cinfo.Output_height)
            {
                cinfo.jpeg_read_scanlines(scanline, 1);
                var row = scanline[0];
                for (var src = 0; src < width * 3;)
                {
                    pixels[dst++] = row[src++];
                    pixels[dst++] = row[src++];
                    pixels[dst++] = row[src++];
                    pixels[dst++] = 255;
                }
            }

            cinfo.jpeg_finish_decompress();
            var bitmap = CreateBitmapFromRgbaBytes(width, height, pixels);
            if (bitmap == null)
                return null;

            return ResizeDecodedBitmap(
                bitmap,
                Math.Clamp(targetWidth, 1, sourceWidth),
                Math.Clamp(targetHeight, 1, sourceHeight));
        }
        catch
        {
            return null;
        }
        finally
        {
            try { cinfo.jpeg_destroy(); }
            catch { /* Ignore cleanup failures from malformed JPEG data. */ }
        }
    }

    private static bool TryGetDctColorSpaces(
        string colorSpace,
        int colorTransform,
        out J_COLOR_SPACE inputColorSpace,
        out J_COLOR_SPACE outputColorSpace)
    {
        inputColorSpace = J_COLOR_SPACE.JCS_UNKNOWN;
        outputColorSpace = J_COLOR_SPACE.JCS_UNKNOWN;
        switch (NormalizeDctColorSpaceName(colorSpace))
        {
            case "DeviceRGB":
                inputColorSpace = colorTransform == 0
                    ? J_COLOR_SPACE.JCS_RGB
                    : J_COLOR_SPACE.JCS_YCbCr;
                outputColorSpace = J_COLOR_SPACE.JCS_RGB;
                return true;
            case "DeviceCMYK":
                inputColorSpace = colorTransform == 0
                    ? J_COLOR_SPACE.JCS_CMYK
                    : J_COLOR_SPACE.JCS_YCCK;
                outputColorSpace = J_COLOR_SPACE.JCS_CMYK;
                return true;
            default:
                return false;
        }
    }

    private SKBitmap? DecodeDctCmykBitmap(
        jpeg_decompress_struct cinfo,
        int width,
        int height,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Excise.Core.Primitives.PdfStream stream)
    {
        if (cinfo.Output_components != 4)
            return null;

        var samples = new byte[checked(width * height * 4)];
        var scanline = new[] { new byte[checked(width * cinfo.Output_components)] };
        var dst = 0;
        while (cinfo.Output_scanline < cinfo.Output_height)
        {
            cinfo.jpeg_read_scanlines(scanline, 1);
            var row = scanline[0];
            Array.Copy(row, 0, samples, dst, width * 4);
            dst += width * 4;
        }

        cinfo.jpeg_finish_decompress();
        var bitmap = CreateBitmapFromRawData(samples, width, height, bitsPerComponent: 8, "DeviceCMYK", stream);
        if (bitmap == null)
            return null;

        return ResizeDecodedBitmap(
            bitmap,
            Math.Clamp(targetWidth, 1, sourceWidth),
            Math.Clamp(targetHeight, 1, sourceHeight));
    }

    private static bool TryGetAdobeDctColorTransform(byte[] data, out int colorTransform)
    {
        colorTransform = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return false;

        var offset = 2;
        while (offset + 3 < data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < data.Length && data[offset] == 0xFF)
                offset++;
            if (offset >= data.Length)
                return false;

            var marker = data[offset++];
            if (marker == 0xDA || marker == 0xD9)
                return false;
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (offset + 1 >= data.Length)
                return false;

            var segmentLength = (data[offset] << 8) | data[offset + 1];
            if (segmentLength < 2)
                return false;
            var payloadOffset = offset + 2;
            var nextOffset = offset + segmentLength;
            if (nextOffset > data.Length)
                return false;

            if (marker == 0xEE &&
                segmentLength >= 14 &&
                data[payloadOffset] == (byte)'A' &&
                data[payloadOffset + 1] == (byte)'d' &&
                data[payloadOffset + 2] == (byte)'o' &&
                data[payloadOffset + 3] == (byte)'b' &&
                data[payloadOffset + 4] == (byte)'e')
            {
                colorTransform = data[payloadOffset + 11] switch
                {
                    0 => 0,
                    1 => 1,
                    2 => 1,
                    _ => -1
                };
                return colorTransform >= 0;
            }

            offset = nextOffset;
        }

        return false;
    }

    private static string NormalizeDctColorSpaceName(string colorSpace)
        => colorSpace switch
        {
            "RGB" => "DeviceRGB",
            "CMYK" => "DeviceCMYK",
            _ => colorSpace
        };

    private static int ChooseDctScaleDenominator(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        foreach (var denominator in new[] { 8, 4, 2 })
        {
            if ((sourceWidth + denominator - 1) / denominator >= targetWidth &&
                (sourceHeight + denominator - 1) / denominator >= targetHeight)
            {
                return denominator;
            }
        }

        return 1;
    }

    private static SKBitmap? ResizeDecodedBitmap(SKBitmap bitmap, int targetWidth, int targetHeight)
    {
        if (bitmap.Width == targetWidth && bitmap.Height == targetHeight)
            return bitmap;

        try
        {
            var resized = bitmap.Resize(
                new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return resized;
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static byte[] CreateSoftMaskAlphaFrom8Bit(
        byte[] data,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Excise.Core.Primitives.PdfStream maskStream)
    {
        var alpha = new byte[targetWidth * targetHeight];
        var dst = 0;
        for (int y = 0; y < targetHeight; y++)
        {
            var sourceY = MapTargetToSource(y, targetHeight, sourceHeight);
            var sourceRow = sourceY * sourceWidth;
            for (int x = 0; x < targetWidth; x++)
            {
                var sourceX = MapTargetToSource(x, targetWidth, sourceWidth);
                var sourceIndex = sourceRow + sourceX;
                alpha[dst++] = sourceIndex < data.Length
                    ? DecodeSoftMaskSample(maskStream, data[sourceIndex], 8)
                    : DecodeSoftMaskSample(maskStream, 0, 8);
            }
        }

        return alpha;
    }

    private static byte[] CreateSoftMaskAlphaFromSamples(
        int[] data,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Excise.Core.Primitives.PdfStream maskStream)
    {
        var alpha = new byte[targetWidth * targetHeight];
        var dst = 0;
        for (int y = 0; y < targetHeight; y++)
        {
            var sourceY = MapTargetToSource(y, targetHeight, sourceHeight);
            var sourceRow = sourceY * sourceWidth;
            for (int x = 0; x < targetWidth; x++)
            {
                var sourceX = MapTargetToSource(x, targetWidth, sourceWidth);
                var sourceIndex = sourceRow + sourceX;
                alpha[dst++] = sourceIndex < data.Length
                    ? DecodeSoftMaskSample(maskStream, data[sourceIndex], 8)
                    : DecodeSoftMaskSample(maskStream, 0, 8);
            }
        }

        return alpha;
    }

    private static int ReadOneBitImageSample(byte[] data, int width, int x, int y)
    {
        var rowStrideBits = ((width + 7) / 8) * 8;
        var bitIndex = (long)y * rowStrideBits + x;
        var byteIndex = bitIndex / 8;
        if (byteIndex < 0 || byteIndex >= data.LongLength)
            return 0;

        var bitInByte = 7 - (int)(bitIndex % 8);
        return (data[byteIndex] >> bitInByte) & 1;
    }

    private static int MapTargetToSource(int targetPosition, int targetSize, int sourceSize)
        => Math.Clamp((int)(((targetPosition + 0.5) * sourceSize) / targetSize), 0, sourceSize - 1);

    private static SKSizeI GetDecodeSize(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        => new(
            Math.Clamp(targetWidth, 1, sourceWidth),
            Math.Clamp(targetHeight, 1, sourceHeight));

    private static bool TryGetSoftMaskReferenceKey(
        Excise.Core.Primitives.PdfObject maskObj,
        Excise.Core.Primitives.PdfStream maskStream,
        out (int ObjectNumber, int Generation) key)
    {
        if (maskObj is PdfReference reference)
        {
            key = (reference.ObjectNum, reference.Generation);
            return true;
        }

        if (maskStream.ObjectNumber.HasValue)
        {
            key = (maskStream.ObjectNumber.Value, maskStream.GenerationNumber ?? 0);
            return true;
        }

        key = default;
        return false;
    }

    private static byte[] ExtractSoftMaskAlpha(SKBitmap maskBitmap, int width, int height, Excise.Core.Primitives.PdfStream maskStream)
    {
        var alpha = new byte[width * height];
        var pixels = maskBitmap.Pixels;
        if (pixels.Length == 0)
            return alpha;

        for (int y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp((int)((long)y * maskBitmap.Height / height), 0, maskBitmap.Height - 1);
            var sourceRow = sourceY * maskBitmap.Width;
            for (int x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp((int)((long)x * maskBitmap.Width / width), 0, maskBitmap.Width - 1);
                var pixel = pixels[sourceRow + sourceX];
                var luma = (byte)Math.Clamp(
                    (0.299 * pixel.Red) + (0.587 * pixel.Green) + (0.114 * pixel.Blue),
                    0,
                    255);
                alpha[y * width + x] = DecodeSoftMaskSample(maskStream, luma, 8);
            }
        }

        return alpha;
    }

    private static byte DecodeSoftMaskSample(Excise.Core.Primitives.PdfStream maskStream, int sample, int bitsPerComponent)
    {
        var decode = maskStream.GetOptional("Decode") as Excise.Core.Primitives.PdfArray;
        var d0 = decode?.Count >= 2 ? decode.GetNumber(0) : 0.0;
        var d1 = decode?.Count >= 2 ? decode.GetNumber(1) : 1.0;
        var maxSample = Math.Pow(2, bitsPerComponent) - 1;
        var decoded = maxSample > 0
            ? d0 + Math.Clamp(sample, 0, maxSample) * ((d1 - d0) / maxSample)
            : d0;
        return (byte)Math.Clamp((int)Math.Round(decoded * 255), 0, 255);
    }

    private static SKBitmap CreateSoftMaskLumaBitmap(SoftMaskAlpha mask)
    {
        var pixels = new byte[checked(mask.Width * mask.Height * 4)];
        var dst = 0;
        for (int i = 0; i < mask.Data.Length; i++)
        {
            var alpha = mask.Data[i];
            pixels[dst++] = alpha;
            pixels[dst++] = alpha;
            pixels[dst++] = alpha;
            pixels[dst++] = 255;
        }

        return CreateBitmapFromRgbaBytes(mask.Width, mask.Height, pixels)
               ?? new SKBitmap(mask.Width, mask.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
    }

    private SKBitmap? CreateBitmapFromRawData(byte[] data, int width, int height, int bitsPerComponent, string colorSpace, Excise.Core.Primitives.PdfStream stream)
    {
        var isImageMask = stream.GetBool("ImageMask");
        PdfColorSpace? pdfColorSpace = null;
        int componentsPerPixel = 3;

        if (bitsPerComponent == 1 && isImageMask)
            return CreateImageMaskBitmapFromPackedBits(data, width, height, stream);

        var csObj = isImageMask ? null : stream.GetOptional("ColorSpace");
        if (csObj != null)
        {
            pdfColorSpace = ResolveImageColorSpace(csObj);
            componentsPerPixel = pdfColorSpace.Components;
        }
        else if (!isImageMask)
        {
            pdfColorSpace = PdfColorSpace.FromName(colorSpace, _page.Document);
            componentsPerPixel = pdfColorSpace.Components;
        }

        if (componentsPerPixel == 0)
            componentsPerPixel = 3;

        var fastBitmap = TryCreateFast8BitBitmapFromRawData(
            data,
            width,
            height,
            bitsPerComponent,
            pdfColorSpace,
            componentsPerPixel,
            stream);
        if (fastBitmap != null)
            return fastBitmap;

        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = new byte[width * height * 4];

        try
        {
            int srcIndex = 0;
            int dstIndex = 0;
            var pixelValues = new double[componentsPerPixel];
            var imageMaskPaintBits = isImageMask
                ? ResolveImageMaskPaintBits(stream)
                : default;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte r = 0, g = 0, b = 0, a = 255;

                    if (bitsPerComponent > 1 && pdfColorSpace != null)
                    {
                        if (bitsPerComponent == 8 && srcIndex + componentsPerPixel <= data.Length)
                        {
                            for (int i = 0; i < componentsPerPixel; i++)
                                pixelValues[i] = DecodeImageSample(
                                    stream,
                                    pdfColorSpace,
                                    i,
                                    data[srcIndex + i],
                                    bitsPerComponent);
                            srcIndex += componentsPerPixel;

                            var (rd, gd, bd) = pdfColorSpace.ToRgb(pixelValues);
                            r = (byte)Math.Clamp(rd * 255, 0, 255);
                            g = (byte)Math.Clamp(gd * 255, 0, 255);
                            b = (byte)Math.Clamp(bd * 255, 0, 255);
                        }
                        else if (bitsPerComponent != 8)
                        {
                            var rowBits = checked(width * componentsPerPixel * bitsPerComponent);
                            var rowStrideBits = AlignBitsToByte(rowBits);
                            var bitOffset = checked((y * rowStrideBits) + (x * componentsPerPixel * bitsPerComponent));
                            if (bitOffset + (componentsPerPixel * bitsPerComponent) <= data.Length * 8)
                            {
                                for (int i = 0; i < componentsPerPixel; i++)
                                {
                                    var sample = ReadPackedImageSample(
                                        data,
                                        bitOffset + (i * bitsPerComponent),
                                        bitsPerComponent);
                                    pixelValues[i] = DecodeImageSample(
                                        stream,
                                        pdfColorSpace,
                                        i,
                                        sample,
                                        bitsPerComponent);
                                }

                                var (rd, gd, bd) = pdfColorSpace.ToRgb(pixelValues);
                                r = (byte)Math.Clamp(rd * 255, 0, 255);
                                g = (byte)Math.Clamp(gd * 255, 0, 255);
                                b = (byte)Math.Clamp(bd * 255, 0, 255);
                            }
                        }
                    }
                    else if (bitsPerComponent == 1)
                    {
                        // 1-bit monochrome
                        int byteIndex = srcIndex / 8;
                        int bitIndex = 7 - (srcIndex % 8);
                        int bit = 0;
                        if (byteIndex < data.Length)
                        {
                            bit = (data[byteIndex] >> bitIndex) & 1;
                        }

                        if (isImageMask)
                        {
                            r = _state.FillColor.Red;
                            g = _state.FillColor.Green;
                            b = _state.FillColor.Blue;
                            a = imageMaskPaintBits.Paints(bit)
                                ? (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255)
                                : (byte)0;
                        }
                        else if (pdfColorSpace != null)
                        {
                            var sample = DecodeOneBitImageSample(stream, bit);
                            var (rd, gd, bd) = pdfColorSpace.ToRgb(new[] { sample });
                            r = (byte)Math.Clamp(rd * 255, 0, 255);
                            g = (byte)Math.Clamp(gd * 255, 0, 255);
                            b = (byte)Math.Clamp(bd * 255, 0, 255);
                        }
                        srcIndex++;
                    }

                    // RGBA format
                    pixels[dstIndex++] = r;
                    pixels[dstIndex++] = g;
                    pixels[dstIndex++] = b;
                    pixels[dstIndex++] = a;
                }

                // Handle row padding for 1-bit images
                if (bitsPerComponent == 1)
                {
                    srcIndex = ((srcIndex + 7) / 8) * 8; // Align to byte boundary
                }
            }

            var destination = bitmap.GetPixels();
            if (destination == IntPtr.Zero)
            {
                bitmap.Dispose();
                return null;
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, destination, pixels.Length);
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    private static SKBitmap? TryCreateFast8BitBitmapFromRawData(
        byte[] data,
        int width,
        int height,
        int bitsPerComponent,
        PdfColorSpace? colorSpace,
        int componentsPerPixel,
        Excise.Core.Primitives.PdfStream stream)
    {
        if (bitsPerComponent != 8 ||
            colorSpace == null ||
            stream.GetOptional("Decode") != null ||
            width <= 0 ||
            height <= 0)
        {
            return null;
        }

        var expectedPixels = checked((long)width * height);
        var requiredBytes = expectedPixels * componentsPerPixel;
        if (requiredBytes > data.LongLength)
            return null;

        return colorSpace.Type switch
        {
            PdfColorSpaceType.DeviceGray when componentsPerPixel == 1 =>
                CreateFastGrayBitmap(data, width, height),
            PdfColorSpaceType.DeviceRGB when componentsPerPixel == 3 =>
                CreateFastRgbBitmap(data, width, height),
            PdfColorSpaceType.DeviceCMYK when componentsPerPixel == 4 =>
                CreateFastCmykBitmap(data, width, height, colorSpace),
            _ => null
        };
    }

    private static SKBitmap? CreateFastGrayBitmap(byte[] data, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            var gray = data[src++];
            pixels[dst++] = gray;
            pixels[dst++] = gray;
            pixels[dst++] = gray;
            pixels[dst++] = 255;
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static SKBitmap? CreateFastRgbBitmap(byte[] data, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            pixels[dst++] = data[src++];
            pixels[dst++] = data[src++];
            pixels[dst++] = data[src++];
            pixels[dst++] = 255;
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static SKBitmap? CreateFastCmykBitmap(byte[] data, int width, int height, PdfColorSpace colorSpace)
    {
        var pixels = new byte[width * height * 4];
        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            var c = data[src++] / 255.0;
            var m = data[src++] / 255.0;
            var y = data[src++] / 255.0;
            var k = data[src++] / 255.0;
            var (r, g, b) = colorSpace.ToRgb(new[] { c, m, y, k });
            pixels[dst++] = (byte)Math.Clamp(r * 255, 0, 255);
            pixels[dst++] = (byte)Math.Clamp(g * 255, 0, 255);
            pixels[dst++] = (byte)Math.Clamp(b * 255, 0, 255);
            pixels[dst++] = 255;
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static SKBitmap? CreateBitmapFromRgbaBytes(
        int width,
        int height,
        byte[] pixels,
        SKAlphaType alphaType = SKAlphaType.Premul)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, alphaType);
        try
        {
            var destination = bitmap.GetPixels();
            if (destination == IntPtr.Zero)
            {
                bitmap.Dispose();
                return null;
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, destination, pixels.Length);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }
    }

    private SKBitmap? CreateImageMaskBitmapFromPackedBits(
        byte[] data,
        int width,
        int height,
        Excise.Core.Primitives.PdfStream stream)
    {
        if (width <= 0 || height <= 0)
            return null;

        var pixels = new byte[width * height * 4];
        var fillAlpha = (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255);
        var paintBits = ResolveImageMaskPaintBits(stream);
        var rowBytes = (width + 7) / 8;
        var dst = 0;

        for (int y = 0; y < height; y++)
        {
            var rowOffset = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                var byteIndex = rowOffset + (x >> 3);
                var bit = byteIndex < data.Length
                    ? (data[byteIndex] >> (7 - (x & 7))) & 1
                    : 0;
                var paint = paintBits.Paints(bit);

                pixels[dst++] = paint ? _state.FillColor.Red : (byte)0;
                pixels[dst++] = paint ? _state.FillColor.Green : (byte)0;
                pixels[dst++] = paint ? _state.FillColor.Blue : (byte)0;
                pixels[dst++] = paint ? fillAlpha : (byte)0;
            }
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private SKBitmap? CreateImageMaskStencilBitmap(
        byte[] data,
        int width,
        int height,
        Excise.Core.Primitives.PdfStream stream)
    {
        if (width <= 0 || height <= 0)
            return null;

        var pixels = new byte[width * height * 4];
        var paintBits = ResolveImageMaskPaintBits(stream);
        var rowBytes = (width + 7) / 8;
        var dst = 0;

        for (int y = 0; y < height; y++)
        {
            var rowOffset = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                var byteIndex = rowOffset + (x >> 3);
                var bit = byteIndex < data.Length
                    ? (data[byteIndex] >> (7 - (x & 7))) & 1
                    : 0;
                var paint = paintBits.Paints(bit);
                var alpha = paint ? (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255) : (byte)0;

                pixels[dst++] = 255;
                pixels[dst++] = 255;
                pixels[dst++] = 255;
                pixels[dst++] = alpha;
            }
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static SKBitmap? CreateExplicitImageMaskBitmap(
        Excise.Core.Primitives.PdfStream stream,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        var bitsPerComponent = stream.GetInt("BitsPerComponent", 1);
        if (bitsPerComponent != 1)
            return null;

        var data = stream.DecodedData;
        var pixels = new byte[width * height * 4];
        var dst = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var bit = ReadOneBitImageSample(data, width, x, y);
                // Explicit image /Mask streams use the same decoded stencil
                // convention to decide which source-image pixels remain
                // visible, but the result becomes alpha instead of current
                // fill color.
                var opaque = DecodeImageMaskBit(stream, bit);
                var alpha = opaque ? (byte)255 : (byte)0;

                pixels[dst++] = 255;
                pixels[dst++] = 255;
                pixels[dst++] = 255;
                pixels[dst++] = alpha;
            }
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static double DecodeOneBitImageSample(Excise.Core.Primitives.PdfStream stream, int bit)
    {
        var decode = stream.GetOptional("Decode") as Excise.Core.Primitives.PdfArray;
        var d0 = decode?.Count >= 2 ? decode.GetNumber(0) : 0.0;
        var d1 = decode?.Count >= 2 ? decode.GetNumber(1) : 1.0;
        return bit == 0 ? d0 : d1;
    }

    private static int AlignBitsToByte(int bitCount)
        => ((bitCount + 7) / 8) * 8;

    private static int ReadPackedImageSample(byte[] data, int bitOffset, int bitsPerComponent)
    {
        var sample = 0;
        for (var i = 0; i < bitsPerComponent; i++)
        {
            var absoluteBit = bitOffset + i;
            var byteIndex = absoluteBit / 8;
            if (byteIndex >= data.Length)
                break;

            var bitIndex = 7 - (absoluteBit % 8);
            sample = (sample << 1) | ((data[byteIndex] >> bitIndex) & 1);
        }

        return sample;
    }

    private static double DecodeImageSample(
        Excise.Core.Primitives.PdfStream stream,
        PdfColorSpace colorSpace,
        int componentIndex,
        int sample,
        int bitsPerComponent)
    {
        var decode = stream.GetOptional("Decode") as Excise.Core.Primitives.PdfArray;
        var offset = componentIndex * 2;
        var maxSample = Math.Pow(2, bitsPerComponent) - 1;
        if (decode != null && decode.Count >= offset + 2)
        {
            var d0 = decode.GetNumber(offset);
            var d1 = decode.GetNumber(offset + 1);
            return maxSample > 0
                ? d0 + sample * ((d1 - d0) / maxSample)
                : d0;
        }

        if (colorSpace.Type == PdfColorSpaceType.Indexed)
            return sample;

        var normalizedByte = maxSample > 0
            ? (byte)Math.Clamp((int)Math.Round(sample * (255.0 / maxSample)), 0, 255)
            : (byte)0;
        return colorSpace.DecodeSampleByte(componentIndex, normalizedByte);
    }

    private static bool DecodeImageMaskBit(Excise.Core.Primitives.PdfStream stream, int bit)
    {
        var decode = stream.GetOptional("Decode") as Excise.Core.Primitives.PdfArray;
        var d0 = decode?.Count >= 2 ? decode.GetNumber(0) : 0.0;
        var d1 = decode?.Count >= 2 ? decode.GetNumber(1) : 1.0;
        // Image masks are stencils: decoded 0 paints with the current color,
        // decoded 1 is transparent. /Decode [1 0] reverses the source-bit
        // polarity while preserving that decoded-value convention.
        return (bit == 0 ? d0 : d1) < 0.5;
    }

    private static ImageMaskPaintBits ResolveImageMaskPaintBits(Excise.Core.Primitives.PdfStream stream)
    {
        if (HasExplicitImageMaskDecode(stream))
            return new ImageMaskPaintBits(DecodeImageMaskBit(stream, 0), DecodeImageMaskBit(stream, 1));

        if (TryGetCcittImageBlackIsOne(stream, out var blackIsOne))
            return blackIsOne
                ? new ImageMaskPaintBits(PaintWhenZero: false, PaintWhenOne: true)
                : new ImageMaskPaintBits(PaintWhenZero: true, PaintWhenOne: false);

        if (HasStreamFilter(stream, "JBIG2Decode"))
        {
            // The JBIG2 decoder returns normalized PDF one-bit image samples:
            // 1 is white/background and 0 is black/foreground. For an image
            // mask without an explicit /Decode, the foreground is the stencil.
            return new ImageMaskPaintBits(PaintWhenZero: true, PaintWhenOne: false);
        }

        return new ImageMaskPaintBits(DecodeImageMaskBit(stream, 0), DecodeImageMaskBit(stream, 1));
    }

    private static bool HasExplicitImageMaskDecode(Excise.Core.Primitives.PdfStream stream)
        => stream.GetOptional("Decode") is Excise.Core.Primitives.PdfArray decode && decode.Count >= 2;

    private static bool TryGetCcittImageBlackIsOne(
        Excise.Core.Primitives.PdfStream stream,
        out bool blackIsOne)
    {
        var filters = stream.Filters;
        var decodeParams = stream.DecodeParams;
        for (int i = filters.Count - 1; i >= 0; i--)
        {
            if (!IsNamedFilter(filters[i], "CCITTFaxDecode"))
                continue;

            var parms = i < decodeParams.Count
                ? decodeParams[i]
                : decodeParams.Count == 1 ? decodeParams[0] : null;
            blackIsOne = parms?.GetBool("BlackIs1", false) ?? false;
            return true;
        }

        blackIsOne = false;
        return false;
    }

    private static bool HasStreamFilter(Excise.Core.Primitives.PdfStream stream, string filterName)
    {
        foreach (var filter in stream.Filters)
        {
            if (IsNamedFilter(filter, filterName))
                return true;
        }

        return false;
    }

    private static bool IsNamedFilter(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.Ordinal) ||
           (string.Equals(expected, "CCITTFaxDecode", StringComparison.Ordinal) &&
            string.Equals(actual, "CCF", StringComparison.Ordinal)) ||
           (string.Equals(expected, "JBIG2Decode", StringComparison.Ordinal) &&
            string.Equals(actual, "JBIG2", StringComparison.Ordinal));

    private readonly record struct ImageMaskPaintBits(bool PaintWhenZero, bool PaintWhenOne)
    {
        public bool Paints(int bit) => bit == 0 ? PaintWhenZero : PaintWhenOne;
    }

    private PdfColorSpace ResolveImageColorSpace(Excise.Core.Primitives.PdfObject colorSpaceObject)
    {
        if (colorSpaceObject is Excise.Core.Primitives.PdfName name)
            return ResolveColorSpace(name.Value) ?? PdfColorSpace.Parse(colorSpaceObject, _page.Document);

        return PdfColorSpace.Parse(colorSpaceObject, _page.Document);
    }

    private string ResolveImageColorSpaceFamilyName(Excise.Core.Primitives.PdfStream stream)
    {
        var colorSpaceObject = stream.GetOptional("ColorSpace") ?? stream.GetOptional("CS");
        if (colorSpaceObject == null)
            return "DeviceRGB";

        try
        {
            var colorSpace = ResolveImageColorSpace(colorSpaceObject);
            return colorSpace.Type switch
            {
                PdfColorSpaceType.DeviceGray or PdfColorSpaceType.CalGray => "DeviceGray",
                PdfColorSpaceType.DeviceCMYK => "DeviceCMYK",
                PdfColorSpaceType.DeviceRGB or PdfColorSpaceType.CalRGB or PdfColorSpaceType.Lab => "DeviceRGB",
                PdfColorSpaceType.ICCBased when colorSpace.Components == 4 => "DeviceCMYK",
                PdfColorSpaceType.ICCBased when colorSpace.Components == 1 => "DeviceGray",
                PdfColorSpaceType.ICCBased => "DeviceRGB",
                _ => colorSpaceObject is Excise.Core.Primitives.PdfName name ? name.Value : "DeviceRGB"
            };
        }
        catch
        {
            return colorSpaceObject is Excise.Core.Primitives.PdfName name ? name.Value : "DeviceRGB";
        }
    }

    /// <summary>
    /// Wrap SKBitmap.Decode so any exception (ArgumentNullException
    /// when SkiaSharp can't find a codec for this image format,
    /// AccessViolationException on truncated/corrupt input, etc.)
    /// returns null instead of propagating up and crashing the
    /// page render. Found by the pdf.js corpus differential —
    /// 8 fixtures with JPEG2000 inline images caused
    /// "Value cannot be null. (Parameter 'codec')" because
    /// SkiaSharp's Linux build ships without a JPX codec.
    /// </summary>
    private static SKBitmap? SafeDecode(byte[]? bytes, SKSizeI? targetSize = null)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            if (targetSize is { Width: > 0, Height: > 0 } size)
            {
                var scaled = SKBitmap.Decode(
                    bytes,
                    new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                if (scaled != null)
                    return scaled;
            }

            return SKBitmap.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }
}

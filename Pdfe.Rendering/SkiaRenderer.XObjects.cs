using Pdfe.Core.Primitives;
using Pdfe.Core.Text;
using SkiaSharp;

namespace Pdfe.Rendering;

internal partial class RenderContext
{
    #region XObject Rendering (Do operator)

    private void RenderXObject(string nameOperand)
    {
        // Remove leading / if present
        var name = nameOperand.TrimStart('/');
        var xobj = ResolveXObjectFromActiveResources(name);
        if (xobj == null)
            return;

        if (xobj is not Pdfe.Core.Primitives.PdfStream stream)
            return;

        if (stream.GetOptional("OC") is { } ocObject && !IsOptionalContentObjectVisible(ocObject))
            return;

        var subtype = stream.GetNameOrNull("Subtype");
        switch (subtype)
        {
            case "Image":
                RenderImageXObject(stream);
                break;
            case "Form":
                RenderFormXObjectAtInvocation(stream);
                break;
        }
    }

    private void RenderFormXObjectAtInvocation(Pdfe.Core.Primitives.PdfStream formStream)
    {
        var isTransparencyGroup = IsTransparencyGroupForm(formStream);
        if (!isTransparencyGroup)
        {
            RenderFormXObject(formStream);
            return;
        }

        var invocationState = _state.Clone();
        var group = ResolveTransparencyGroup(formStream);
        var isDeviceCmykGroup = SkiaRenderer.IsDeviceCmykTransparencyGroup(group, _page.Document);
        if (isDeviceCmykGroup && invocationState.SoftMask == null)
        {
            if (TryRenderDeviceCmykFormGroup(formStream, group, invocationState))
                return;

            var savedState = _state;
            var savedBlendOverride = _deviceCmykGroupCompositeBlendOverride;
            _deviceCmykTransparencyGroupDepth++;
            try
            {
                _state = invocationState.Clone();
                _deviceCmykGroupCompositeBlendOverride = invocationState.BlendMode;
                RenderFormXObject(formStream);
            }
            finally
            {
                _state = savedState;
                _deviceCmykGroupCompositeBlendOverride = savedBlendOverride;
                _deviceCmykTransparencyGroupDepth--;
            }
            return;
        }

        using var paint = new SKPaint
        {
            BlendMode = invocationState.BlendMode,
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(invocationState.FillAlpha * 255, 0, 255)),
            IsAntialias = _options.AntiAlias
        };

        var layerBounds = GetFormInvocationBounds(formStream);
        void DrawFormContent()
        {
            var savedState = _state;
            try
            {
                _state = invocationState.Clone();
                _state.BlendMode = SKBlendMode.SrcOver;
                _state.FillAlpha = 1;
                _state.StrokeAlpha = 1;
                _state.SoftMask = null;

                RenderFormXObject(formStream);
            }
            finally
            {
                _state = savedState;
            }
        }

        if (invocationState.SoftMask != null)
        {
            var savedState = _state;
            try
            {
                _state = invocationState.Clone();
                RenderWithCurrentSoftMask(DrawFormContent, paint, layerBounds);
            }
            finally
            {
                _state = savedState;
            }
            return;
        }

        if (!TryGetLayerBounds(layerBounds, out var bounds))
        {
            DrawFormContent();
            return;
        }

        _canvas.SaveLayer(bounds, paint);
        try
        {
            DrawFormContent();
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private bool IsTransparencyGroupForm(Pdfe.Core.Primitives.PdfStream formStream)
    {
        var group = ResolveTransparencyGroup(formStream);
        return string.Equals(group?.GetNameOrNull("S"), "Transparency", StringComparison.Ordinal);
    }

    private Pdfe.Core.Primitives.PdfDictionary? ResolveTransparencyGroup(Pdfe.Core.Primitives.PdfStream formStream)
    {
        var groupObj = formStream.GetOptional("Group");
        return groupObj != null
            ? _page.Document.Resolve(groupObj) as Pdfe.Core.Primitives.PdfDictionary
            : null;
    }

    private SKRect? GetFormInvocationBounds(Pdfe.Core.Primitives.PdfStream formStream)
    {
        var bbox = ResolveArray(formStream, "BBox");
        if (bbox == null || bbox.Count < 4)
            return null;

        var bounds = new SKRect(
            (float)Math.Min(ArrayNumberOrDefault(bbox, 0), ArrayNumberOrDefault(bbox, 2)),
            (float)Math.Min(ArrayNumberOrDefault(bbox, 1), ArrayNumberOrDefault(bbox, 3)),
            (float)Math.Max(ArrayNumberOrDefault(bbox, 0), ArrayNumberOrDefault(bbox, 2)),
            (float)Math.Max(ArrayNumberOrDefault(bbox, 1), ArrayNumberOrDefault(bbox, 3)));
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        var matrix = GetMatrix(formStream.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
        return MapRect(matrix, bounds);
    }

    private bool TryRenderDeviceCmykFormGroup(
        Pdfe.Core.Primitives.PdfStream formStream,
        Pdfe.Core.Primitives.PdfDictionary? group,
        GraphicsState invocationState)
    {
        if (group == null ||
            _rootBitmap == null ||
            _deviceCmykBackdrop == null)
        {
            return false;
        }

        var invocationBounds = GetFormInvocationBounds(formStream);
        if (invocationBounds == null)
            return false;

        var parentMatrix = _canvas.TotalMatrix;
        var deviceBounds = parentMatrix.MapRect(invocationBounds.Value);
        var left = Math.Clamp((int)Math.Floor(deviceBounds.Left) - 1, 0, _rootBitmap.Width);
        var top = Math.Clamp((int)Math.Floor(deviceBounds.Top) - 1, 0, _rootBitmap.Height);
        var right = Math.Clamp((int)Math.Ceiling(deviceBounds.Right) + 1, 0, _rootBitmap.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(deviceBounds.Bottom) + 1, 0, _rootBitmap.Height);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
            return true;

        var pixels = (long)width * height;
        if (pixels > _options.MaxPixelCount)
            return false;

        using var groupBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var groupCanvas = new SKCanvas(groupBitmap))
        {
            groupCanvas.Clear(SKColors.Transparent);
            var groupMatrix = parentMatrix;
            groupMatrix.TransX -= left;
            groupMatrix.TransY -= top;
            groupCanvas.SetMatrix(groupMatrix);

            var child = new RenderContext(
                groupCanvas,
                _page,
                _options,
                _cancellationToken,
                groupBitmap,
                startsInDeviceCmykTransparencyGroup: true);
            child._resourcesStack.Push(_page.Resources);
            child._state = invocationState.Clone();
            child._state.BlendMode = SKBlendMode.SrcOver;
            child._state.SoftMask = null;
            var isIsolated = group.GetBool("I");
            var isKnockout = group.GetBool("K");
            child._deviceCmykPreserveZeroAlphaShape = _deviceCmykKnockoutGroupDepth > 0 && !isIsolated;

            var parentBackdropForChild = _deviceCmykKnockoutGroupDepth > 0 &&
                                         _deviceCmykKnockoutInitialBackdrop != null
                ? _deviceCmykKnockoutInitialBackdrop
                : _deviceCmykBackdrop;
            if (!isIsolated && child._deviceCmykBackdrop != null && parentBackdropForChild != null)
                SeedDeviceCmykGroupBackdrop(child._deviceCmykBackdrop, parentBackdropForChild, left, top, width, height);

            if (isKnockout)
            {
                child._deviceCmykKnockoutGroupDepth++;
                child._deviceCmykDirectBlendFunctionDepth++;
                child._deviceCmykKnockoutInitialBackdrop = child._deviceCmykBackdrop?.Clone();
            }
            if (isIsolated)
                child._deviceCmykIsolatedGroupDepth++;

            try
            {
                child.RenderFormXObject(formStream);
            }
            finally
            {
                child._resourcesStack.Clear();
                child.DisposeOwnedResources();
            }

            if (child._deviceCmykBackdrop == null)
                return false;

            CompositeDeviceCmykGroupBitmap(groupBitmap, child._deviceCmykBackdrop, left, top, invocationState.BlendMode);
        }

        return true;
    }

    private static void SeedDeviceCmykGroupBackdrop(
        DeviceCmykBackdrop groupBackdrop,
        DeviceCmykBackdrop sourceBackdrop,
        int left,
        int top,
        int width,
        int height)
    {
        for (var y = 0; y < height; y++)
        {
            var parentY = top + y;
            for (var x = 0; x < width; x++)
            {
                var parentX = left + x;
                groupBackdrop.Set(x, y, sourceBackdrop.Get(parentX, parentY));
            }
        }
    }

    private void CompositeDeviceCmykGroupBitmap(
        SKBitmap groupBitmap,
        DeviceCmykBackdrop groupBackdrop,
        int left,
        int top,
        SKBlendMode invocationBlendMode)
    {
        if (_rootBitmap == null || _deviceCmykBackdrop == null)
            return;

        var isNormalBlend = invocationBlendMode == SKBlendMode.SrcOver;
        PdfSeparableBlendMode blend = default;
        if (!isNormalBlend && !TryMapSkiaBlendToPdfBlend(invocationBlendMode, out blend))
            return;
        var useDirectBlendFunctions =
            (_deviceCmykDirectBlendFunctionDepth > 0 &&
             !isNormalBlend &&
             UsesDirectDeviceCmykKnockoutBlend(blend)) ||
            (_deviceCmykIsolatedGroupDepth > 0 &&
             !isNormalBlend &&
             blend is PdfSeparableBlendMode.Lighten or
                 PdfSeparableBlendMode.Screen or
                 PdfSeparableBlendMode.ColorDodge);

        for (var y = 0; y < groupBitmap.Height; y++)
        {
            var parentY = top + y;
            if (parentY < 0 || parentY >= _rootBitmap.Height)
                continue;

            for (var x = 0; x < groupBitmap.Width; x++)
            {
                var alpha = groupBitmap.GetPixel(x, y).Alpha / 255.0;
                if (alpha <= 0)
                    continue;

                var parentX = left + x;
                if (parentX < 0 || parentX >= _rootBitmap.Width)
                    continue;

                var dst = _rootBitmap.GetPixel(parentX, parentY);
                if (_deviceCmykKnockoutGroupDepth > 0)
                {
                    var initialBackdrop = _deviceCmykKnockoutInitialBackdrop?.Get(parentX, parentY)
                                          ?? new DeviceCmykColor(0, 0, 0, 0);
                    _deviceCmykBackdrop.Set(parentX, parentY, initialBackdrop);
                    var (initialR, initialG, initialB) = Pdfe.Core.ColorSpaces.PdfColorSpace.ConvertDeviceCmykToRgb(
                        initialBackdrop.C,
                        initialBackdrop.M,
                        initialBackdrop.Y,
                        initialBackdrop.K);
                    dst = new SKColor(
                        ToByte(initialR),
                        ToByte(initialG),
                        ToByte(initialB),
                        0);
                    _rootBitmap.SetPixel(parentX, parentY, dst);
                }

                var source = groupBackdrop.Get(x, y);
                var backdrop = _deviceCmykBackdrop.Get(parentX, parentY);
                var blended = isNormalBlend
                    ? source
                    : useDirectBlendFunctions
                        ? BlendDeviceCmykDirect(backdrop, source, blend)
                        : BlendDeviceCmyk(backdrop, source, blend);
                _deviceCmykBackdrop.CompositeSourceOver(parentX, parentY, blended, alpha);
                var output = _deviceCmykBackdrop.Get(parentX, parentY);
                var (r, g, b) = Pdfe.Core.ColorSpaces.PdfColorSpace.ConvertDeviceCmykToRgb(
                    output.C,
                    output.M,
                    output.Y,
                    output.K);
                var dstAlpha = dst.Alpha / 255.0;
                var outAlpha = alpha + (dstAlpha * (1 - alpha));
                _rootBitmap.SetPixel(parentX, parentY, new SKColor(
                    ToByte(r),
                    ToByte(g),
                    ToByte(b),
                    ToByte(outAlpha)));
            }
        }
    }

    private void RenderFormXObject(Pdfe.Core.Primitives.PdfStream formStream)
    {
        // Cycle detection: a Form XObject that ends up invoking itself
        // (transitively) would otherwise recurse until the .NET stack
        // overflows, which is uncatchable and aborts the whole process.
        if (!_formXObjectStack.Add(formStream)) return;
        if (_formXObjectDepth >= MaxFormXObjectDepth)
        {
            _formXObjectStack.Remove(formStream);
            return;
        }
        _formXObjectDepth++;

        try
        {
            RenderFormXObjectInner(formStream);
        }
        finally
        {
            _formXObjectStack.Remove(formStream);
            _formXObjectDepth--;
        }
    }

    private void RenderFormXObjectInner(Pdfe.Core.Primitives.PdfStream formStream)
    {
        // Form XObjects contain their own content stream
        // Get the form's content and render it recursively
        var formContent = formStream.DecodedData;
        if (formContent.Length == 0)
            return;

        var savedCanvasCount = _canvas.SaveCount;
        var savedStateStack = SnapshotGraphicsStateStack();
        var savedState = _state.Clone();
        var savedTextState = _textState.Clone();
        var savedInTextBlock = _inTextBlock;
        var savedCurrentPath = _currentPath;
        var savedPendingClipEvenOdd = _pendingClipEvenOdd;
        var savedPendingTextClipPath = _pendingTextClipPath;

        _currentPath = null;
        _pendingClipEvenOdd = null;
        _pendingTextClipPath = null;
        _canvas.Save();

        // Push the form's own /Resources so font / XObject lookups inside
        // its content stream resolve against the form's resource dict
        // first (with fallback to outer scopes via the resources stack).
        // PDF 32000-2 §7.8.3: a Form XObject inherits resources from its
        // page, so falling through is required for forms that omit names
        // their content references.
        var formResources = formStream.GetOptional("Resources") is { } resObj
            ? _page.Document.Resolve(resObj) as Pdfe.Core.Primitives.PdfDictionary
            : null;
        _resourcesStack.Push(formResources);

        try
        {
            // Apply the form's transformation matrix if present
            var matrixArray = formStream.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray;
            if (matrixArray != null && matrixArray.Count >= 6)
            {
                var matrix = GetMatrix(matrixArray);
                _canvas.Concat(in matrix);
                _state.CurrentTransform = Concat(_state.CurrentTransform, matrix);
            }

            var bboxArray = ResolveArray(formStream, "BBox");
            if (bboxArray != null && bboxArray.Count >= 4)
            {
                var x0 = (float)ArrayNumberOrDefault(bboxArray, 0);
                var y0 = (float)ArrayNumberOrDefault(bboxArray, 1);
                var x1 = (float)ArrayNumberOrDefault(bboxArray, 2);
                var y1 = (float)ArrayNumberOrDefault(bboxArray, 3);
                var bounds = new SKRect(
                    Math.Min(x0, x1),
                    Math.Min(y0, y1),
                    Math.Max(x0, x1),
                    Math.Max(y0, y1));
                if (bounds.Width > 0 && bounds.Height > 0)
                    _canvas.ClipRect(bounds, SKClipOperation.Intersect, _options.AntiAlias);
            }

            // Parse and render the form's content stream through the same
            // typed operator path as normal page content. Resource resolution
            // stays on the renderer's stack, so local form resources still
            // override inherited page resources during execution.
            ExecuteContentBytes(formContent);
        }
        finally
        {
            _currentPath?.Dispose();
            _pendingTextClipPath?.Dispose();
            RestoreGraphicsStateStack(savedStateStack);
            _state = savedState;
            _textState = savedTextState;
            _inTextBlock = savedInTextBlock;
            _currentPath = savedCurrentPath;
            _pendingClipEvenOdd = savedPendingClipEvenOdd;
            _pendingTextClipPath = savedPendingTextClipPath;
            _resourcesStack.Pop();
            _canvas.RestoreToCount(savedCanvasCount);
        }
    }

    private GraphicsState[] SnapshotGraphicsStateStack()
    {
        var snapshot = _stateStack.ToArray();
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i] = snapshot[i].Clone();
        return snapshot;
    }

    private void RestoreGraphicsStateStack(GraphicsState[] snapshot)
    {
        _stateStack.Clear();
        for (var i = snapshot.Length - 1; i >= 0; i--)
            _stateStack.Push(snapshot[i]);
    }

    private CurrentFontState SnapshotCurrentFontState() => new(
        _currentFontWidths,
        _currentFontFirstChar,
        _currentFontMissingWidth,
        _currentCodeToUnicode,
        _currentUnicodeToCode,
        _currentCodeToGlyphName,
        _currentFontDict,
        _currentTypeface,
        _currentByteToGlyph,
        _currentFontEncoding,
        _currentFontIsType0,
        _currentFontIsType3,
        _currentFontHasEmbeddedProgram,
        _currentCidWidths,
        _currentCidDefaultWidth,
        _currentCidUseUnicodeCmap,
        _currentCidEncodingCMap,
        _currentCidToGidMap,
        _currentCffCidToGlyph);

    private void RestoreCurrentFontState(CurrentFontState state)
    {
        _currentFontWidths = state.FontWidths;
        _currentFontFirstChar = state.FontFirstChar;
        _currentFontMissingWidth = state.FontMissingWidth;
        _currentCodeToUnicode = state.CodeToUnicode;
        _currentUnicodeToCode = state.UnicodeToCode;
        _currentCodeToGlyphName = state.CodeToGlyphName;
        _currentFontDict = state.FontDict;
        _currentTypeface = state.Typeface;
        _currentByteToGlyph = state.ByteToGlyph;
        _currentFontEncoding = state.FontEncoding;
        _currentFontIsType0 = state.FontIsType0;
        _currentFontIsType3 = state.FontIsType3;
        _currentFontHasEmbeddedProgram = state.FontHasEmbeddedProgram;
        _currentCidWidths = state.CidWidths;
        _currentCidDefaultWidth = state.CidDefaultWidth;
        _currentCidUseUnicodeCmap = state.CidUseUnicodeCmap;
        _currentCidEncodingCMap = state.CidEncodingCMap;
        _currentCidToGidMap = state.CidToGidMap;
        _currentCffCidToGlyph = state.CffCidToGlyph;
    }

    private sealed record CurrentFontState(
        float[]? FontWidths,
        int FontFirstChar,
        float FontMissingWidth,
        char[]? CodeToUnicode,
        Dictionary<char, byte>? UnicodeToCode,
        string?[]? CodeToGlyphName,
        Pdfe.Core.Primitives.PdfDictionary? FontDict,
        SKTypeface? Typeface,
        ushort[]? ByteToGlyph,
        string FontEncoding,
        bool FontIsType0,
        bool FontIsType3,
        bool FontHasEmbeddedProgram,
        Dictionary<int, float>? CidWidths,
        float CidDefaultWidth,
        bool CidUseUnicodeCmap,
        CidCMap? CidEncodingCMap,
        ushort[]? CidToGidMap,
        Dictionary<int, int>? CffCidToGlyph);

    #endregion
}

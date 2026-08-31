using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Rendering.Transparency;
using SkiaSharp;

namespace Excise.Rendering;

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

        if (xobj is not Excise.Core.Primitives.PdfStream stream)
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

    private void RenderFormXObjectAtInvocation(Excise.Core.Primitives.PdfStream formStream)
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
                RenderWithCurrentSoftMask(
                    DrawFormContent,
                    paint,
                    layerBounds,
                    seedBackdrop: group?.GetBool("I") == false);
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

    private bool IsTransparencyGroupForm(Excise.Core.Primitives.PdfStream formStream)
    {
        var group = ResolveTransparencyGroup(formStream);
        return string.Equals(group?.GetNameOrNull("S"), "Transparency", StringComparison.Ordinal);
    }

    private Excise.Core.Primitives.PdfDictionary? ResolveTransparencyGroup(Excise.Core.Primitives.PdfStream formStream)
    {
        var groupObj = formStream.GetOptional("Group");
        return groupObj != null
            ? _page.Document.Resolve(groupObj) as Excise.Core.Primitives.PdfDictionary
            : null;
    }

    private SKRect? GetFormInvocationBounds(Excise.Core.Primitives.PdfStream formStream)
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

        var matrix = GetMatrix(formStream.GetOptional("Matrix") as Excise.Core.Primitives.PdfArray);
        return MapRect(matrix, bounds);
    }

    private bool TryRenderDeviceCmykFormGroup(
        Excise.Core.Primitives.PdfStream formStream,
        Excise.Core.Primitives.PdfDictionary? group,
        GraphicsState invocationState)
    {
        if (group == null ||
            _rootBitmap == null ||
            _deviceCmyk.Backdrop == null)
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
                _resourceScope,
                _cancellationToken,
                groupBitmap,
                startsInDeviceCmykTransparencyGroup: true);
            child._resourcesStack.Push(_page.Resources);
            child._state = invocationState.Clone();
            child._state.BlendMode = SKBlendMode.SrcOver;
            child._state.SoftMask = null;
            var isIsolated = group.GetBool("I");
            var isKnockout = group.GetBool("K");

            if (!isIsolated && invocationState.BlendMode != SKBlendMode.SrcOver)
                SyncDeviceCmykBackdropFromRootBitmap(left, top, width, height);

            child._deviceCmyk.EnterChildGroup(new DeviceCmykChildGroupRequest(
                isIsolated,
                isKnockout,
                _deviceCmyk.IsInKnockoutGroup,
                _deviceCmyk.SelectBackdropForChild(),
                left,
                top,
                width,
                height));

            DeviceCmykChildGroupResult childResult;
            try
            {
                child.RenderFormXObject(formStream);
                childResult = child._deviceCmyk.CompleteChildGroup(
                    () => child.SyncDeviceCmykBackdropFromRootBitmap(0, 0, width, height));
            }
            finally
            {
                child._resourcesStack.Clear();
                child.DisposeOwnedResources();
            }

            if (!childResult.IsAvailable)
                return false;

            var groupInvocationAlpha = _deviceCmyk.IsInKnockoutGroup
                ? 1
                : invocationState.FillAlpha;
            CompositeDeviceCmykGroupBitmap(
                groupBitmap,
                childResult.Backdrop!,
                left,
                top,
                invocationState.BlendMode,
                groupInvocationAlpha);
        }

        return true;
    }

    private void SyncDeviceCmykBackdropFromRootBitmap(int left, int top, int width, int height)
    {
        if (_rootBitmap == null || _deviceCmyk.Backdrop == null)
            return;

        for (var y = 0; y < height; y++)
        {
            var parentY = top + y;
            if (parentY < 0 || parentY >= _rootBitmap.Height)
                continue;

            for (var x = 0; x < width; x++)
            {
                var parentX = left + x;
                if (parentX < 0 || parentX >= _rootBitmap.Width)
                    continue;

                var pixel = _rootBitmap.GetPixel(parentX, parentY);
                var retained = _deviceCmyk.Backdrop.Get(parentX, parentY);
                var (retainedR, retainedG, retainedB) = DeviceCmykToRgb(retained);
                if (Math.Abs(pixel.Red - ToByte(retainedR)) +
                    Math.Abs(pixel.Green - ToByte(retainedG)) +
                    Math.Abs(pixel.Blue - ToByte(retainedB)) <= 12)
                {
                    continue;
                }

                var alpha = pixel.Alpha / 255.0;
                var r = (pixel.Red / 255.0 * alpha) + (1 - alpha);
                var g = (pixel.Green / 255.0 * alpha) + (1 - alpha);
                var b = (pixel.Blue / 255.0 * alpha) + (1 - alpha);
                _deviceCmyk.Backdrop.Set(parentX, parentY, RgbToDeviceCmyk(r, g, b), alpha);
            }
        }
    }

    private void CompositeDeviceCmykGroupBitmap(
        SKBitmap groupBitmap,
        DeviceCmykBackdrop groupBackdrop,
        int left,
        int top,
        SKBlendMode invocationBlendMode,
        float invocationAlpha)
    {
        if (_rootBitmap == null || _deviceCmyk.Backdrop == null)
            return;

        var isNormalBlend = invocationBlendMode == SKBlendMode.SrcOver;
        PdfSeparableBlendMode blend = default;
        if (!isNormalBlend && !TryMapSkiaBlendToPdfBlend(invocationBlendMode, out blend))
            return;
        var useDirectBlendFunctions =
            // Match the path-painting fast path: isolated CMYK groups keep direct
            // handling for these retained-backdrop modes, but knockout compositing
            // uses the subtractive DeviceCMYK blend path.
            _deviceCmyk.IsInIsolatedGroup &&
            !isNormalBlend &&
            blend is PdfSeparableBlendMode.Lighten or
                PdfSeparableBlendMode.Screen or
                PdfSeparableBlendMode.ColorDodge;

        for (var y = 0; y < groupBitmap.Height; y++)
        {
            var parentY = top + y;
            if (parentY < 0 || parentY >= _rootBitmap.Height)
                continue;

            for (var x = 0; x < groupBitmap.Width; x++)
            {
                var alpha = (groupBitmap.GetPixel(x, y).Alpha / 255.0) * Math.Clamp(invocationAlpha, 0, 1);
                if (alpha <= 0)
                    continue;

                var parentX = left + x;
                if (parentX < 0 || parentX >= _rootBitmap.Width)
                    continue;

                var dst = _rootBitmap.GetPixel(parentX, parentY);
                if (_deviceCmyk.IsInKnockoutGroup)
                {
                    var initialBackdrop = _deviceCmyk.KnockoutInitialBackdrop?.Get(parentX, parentY)
                                          ?? new DeviceCmykColor(0, 0, 0, 0);
                    var initialAlpha = _deviceCmyk.KnockoutInitialBackdrop?.GetAlpha(parentX, parentY) ?? 0;
                    _deviceCmyk.Backdrop.Set(parentX, parentY, initialBackdrop, initialAlpha);
                    var (initialR, initialG, initialB) = DeviceCmykToRgb(initialBackdrop);
                    dst = new SKColor(
                        ToByte(initialR),
                        ToByte(initialG),
                        ToByte(initialB),
                        0);
                    _rootBitmap.SetPixel(parentX, parentY, dst);
                }

                var source = groupBackdrop.Get(x, y);
                var backdrop = _deviceCmyk.Backdrop.Get(parentX, parentY);
                var blended = isNormalBlend
                    ? source
                    : BlendDeviceCmykWithBackdropAlpha(
                        backdrop,
                        source,
                        blend,
                        _deviceCmyk.Backdrop.GetAlpha(parentX, parentY),
                        useDirectBlendFunctions);
                _deviceCmyk.Backdrop.CompositeSourceOver(parentX, parentY, blended, alpha);
                var output = _deviceCmyk.Backdrop.Get(parentX, parentY);
                var (r, g, b) = DeviceCmykToRgb(output);
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

    private void RenderFormXObject(Excise.Core.Primitives.PdfStream formStream)
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

    private void RenderFormXObjectInner(Excise.Core.Primitives.PdfStream formStream)
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
        // §8.10.1: a form XObject's execution is bracketed by an implicit
        // q/Q, so the font it selects must not survive the `Do` — and the
        // RESOLVED font is the font, not the name (#986). This site saved the
        // name and size and not the resolved font, which is the incoherent
        // middle: after a form set `/F2 24 Tf`, the page's next unstyled run
        // reported F1@24 while drawing out of F2's typeface, widths and
        // encoding. MEASURED at 72 dpi on a Helvetica page whose form selects
        // Courier: excise drew the post-`Do` `(MMMM)` run 58 px wide where
        // mutool, pdftocairo and Ghostscript all draw it 78 px wide.
        var savedFont = _currentFont;
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
            ? _page.Document.Resolve(resObj) as Excise.Core.Primitives.PdfDictionary
            : null;
        _resourcesStack.Push(formResources);

        try
        {
            // Apply the form's transformation matrix if present
            var matrixArray = formStream.GetOptional("Matrix") as Excise.Core.Primitives.PdfArray;
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
            _currentFont = savedFont;
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

    #endregion
}

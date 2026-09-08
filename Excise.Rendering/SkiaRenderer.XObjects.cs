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

    /// <summary>
    /// Category-based, cache-reusing check for "this form's content stream
    /// cannot mark the page at all" (#1426). Uses the same
    /// <see cref="Excise.Core.Content.OperatorCategory"/> the parser already
    /// assigns every operator, rather than a second hand-rolled operator
    /// list -- PathPainting/TextShowing/Shading/XObject are exactly the
    /// categories that can put ink on the page; every other category
    /// (graphics/text state, path construction, clipping, colour, marked
    /// content, compatibility) cannot by construction. Reuses the same
    /// byte[]-keyed parse cache <see cref="ExecuteContentBytes"/> already
    /// relies on, so a form invoked dozens of times (the exact shape this
    /// exists for) only pays the parse cost once.
    /// </summary>
    private bool FormContentStreamPaintsNothing(Excise.Core.Primitives.PdfStream formStream)
    {
        var contentBytes = formStream.DecodedData;
        if (contentBytes.Length == 0)
            return true;

        if (!_resourceScope.TryGetParsedContent(contentBytes, out var content))
        {
            content = new Excise.Core.Content.ContentStreamParser(contentBytes, _page)
                { ComputeOperatorMetadata = false }
                .Parse(_cancellationToken);
            _resourceScope.CacheParsedContent(contentBytes, content);
        }

        foreach (var op in content!.Operators)
        {
            switch (op.Category)
            {
                case Excise.Core.Content.OperatorCategory.PathPainting:
                case Excise.Core.Content.OperatorCategory.TextShowing:
                case Excise.Core.Content.OperatorCategory.Shading:
                case Excise.Core.Content.OperatorCategory.XObject:
                    return false;
            }
        }

        return true;
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

        // #1426: a degenerate/no-op form still gets a full group-bitmap
        // allocate + backdrop-sync + composite on every invocation without
        // this check -- measured at 57 invocations on the mesh-shading
        // Class B fixture, ~1.7 GB of memory traffic per render to paint
        // nothing. Per ISO 32000-1 §11.4/§11.6.6 a group that paints
        // nothing contributes nothing to the backdrop either way, so
        // skipping straight past both the group-bitmap path AND the
        // SyncDeviceCmykBackdropFromRootBitmap side effect is correct, not
        // just fast -- verified against mutool/pdftocairo on both known
        // fixtures, see RenderTailEmptyTransparencyGroupTests.
        if (FormContentStreamPaintsNothing(formStream))
            return true;

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

        // #1425/#1426: this was the one _rootBitmap.GetPixel-per-pixel loop
        // #1402 left unconverted in this file. GetPixel returns STRAIGHT
        // (unpremultiplied) colour from the raw premultiplied bytes this
        // bitmap actually stores, so reproducing its output needs an actual
        // unpremultiply, not just re-deriving the formula in premultiplied
        // terms (an earlier version of this comment tried that; it changes
        // the skip-threshold comparison's behaviour materially at low alpha
        // -- SyncDeviceCmykBackdropFromRootBitmapPixelContractTests caught
        // it). UnpremultiplyChannel matches SKBitmap.GetPixel's own
        // unpremultiply to within 1/255 in the rare case they diverge at
        // all (measured: 322/65280 value/alpha pairs, always by exactly 1),
        // which only ever flips this function's approximate 12-unit skip
        // threshold at an exact tie (measured: 1/200,000 random trials) --
        // see that test file for both measurements.
        _canvas.Flush();
        var rootRowBytes = _rootBitmap.RowBytes;
        var rootPixels = GetRootPixelSpan();

        for (var y = 0; y < height; y++)
        {
            var parentY = top + y;
            if (parentY < 0 || parentY >= _rootBitmap.Height)
                continue;

            var rowStart = parentY * rootRowBytes;

            for (var x = 0; x < width; x++)
            {
                var parentX = left + x;
                if (parentX < 0 || parentX >= _rootBitmap.Width)
                    continue;

                var offset = rowStart + (parentX * 4);
                var rawAlpha = rootPixels[offset + 3];
                var straightR = UnpremultiplyChannel(rootPixels[offset], rawAlpha);
                var straightG = UnpremultiplyChannel(rootPixels[offset + 1], rawAlpha);
                var straightB = UnpremultiplyChannel(rootPixels[offset + 2], rawAlpha);

                var retained = _deviceCmyk.Backdrop.Get(parentX, parentY);
                var (retainedR, retainedG, retainedB) = DeviceCmykToRgb(retained);
                if (Math.Abs(straightR - ToByte(retainedR)) +
                    Math.Abs(straightG - ToByte(retainedG)) +
                    Math.Abs(straightB - ToByte(retainedB)) <= 12)
                {
                    continue;
                }

                var alpha = rawAlpha / 255.0;
                var r = (straightR / 255.0 * alpha) + (1 - alpha);
                var g = (straightG / 255.0 * alpha) + (1 - alpha);
                var b = (straightB / 255.0 * alpha) + (1 - alpha);
                _deviceCmyk.Backdrop.Set(parentX, parentY, RgbToDeviceCmyk(r, g, b), alpha);
            }
        }
    }

    /// <summary>
    /// Recovers a straight (unpremultiplied) channel value from this
    /// bitmap's raw premultiplied byte, matching what <c>SKBitmap.GetPixel</c>
    /// would return closely enough for <see cref="SyncDeviceCmykBackdropFromRootBitmap"/>'s
    /// approximate skip-threshold comparison (see that method's comment and
    /// SyncDeviceCmykBackdropFromRootBitmapPixelContractTests for the measured
    /// agreement). Internal so that test can pin the measurement.
    /// </summary>
    internal static byte UnpremultiplyChannel(byte raw, byte alpha)
    {
        if (alpha == 0)
            return 0;
        return (byte)Math.Clamp((raw * 255 + (alpha / 2)) / alpha, 0, 255);
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

        // #1402: this used to be groupBitmap.GetPixel/_rootBitmap.GetPixel/SetPixel
        // per pixel — each call marshals through SKBitmap.Info — over a region that
        // is routinely the FULL PAGE (measured: 57 group compositions at
        // 1275x1650 = ~120M pixel visits on the #1402 fixture, none of them from
        // the mesh shading the issue named). Reading/writing the raw premultiplied
        // byte planes directly is byte-identical: GetPixel's .Alpha is unaffected
        // by premultiplication (raw alpha byte == straight alpha byte), and
        // WritePremulRgba is pinned exhaustively equal to SetPixel by
        // DeviceCmykBlendPixelContractTests.
        var clampedInvocationAlpha = Math.Clamp(invocationAlpha, 0, 1);
        var groupWidth = groupBitmap.Width;
        var groupHeight = groupBitmap.Height;
        var groupRowBytes = groupBitmap.RowBytes;
        var groupPixels = groupBitmap.GetPixelSpan();
        var rootWidth = _rootBitmap.Width;
        var rootHeight = _rootBitmap.Height;
        var rootRowBytes = _rootBitmap.RowBytes;

        _canvas.Flush();
        var rootPixels = GetRootPixelSpan();
        var wroteRoot = false;

        for (var y = 0; y < groupHeight; y++)
        {
            var parentY = top + y;
            if (parentY < 0 || parentY >= rootHeight)
                continue;

            var groupRowStart = y * groupRowBytes;
            var rootRowStart = parentY * rootRowBytes;

            for (var x = 0; x < groupWidth; x++)
            {
                var alpha = (groupPixels[groupRowStart + (x * 4) + 3] / 255.0) * clampedInvocationAlpha;
                if (alpha <= 0)
                    continue;

                var parentX = left + x;
                if (parentX < 0 || parentX >= rootWidth)
                    continue;

                var rootOffset = rootRowStart + (parentX * 4);
                var dstAlphaByte = rootPixels[rootOffset + 3];
                if (_deviceCmyk.IsInKnockoutGroup)
                {
                    var initialBackdrop = _deviceCmyk.KnockoutInitialBackdrop?.Get(parentX, parentY)
                                          ?? new DeviceCmykColor(0, 0, 0, 0);
                    var initialAlpha = _deviceCmyk.KnockoutInitialBackdrop?.GetAlpha(parentX, parentY) ?? 0;
                    _deviceCmyk.Backdrop.Set(parentX, parentY, initialBackdrop, initialAlpha);
                    var (initialR, initialG, initialB) = DeviceCmykToRgb(initialBackdrop);
                    WritePremulRgba(
                        rootPixels,
                        rootOffset,
                        ToByte(initialR),
                        ToByte(initialG),
                        ToByte(initialB),
                        0);
                    dstAlphaByte = 0;
                    wroteRoot = true;
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
                var dstAlpha = dstAlphaByte / 255.0;
                var outAlpha = alpha + (dstAlpha * (1 - alpha));
                WritePremulRgba(
                    rootPixels,
                    rootOffset,
                    ToByte(r),
                    ToByte(g),
                    ToByte(b),
                    ToByte(outAlpha));
                wroteRoot = true;
            }
        }

        if (wroteRoot)
            _rootBitmap.NotifyPixelsChanged();
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

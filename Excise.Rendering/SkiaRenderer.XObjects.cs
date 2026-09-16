using System.Diagnostics.CodeAnalysis;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Rendering.Transparency;
using SkiaSharp;

namespace Excise.Rendering;

internal partial class RenderContext
{
    #region XObject Rendering (Do operator)

    /// <summary>
    /// Depth of Skia layers, opened on a DeviceCMYK page, whose contents must NOT
    /// write around the layer (#1395). While it is above zero the DeviceCMYK
    /// direct-write paths (<c>TryPaintDeviceCmykBlendPath</c>,
    /// <c>CompositeImageIntoDeviceCmykBackdrop</c>, <c>TryRenderDeviceCmykFormGroup</c>)
    /// refuse, so everything the group draws lands in the layer where its
    /// <c>/SMask</c>, <c>/ca</c> and <c>/BM</c> can act on it. The layer's
    /// composite is folded back into the CMYK backdrop afterwards
    /// (<see cref="SyncDeviceCmykBackdropForThisBitmap"/>).
    /// </summary>
    private int _deviceCmykDirectWriteSuppression;

    /// <summary>
    /// True in a child context rendering a transparency group into its own
    /// offscreen buffer for one of the #1395 entries. The buffer starts EMPTY,
    /// which changes what "this paint cannot change the backdrop" means — see
    /// the zero-ink guard in <c>TryPaintDeviceCmykBlendPath</c>.
    /// </summary>
    private bool _isContainedGroupChild;

    /// <summary>
    /// True when this context's <c>_rootBitmap</c> is a transparency-GROUP
    /// bitmap — one <see cref="TryRenderDeviceCmykFormGroup"/> allocated and
    /// cleared to <see cref="SKColors.Transparent"/> — rather than the page
    /// bitmap, which a DeviceCMYK-group page clears to the paper colour
    /// (<c>SkiaRenderer.RenderPage</c>'s <c>startsInDeviceCmykGroup</c> branch).
    ///
    /// <para>This is the property that decides WHICH backdrop sync is correct
    /// (#1510): a page bitmap carries alpha 255 everywhere, so resolving
    /// partial alpha against paper is right and the rare zero-alpha pixel is a
    /// truth to record; a group bitmap starts empty, so an unpainted pixel
    /// carries no information at all and must be left alone. Set for EVERY
    /// child that method opens, both the pre-#1395 <c>UnmaskedDeviceCmykGroup</c>
    /// entry and #1395's contained ones — unlike
    /// <see cref="_isContainedGroupChild"/>, which #1505 used as a proxy for it
    /// and which is true for only the latter.</para>
    ///
    /// <para>Constructor-set and readonly on purpose: a group bitmap and the
    /// fact that it is one are two halves of one decision, and #1510 exists
    /// because they were made in different places.</para>
    /// </summary>
    private readonly bool _isDeviceCmykGroupBitmap;

    /// <summary>
    /// True when this context composites DeviceCMYK paint straight into
    /// <c>_rootBitmap</c> and its retained CMYK backdrop, bypassing the Skia
    /// layer stack. That bypass is what let a soft-masked group's contents
    /// escape the layer the mask was applied to (#1395).
    /// </summary>
    [MemberNotNullWhen(true, nameof(_rootBitmap))]
    private bool CanWriteDeviceCmykDirectly =>
        _rootBitmap != null &&
        _deviceCmyk.IsInTransparencyGroup &&
        _deviceCmykDirectWriteSuppression == 0;

    /// <summary>Which invocation reached <see cref="TryRenderDeviceCmykFormGroup"/>.</summary>
    private enum DeviceCmykGroupEntry
    {
        /// <summary>A <c>/CS /DeviceCMYK</c> group with no soft mask — the pre-#1395 entry, behaviour unchanged.</summary>
        UnmaskedDeviceCmykGroup,

        /// <summary>A group invoked under a <c>/SMask</c> on a DeviceCMYK page (#1395).</summary>
        SoftMaskedGroup,

        /// <summary>A group invoked with <c>/ca</c> &lt; 1 or a non-Normal <c>/BM</c> and no mask, whose <c>/CS</c> is absent (inherited CMYK) (#1395).</summary>
        PlainGroup,
    }

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
        var attemptedUnmaskedCmykGroup = isDeviceCmykGroup && invocationState.SoftMask == null;
        if (attemptedUnmaskedCmykGroup &&
            TryRenderDeviceCmykFormGroup(
                formStream, group, invocationState, DeviceCmykGroupEntry.UnmaskedDeviceCmykGroup))
        {
            return;
        }

        if (CanWriteDeviceCmykDirectly && GroupInvocationNeedsCompositing(invocationState))
        {
            // #1395. On a DeviceCMYK page the group's contents composite
            // DIRECTLY into _rootBitmap and the retained CMYK backdrop. A Skia
            // SaveLayer opened for this group's /SMask, /ca or /BM therefore
            // held only whatever did NOT take a direct-write path, the mask or
            // alpha was applied to that, and Restore painted the result over
            // the unmasked object that had already escaped. On Ghent GWG168
            // every cell of the "Actual test objects" row came out wrong that
            // way while every mask bitmap was individually correct.
            //
            // A group whose /CS is DeviceCMYK — or absent, which inherits the
            // CMYK page's space — renders through its own child context and
            // offscreen bitmap, and the mask/alpha/blend are applied during the
            // CMYK composite, so blending inside it stays CMYK-exact (#1395's
            // Option B).
            var inheritsDeviceCmyk = isDeviceCmykGroup || group?.GetOptional("CS") == null;
            var entry = invocationState.SoftMask != null
                ? DeviceCmykGroupEntry.SoftMaskedGroup
                : DeviceCmykGroupEntry.PlainGroup;

            // A NON-isolated group composited with a non-Normal blend mode used
            // to be routed AWAY from the child path here, because §11.4.4's
            // backdrop-REMOVAL step did not exist and blending a result that
            // still carried the backdrop over that same backdrop counted it
            // twice. #1504 implements the removal in
            // CompositeDeviceCmykGroupBitmap, so this is exactly the case that
            // now MUST take the child path: the contained Skia layer is the one
            // place where the seeded backdrop is provably wrong under a
            // non-Normal invocation blend (see RenderFormGroupThroughSkiaLayer).
            //
            // An unmasked /CS /DeviceCMYK group already had its attempt above;
            // if that failed (no usable /BBox, over the pixel limit) a second
            // attempt would fail the same way, so it goes straight to the
            // contained layer rather than an uncontained one.
            if (inheritsDeviceCmyk &&
                !attemptedUnmaskedCmykGroup &&
                TryRenderDeviceCmykFormGroup(formStream, group, invocationState, entry))
            {
                return;
            }

            // An explicit non-CMYK /CS (the group's own blending space is not
            // CMYK), or a CMYK group the child path could not take: keep the
            // Skia layer, but CONTAIN it — refuse every direct write while it
            // is open so the whole group lands in the layer, then fold the
            // composite back into the CMYK backdrop for the region it covers.
            var syncRegion = GetDeviceCmykSyncRegion(GetFormInvocationBounds(formStream));
            _deviceCmykDirectWriteSuppression++;
            try
            {
                RenderFormGroupThroughSkiaLayer(formStream, group, invocationState);
            }
            finally
            {
                _deviceCmykDirectWriteSuppression--;
            }

            // #1510: the fold-back has to match what THIS context's bitmap is.
            // Reachable from a CHILD group context — a nested group whose /CS is
            // explicitly not CMYK fails `inheritsDeviceCmyk` and lands here,
            // where `this` is the enclosing child and _rootBitmap is its
            // transparent group bitmap. The page-flavoured sync then resolved
            // the layer's partial alpha against paper, and the composite
            // applied that alpha a second time.
            if (syncRegion is { } region)
                SyncDeviceCmykBackdropForThisBitmap(region.Left, region.Top, region.Width, region.Height);
            return;
        }

        RenderFormGroupThroughSkiaLayer(formStream, group, invocationState);
    }

    /// <summary>
    /// A group invocation is more than "draw the contents" when it carries a
    /// soft mask, a constant alpha below one, or a non-Normal blend mode — the
    /// three compositing parameters §11.6.6 applies to the finished group.
    /// </summary>
    private static bool GroupInvocationNeedsCompositing(GraphicsState invocationState)
        => invocationState.SoftMask != null ||
           invocationState.FillAlpha < 1f ||
           invocationState.BlendMode != SKBlendMode.SrcOver;

    private void RenderFormGroupThroughSkiaLayer(
        Excise.Core.Primitives.PdfStream formStream,
        Excise.Core.Primitives.PdfDictionary? group,
        GraphicsState invocationState)
    {
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
                _state.ClearSoftMask();

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
                    seedBackdrop: group?.GetBool("I") == false,
                    // DrawFormContent draws the group at full alpha; the
                    // group's own /ca belongs to the composite of the finished
                    // group (§11.6.6), so it goes on the layer. Every other
                    // caller has its alpha in the content already (#1393).
                    layerAlpha: invocationState.FillAlpha);
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

        // ⚠️ This layer is ISOLATED, and §11.6.6 says it should not be: /I
        // defaults to FALSE, so a group is non-isolated unless it says
        // otherwise, and a /BM used inside it must act on the group's backdrop.
        // Skia's SaveLayer starts fully transparent, so it does not. #1394
        // filed that and is CLOSED, but it covered only the SEEDING half; the
        // §11.4.4 backdrop-REMOVAL step is #1504.
        //
        // #1504 implements removal for the DeviceCMYK CHILD-CONTEXT path
        // (CompositeDeviceCmykGroupBitmap). It deliberately does NOT extend
        // seeding to this plain layer path, and the reason is no longer "it
        // overshoots" — it is that removal cannot be EXPRESSED here. Skia's
        // SaveLayer keeps one alpha channel, so seeding writes the backdrop
        // alpha a0 into it and the group's own accumulated alpha agn (§11.4.4's
        // result alpha) becomes unrecoverable the moment a0 = 1. Removal needs
        // both. The child path can do it because the seed goes into the
        // RETAINED CMYK backdrop while the group bitmap's alpha stays agn.
        //
        // What IS provable about the soft-mask branch above, which does seed:
        // for a0 = 1 and a Normal invocation blend, seeding WITHOUT removal is
        // algebraically identical to the spec, because Restore composites the
        // layer at the layer's OWN alpha and colour — a consistent pair — where
        // the CMYK composite pairs Cn (backdrop included) with agn (backdrop
        // excluded). See SeedNonIsolatedGroupBackdrop for the derivation and
        // the two residual cases.
        //
        // A synthetic probe does NOT catch any of this: with a group that
        // paints an opaque rect over the sample point, agn = 1 and removal is a
        // no-op, so seeding looks exactly right (it matched gs and mutool on
        // four such probes). A discriminating fixture needs PARTIAL alpha
        // inside the group over a non-empty backdrop — see
        // DeviceCmykNonIsolatedGroupBackdropRemovalTests.
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

    /// <summary>
    /// The device-pixel rectangle of <c>_rootBitmap</c> a contained group layer
    /// can have changed: the invocation bounds (or the clip, when the form has
    /// no usable <c>/BBox</c>) mapped to device space, padded by a pixel for
    /// antialiasing and clamped to the bitmap and the device clip. Null when
    /// nothing can have been drawn.
    /// </summary>
    private SKRectI? GetDeviceCmykSyncRegion(SKRect? localBounds)
    {
        if (_rootBitmap == null)
            return null;

        var clip = _canvas.DeviceClipBounds;
        SKRect device = localBounds.HasValue
            ? _canvas.TotalMatrix.MapRect(localBounds.Value)
            : new SKRect(clip.Left, clip.Top, clip.Right, clip.Bottom);

        var left = Math.Max(Math.Clamp((int)Math.Floor(device.Left) - 1, 0, _rootBitmap.Width), clip.Left);
        var top = Math.Max(Math.Clamp((int)Math.Floor(device.Top) - 1, 0, _rootBitmap.Height), clip.Top);
        var right = Math.Min(Math.Clamp((int)Math.Ceiling(device.Right) + 1, 0, _rootBitmap.Width), clip.Right);
        var bottom = Math.Min(Math.Clamp((int)Math.Ceiling(device.Bottom) + 1, 0, _rootBitmap.Height), clip.Bottom);
        if (right <= left || bottom <= top)
            return null;

        return new SKRectI(left, top, right, bottom);
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

    /// <summary>
    /// Render a transparency group on a DeviceCMYK page through a child
    /// context with its own offscreen bitmap and retained CMYK backdrop, then
    /// composite the finished group onto this context's backdrop.
    ///
    /// <para><b>Containment (#1395).</b> Every DeviceCMYK direct-write path the
    /// group's contents take — blend/overprint fills, image backdrop
    /// composites, nested CMYK groups — writes into the CHILD's bitmap and
    /// backdrop, never into this context's. The group's compositing parameters
    /// then act on the whole finished group during
    /// <see cref="CompositeDeviceCmykGroupBitmap"/>: the soft mask as a
    /// per-pixel factor on the group alpha, <c>/ca</c> as a constant factor,
    /// <c>/BM</c> as the CMYK blend against this context's backdrop.</para>
    ///
    /// <para>Every entry starts the child at Normal / alpha 1 / no mask
    /// (§11.6.6). The two #1395 entries (<see cref="DeviceCmykGroupEntry.SoftMaskedGroup"/>,
    /// <see cref="DeviceCmykGroupEntry.PlainGroup"/>) additionally clip the
    /// group region to the device clip and fold Skia-RGB paint inside the child
    /// into the child's CMYK backdrop before the composite. The pre-#1395 entry
    /// keeps its region and its dirty-flag-gated fold — since #1510 through the
    /// same group-flavoured sync, since the child's bitmap is a group bitmap
    /// under every entry.</para>
    ///
    /// <para><b>Non-isolated groups (§11.4.4, #1504).</b> A non-isolated child
    /// is seeded from this context's backdrop, so the child's accumulated
    /// colour includes that backdrop. <see cref="CompositeDeviceCmykGroupBitmap"/>
    /// removes it again before compositing — otherwise the backdrop is counted
    /// twice. The seed source is passed to the composite verbatim
    /// (<c>SelectBackdropForChild()</c>, which is the enclosing knockout
    /// group's INITIAL backdrop when there is one — §11.4.6's note that a
    /// non-isolated group nested in a knockout group takes the OUTER group's
    /// initial backdrop, not its immediate one), so <c>C0</c> is exactly what
    /// was seeded and not a re-derivation of it.</para>
    /// </summary>
    private bool TryRenderDeviceCmykFormGroup(
        Excise.Core.Primitives.PdfStream formStream,
        Excise.Core.Primitives.PdfDictionary? group,
        GraphicsState invocationState,
        DeviceCmykGroupEntry entry)
    {
        if (group == null ||
            _rootBitmap == null ||
            _deviceCmyk.Backdrop == null ||
            // Inside a contained Skia layer nothing may bypass the layer,
            // including a nested CMYK group (#1395).
            _deviceCmykDirectWriteSuppression > 0)
        {
            return false;
        }

        var isContainedEntry = entry != DeviceCmykGroupEntry.UnmaskedDeviceCmykGroup;

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
        if (isContainedEntry)
        {
            // The child canvas does not inherit this canvas's clip and the
            // composite writes pixels directly, so without this a group drawn
            // inside `re W n` would composite outside it. The device clip
            // bounds are the clip's bounding box: exact for the rectangular
            // clips producers wrap groups in, conservative for path clips.
            var clip = _canvas.DeviceClipBounds;
            left = Math.Max(left, clip.Left);
            top = Math.Max(top, clip.Top);
            right = Math.Min(right, clip.Right);
            bottom = Math.Min(bottom, clip.Bottom);
        }

        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
            return true;

        var pixels = (long)width * height;
        if (pixels > _options.MaxPixelCount)
            return false;

        byte[]? maskPlane = null;
        if (entry == DeviceCmykGroupEntry.SoftMaskedGroup &&
            !TryRasterizeDeviceCmykGroupSoftMask(invocationState, parentMatrix, left, top, width, height, out maskPlane))
        {
            // The caller falls back to the contained Skia layer, whose
            // RenderWithCurrentSoftMask handles every mask shape this cannot.
            return false;
        }

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
                startsInDeviceCmykTransparencyGroup: true,
                // groupBitmap was just Clear(Transparent)'d, under EVERY entry
                // — which is what #1510 is about.
                rootBitmapIsTransparencyGroupBitmap: true);
            child._resourcesStack.Push(_page.Resources);
            child._state = invocationState.Clone();
            // §11.6.6: a group's content starts at blend mode Normal, alpha
            // constants 1.0 and no soft mask; the invocation's /ca, /BM and
            // /SMask apply once, to the finished group, in the composite below.
            // The Skia layer path does the same reset in DrawFormContent. Until
            // #1395 this entry kept the invocation's /ca inside the group AND
            // applied it on the composite (~0.125 where the spec says 0.5 for a
            // /ca 0.5 group over a wash) — no Ghent gwg160–162 group is invoked
            // with /ca below 1, so no contract saw it. See
            // DeviceCmykSoftMaskedGroupTests' constant-alpha rows.
            child._state.BlendMode = SKBlendMode.SrcOver;
            child._state.FillAlpha = 1;
            child._state.StrokeAlpha = 1;
            child._state.ClearSoftMask();
            child._isContainedGroupChild = isContainedEntry;

            var isIsolated = group.GetBool("I");
            var isKnockout = group.GetBool("K");

            // A non-isolated group composited with a non-Normal invocation /BM
            // needs THIS context's retained CMYK backdrop to agree with what is
            // actually in THIS context's bitmap before the seed is taken: the
            // blend runs against the seeded backdrop, so RGB paint that landed
            // in the bitmap without reaching the CMYK backdrop has to be folded
            // in first. WHICH sync does that depends on what the bitmap IS
            // (#1505).
            //
            // On the page, _rootBitmap is the page bitmap, and a page that
            // starts in a DeviceCMYK group is cleared to the PAPER colour
            // (SkiaRenderer.RenderPage's startsInDeviceCmykGroup branch), so
            // every pixel carries alpha 255 and the page-flavoured sync is
            // right: it resolves partial alpha against paper, and the only
            // zero-alpha pixels it meets are knockout resets
            // (ResetDeviceCmykKnockoutPixel), where "no backdrop here" is the
            // truth it must record.
            //
            // In a CHILD group context (#1395) _rootBitmap is the group bitmap,
            // and that one starts Clear(Transparent). The page-flavoured sync
            // then reads raw alpha 0 / straight RGB (0,0,0) on every pixel the
            // group has not painted yet, finds it 500+ units away from the
            // seeded backdrop's own RGB, and OVERWRITES the seed with zero ink
            // at alpha 0 — destroying the non-isolated seed precisely over the
            // region the nested group's blend is about to act on.
            // BlendDeviceCmykWithBackdropAlpha then sees backdrop alpha 0 and
            // returns the source UNBLENDED (correct for a genuinely transparent
            // backdrop, §11.3.6's Cs weighting), so the nested group's raw
            // colour lands on the page at full strength. Measured on pdf.js
            // issue13520 (#1505): 439 dark pixels where mutool and Ghostscript
            // both draw 0, with the group's accumulated colour arriving as the
            // raw shading ink (1, 1, 0, 0.85) instead of the Screen result.
            //
            // The group-flavoured sync is the one written for a buffer that
            // starts transparent: it SKIPS alpha-0 pixels, so the seed survives
            // where the group has painted nothing, and stores straight colour
            // plus the pixel's own alpha where it has.
            //
            // #1510: the discriminator is _isDeviceCmykGroupBitmap — "this
            // bitmap is a group bitmap" — which is the property that actually
            // matters. #1505 used _isContainedGroupChild as a proxy for it and
            // said so: the pre-#1395 UnmaskedDeviceCmykGroup child (an explicit
            // /CS /DeviceCMYK group) is _isContainedGroupChild FALSE and still a
            // transparent group bitmap, so it kept taking the page-flavoured
            // sync here — the same defect one entry over.
            //
            // It still must not become unconditional: on the PAGE bitmap the
            // page-flavoured sync is the correct one, and that is what the flag
            // selects.
            //
            // Knockout is the case that makes the group flavour more correct
            // rather than merely different: inside a knockout child,
            // ResetDeviceCmykKnockoutPixel writes the group's INITIAL backdrop
            // into the retained backdrop and alpha 0 into the bitmap (§11.4.6).
            // The group-flavoured sync skips alpha-0 pixels, so that reset
            // survives to be the backdrop a nested blend acts on; the
            // page-flavoured sync overwrote it with zero ink at alpha 0.
            if (!isIsolated && invocationState.BlendMode != SKBlendMode.SrcOver)
                SyncDeviceCmykBackdropForThisBitmap(left, top, width, height);

            // The seed source, kept so the composite can remove exactly what
            // was seeded (§11.4.4, #1504) rather than re-deriving C0 from a
            // backdrop this composite is itself mutating pixel by pixel.
            // EnterChildGroup seeds only when the group is non-isolated and
            // this is non-null, so those are the same two conditions removal
            // applies under.
            var parentBackdropForChild = _deviceCmyk.SelectBackdropForChild();
            var seededInitialBackdrop = isIsolated ? null : parentBackdropForChild;

            child._deviceCmyk.EnterChildGroup(new DeviceCmykChildGroupRequest(
                isIsolated,
                isKnockout,
                _deviceCmyk.IsInKnockoutGroup,
                parentBackdropForChild,
                left,
                top,
                width,
                height));

            DeviceCmykChildGroupResult childResult;
            try
            {
                child.RenderFormXObject(formStream);

                // Text, DeviceGray/RGB fills, and any fill carrying its OWN
                // /SMask (TryPaintDeviceCmykBlendPath refuses those) are painted
                // by Skia into groupBitmap without touching the child's CMYK
                // backdrop, and only shading patterns mark the backdrop dirty.
                // The composite reads COLOUR from the backdrop, so without this
                // those pixels would composite as whatever the backdrop held.
                // GWG168's masked groups contain exactly such nested masked
                // fills; GWG1610/1611's contain text.
                //
                // For those entries the unconditional sync SUBSUMES the
                // dirty-flag one CompleteChildGroup would otherwise run, so it
                // gets a no-op — running both would visit the same pixels twice.
                //
                // #1510: BOTH branches now use the group-flavoured sync, because
                // `child`'s bitmap is a group bitmap under every entry. The
                // page-flavoured sync this branch used to run resolved partial
                // alpha against paper — the composite then applied that alpha a
                // second time, so a half-opacity result inside a pre-#1395
                // /CS /DeviceCMYK group reached the page at a quarter strength —
                // and it re-whitened any pixel whose RGB->CMYK->RGB round trip
                // drifted past the 12-unit threshold.
                //
                // What is left between the branches is the TRIGGER, not the
                // flavour: this one runs only when BackdropDirtyFromRgbPaint is
                // set, and the only paint that sets it is a shading or tiling
                // pattern (plus `sh` since #1511). Text and DeviceGray/RGB fills
                // in a pre-#1395 /CS /DeviceCMYK group still reach the composite
                // unfolded and take the backdrop's colour — tracked separately,
                // deliberately not widened here so #1511 stays observable.
                Action synchronizeDirtyBackdrop;
                if (isContainedEntry)
                {
                    child.SyncDeviceCmykGroupBackdropFromGroupBitmap();
                    synchronizeDirtyBackdrop = static () => { };
                }
                else
                {
                    synchronizeDirtyBackdrop =
                        () => child.SyncDeviceCmykGroupBackdropFromGroupBitmap(0, 0, width, height);
                }

                childResult = child._deviceCmyk.CompleteChildGroup(synchronizeDirtyBackdrop);
            }
            finally
            {
                child._resourcesStack.Clear();
                child.DisposeOwnedResources();
            }

            if (!childResult.IsAvailable)
                return false;

            // #1514: this used to be `_deviceCmyk.IsInKnockoutGroup ? 1 :
            // invocationState.FillAlpha`, which DISCARDED the invocation's /ca
            // for every group invoked inside a knockout parent. Measured on
            // Ghent GWG161's "Opacity (0%)" cell, whose second element is
            // invoked at /ca 0: the probe read
            // `/ca=0.000 -> groupInvocationAlpha=1.000`, so the element painted
            // at full strength and drew the very X the page exists to forbid
            // ("If an 'X' appears, rendering of Knockout Transparency Groups is
            // not performed correctly"). Blue over the cell came out 133 where
            // mutool reads 191, Ghostscript 196 and pdftocairo 201.
            //
            // Nothing in §11.4.6 makes a knockout group ignore its members'
            // constant alpha; what knockout changes is the BACKDROP each element
            // composites against, which the reset in
            // CompositeDeviceCmykGroupBitmap handles.
            //
            // ⚠️ The override predates #1395 and was harmless until it: the
            // child context used to inherit the invocation's FillAlpha, so /ca
            // was applied INSIDE the group as well as here. That double
            // application was itself a bug (#1395's own commit: ~0.125 where the
            // spec says 0.5), but 0 x 1 = 0, so it happened to enforce /ca 0.
            // #1395 correctly reset the child to alpha 1 per §11.6.6 and left
            // /ca enforced in neither place.
            var groupInvocationAlpha = invocationState.FillAlpha;
            CompositeDeviceCmykGroupBitmap(
                groupBitmap,
                childResult.Backdrop!,
                left,
                top,
                invocationState.BlendMode,
                groupInvocationAlpha,
                maskPlane,
                isContainedEntry,
                seededInitialBackdrop);
        }

        return true;
    }

    /// <summary>
    /// Rasterise the invocation's <c>/SMask</c> into a device-space mask plane
    /// aligned with a DeviceCMYK group bitmap at (<paramref name="left"/>,
    /// <paramref name="top"/>): one byte per pixel, 0 = fully masked out,
    /// 255 = fully revealed (#1395).
    ///
    /// <para>The mask is produced the way <see cref="RenderWithCurrentSoftMask"/>
    /// produces it — the same <c>/G</c> rendering (§11.6.5.2 independence from
    /// the caller, <c>/BC</c> backdrop), the same luminosity colour filter and
    /// the same <c>/TR</c> table — so a mask means the same thing on a CMYK page
    /// as on an RGB one. Two differences, both spec-driven:</para>
    /// <list type="bullet">
    /// <item>It is rendered directly in DEVICE space for exactly this region,
    /// rather than into a user-space rectangle scaled to a bitmap, so it stays
    /// aligned under rotated or skewed CTMs.</item>
    /// <item>It uses the CTM in force when the <c>gs</c> that set the mask ran
    /// (§11.6.5.1), when that <c>gs</c> ran in this context. A mask inherited
    /// from another context falls back to the CTM at invocation.</item>
    /// </list>
    /// <para><c>/S /Alpha</c> takes the group's alpha (composited over a
    /// transparent backdrop, <c>/BC</c> ignored) instead of its luminosity.</para>
    ///
    /// <para>Returns true with a null plane when the mask is <c>/S /None</c>
    /// (no mask applies). Returns false when the mask cannot be produced here —
    /// no usable <c>/G</c> form, or a legacy non-dictionary mask — so the
    /// caller falls back to the contained Skia layer and its established
    /// handling of those shapes.</para>
    /// </summary>
    private bool TryRasterizeDeviceCmykGroupSoftMask(
        GraphicsState invocationState,
        SKMatrix invocationMatrix,
        int left,
        int top,
        int width,
        int height,
        out byte[]? maskPlane)
    {
        maskPlane = null;
        if (invocationState.SoftMask == null)
            return true;

        var resolution = ResolveSoftMask(
            invocationState.SoftMask,
            out _,
            out var maskStream,
            out var maskDictionary);
        if (resolution == SoftMaskResolution.Disabled)
            return true;

        if (resolution != SoftMaskResolution.Usable ||
            maskStream == null ||
            maskDictionary == null ||
            !string.Equals(maskStream.GetNameOrNull("Subtype"), "Form", StringComparison.Ordinal))
        {
            return false;
        }

        var isAlphaMask = string.Equals(maskDictionary.GetNameOrNull("S"), "Alpha", StringComparison.Ordinal);

        var maskMatrix = ReferenceEquals(invocationState.SoftMaskOwner, this) &&
                         invocationState.SoftMaskDeviceMatrix is { } gsTimeMatrix
            ? gsTimeMatrix
            : invocationMatrix;
        maskMatrix.TransX -= left;
        maskMatrix.TransY -= top;

        using var maskBitmap = RenderFormSoftMaskIntoBitmap(
            maskStream,
            width,
            height,
            maskMatrix,
            invocationState,
            maskDictionary,
            alphaSubtype: isAlphaMask);
        if (maskBitmap == null)
            return false;

        using var planeBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var planeCanvas = new SKCanvas(planeBitmap))
        {
            planeCanvas.Clear(SKColors.Transparent);
            using var lumaFilter = isAlphaMask ? null : SKColorFilter.CreateLumaColor();
            using var transferFilter = CreateSoftMaskTransferFilter(maskDictionary);
            using var composedFilter = lumaFilter != null && transferFilter != null
                ? SKColorFilter.CreateCompose(transferFilter, lumaFilter)
                : null;
            using var planePaint = new SKPaint
            {
                // Src: the plane IS the filtered mask, not the mask over anything.
                BlendMode = SKBlendMode.Src,
                ColorFilter = composedFilter ?? lumaFilter ?? transferFilter,
            };
            planeCanvas.DrawBitmap(maskBitmap, 0, 0, planePaint);
        }

        var planePixels = planeBitmap.GetPixelSpan();
        var planeRowBytes = planeBitmap.RowBytes;
        var plane = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * planeRowBytes;
            var planeRow = y * width;
            for (var x = 0; x < width; x++)
                plane[planeRow + x] = planePixels[rowStart + (x * 4) + 3];
        }

        maskPlane = plane;
        return true;
    }

    /// <summary>
    /// Fold Skia-RGB paint in THIS context's bitmap into THIS context's
    /// retained CMYK backdrop, over one region in this context's pixel
    /// coordinates, choosing the sync that matches what the bitmap IS (#1510).
    ///
    /// <para>The two syncs differ only where the bitmap's alpha is below 255 —
    /// at alpha 255 they store the same colour and the same alpha — so this
    /// choice is exactly the choice of what a partially or fully transparent
    /// pixel MEANS. On the page bitmap it means "partly covered paper", which
    /// <see cref="SyncDeviceCmykBackdropFromRootBitmap"/> resolves; in a group
    /// bitmap it means "the group has not painted here", which
    /// <see cref="SyncDeviceCmykGroupBackdropFromGroupBitmap(int,int,int,int)"/>
    /// leaves alone.</para>
    ///
    /// <para>Every caller is a context whose bitmap it does not choose — the
    /// page context and a #1395 child context run the same code — which is why
    /// the dispatch lives here rather than at the three call sites, two of
    /// which got it wrong.</para>
    /// </summary>
    private void SyncDeviceCmykBackdropForThisBitmap(int left, int top, int width, int height)
    {
        if (_isDeviceCmykGroupBitmap)
            SyncDeviceCmykGroupBackdropFromGroupBitmap(left, top, width, height);
        else
            SyncDeviceCmykBackdropFromRootBitmap(left, top, width, height);
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
    /// Fold Skia-RGB paint in a CHILD group bitmap into the child's retained
    /// CMYK backdrop before the group is composited (#1395).
    ///
    /// <para>Differs from <see cref="SyncDeviceCmykBackdropFromRootBitmap"/> in
    /// the one way a group bitmap differs from a page bitmap: it starts
    /// TRANSPARENT, not on paper. That sync mixes a partially transparent pixel
    /// with white (it is resolving the page's paper), and the composite then
    /// applies the same alpha again — a 50% black soft shadow would come out
    /// 25% dark. Here the straight colour and the pixel's own alpha are stored
    /// as they are, so the composite applies the alpha exactly once.</para>
    ///
    /// <para>Pixels the CMYK paths wrote already agree with the backdrop
    /// (within the same 12-unit tolerance), so CMYK-exact colour is kept; fully
    /// transparent pixels carry nothing and are skipped, which leaves a
    /// non-isolated group's seeded backdrop alone where the group did not
    /// paint. That last property is what makes this — and not
    /// <see cref="SyncDeviceCmykBackdropFromRootBitmap"/> — the sync a child
    /// context may run BEFORE seeding a nested group (#1505), and since #1510
    /// the sync a child context runs in EVERY position, chosen by
    /// <see cref="SyncDeviceCmykBackdropForThisBitmap"/>.</para>
    ///
    /// <para>⚠️ <c>Set</c> replaces colour AND alpha, so a partially
    /// transparent pixel is recorded at its own alpha rather than at the union
    /// with the backdrop it overwrites. That is the right shape for the
    /// pre-composite call (the composite applies the alpha exactly once) and a
    /// known approximation where the value is consumed AS a backdrop — #1513,
    /// which #1510 gives two more call sites and does not change.</para>
    /// </summary>
    private void SyncDeviceCmykGroupBackdropFromGroupBitmap()
    {
        if (_rootBitmap == null || _deviceCmyk.Backdrop == null)
            return;

        SyncDeviceCmykGroupBackdropFromGroupBitmap(
            0,
            0,
            Math.Min(_rootBitmap.Width, _deviceCmyk.Backdrop.Width),
            Math.Min(_rootBitmap.Height, _deviceCmyk.Backdrop.Height));
    }

    /// <summary>
    /// <see cref="SyncDeviceCmykGroupBackdropFromGroupBitmap()"/> over one
    /// region of the group bitmap, in this context's pixel coordinates. The
    /// whole-bitmap overload delegates here with the full extent, so both paths
    /// visit the same pixels in the same order (#1505).
    /// </summary>
    private void SyncDeviceCmykGroupBackdropFromGroupBitmap(int left, int top, int width, int height)
    {
        if (_rootBitmap == null || _deviceCmyk.Backdrop == null)
            return;

        _canvas.Flush();
        var rowBytes = _rootBitmap.RowBytes;
        var pixels = GetRootPixelSpan();
        var right = Math.Min(left + width, Math.Min(_rootBitmap.Width, _deviceCmyk.Backdrop.Width));
        var bottom = Math.Min(top + height, Math.Min(_rootBitmap.Height, _deviceCmyk.Backdrop.Height));
        left = Math.Max(left, 0);
        top = Math.Max(top, 0);

        for (var y = top; y < bottom; y++)
        {
            var rowStart = y * rowBytes;
            for (var x = left; x < right; x++)
            {
                var offset = rowStart + (x * 4);
                var rawAlpha = pixels[offset + 3];
                if (rawAlpha == 0)
                    continue;

                var straightR = UnpremultiplyChannel(pixels[offset], rawAlpha);
                var straightG = UnpremultiplyChannel(pixels[offset + 1], rawAlpha);
                var straightB = UnpremultiplyChannel(pixels[offset + 2], rawAlpha);

                var (retainedR, retainedG, retainedB) = DeviceCmykToRgb(_deviceCmyk.Backdrop.Get(x, y));
                if (Math.Abs(straightR - ToByte(retainedR)) +
                    Math.Abs(straightG - ToByte(retainedG)) +
                    Math.Abs(straightB - ToByte(retainedB)) <= 12)
                {
                    continue;
                }

                _deviceCmyk.Backdrop.Set(
                    x,
                    y,
                    RgbToDeviceCmyk(straightR / 255.0, straightG / 255.0, straightB / 255.0),
                    rawAlpha / 255.0);
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

    /// <summary>
    /// Composite a finished DeviceCMYK child group onto this context's
    /// backdrop and bitmap. <paramref name="maskPlane"/>, when present, is the
    /// group's soft mask in device space aligned with <paramref name="groupBitmap"/>
    /// (one byte per pixel); it multiplies the group alpha BEFORE the
    /// zero-alpha skip, so a fully masked-out pixel never triggers the knockout
    /// reset below (#1395).
    ///
    /// <para><paramref name="seededInitialBackdrop"/>, when present, is the
    /// backdrop a NON-isolated group was seeded from, in this context's pixel
    /// coordinates. Its contribution is removed from the group's accumulated
    /// colour before the group is composited (§11.4.4, #1504) — see
    /// <see cref="RemoveNonIsolatedGroupInitialBackdrop"/>.</para>
    ///
    /// <para><c>/AIS</c> (alpha is shape) is not read. Outside a knockout
    /// parent shape and opacity multiply into the same composite alpha, so it
    /// cannot change this result; inside one it would select which of the two
    /// the knockout test uses.</para>
    /// </summary>
    private void CompositeDeviceCmykGroupBitmap(
        SKBitmap groupBitmap,
        DeviceCmykBackdrop groupBackdrop,
        int left,
        int top,
        SKBlendMode invocationBlendMode,
        float invocationAlpha,
        byte[]? maskPlane = null,
        bool isContainedEntry = false,
        DeviceCmykBackdrop? seededInitialBackdrop = null)
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
                // The group's OWN accumulated alpha: §11.4.4's agn, the result
                // alpha of the group compositing function. The group bitmap
                // starts transparent and only the group's contents paint into
                // it — a non-isolated group's seed goes into the retained CMYK
                // backdrop, never here — so this byte excludes the backdrop.
                // It is what the removal step needs, and it is NOT `alpha`
                // below, which has the invocation's /ca and /SMask folded in;
                // those apply to the finished group as an object (§11.6.6),
                // after the group compositing function has returned.
                var groupAlpha = groupPixels[groupRowStart + (x * 4) + 3] / 255.0;

                // §11.4.6 knocks out on SHAPE, not on opacity: inside a knockout
                // group each element is composited against the group's INITIAL
                // backdrop wherever the element has shape, so an element at
                // /ca 0 contributes no colour and still erases its predecessor.
                // Shape here is the group's own coverage (and the mask, which
                // #1395 deliberately folds in before the skip so a fully
                // masked-out pixel triggers no reset); the invocation's /ca is
                // opacity and belongs only to the colour composite below.
                //
                // #1514: with the /ca override above removed but this skip still
                // keyed on `alpha`, a /ca 0 element was dropped before it could
                // knock anything out and the PRECEDING element survived — the
                // same GWG161 cell then failed on red 122.5 against a floor of
                // 40 instead of on blue. Both halves are needed.
                var knockoutShape = groupAlpha;
                if (maskPlane != null)
                    knockoutShape *= maskPlane[(y * groupWidth) + x] / 255.0;
                var alpha = knockoutShape * clampedInvocationAlpha;
                if (knockoutShape <= 0 || (alpha <= 0 && !_deviceCmyk.IsInKnockoutGroup))
                    continue;

                var parentX = left + x;
                if (parentX < 0 || parentX >= rootWidth)
                    continue;

                // §11.4.4 backdrop removal, BEFORE anything below can write to
                // this pixel of the parent backdrop: in the non-knockout case
                // seededInitialBackdrop IS _deviceCmyk.Backdrop, which the
                // composite mutates pixel by pixel, so C0 has to be read while
                // this pixel still holds what was seeded (#1504).
                var source = groupBackdrop.Get(x, y);
                // groupAlpha == 1 is both the common case (an opaque group hides
                // its backdrop, so removal is a no-op) and the one where the a0
                // derivation is 0/0 — short-circuited here so the parent
                // backdrop is not even read for it.
                if (seededInitialBackdrop != null && groupAlpha < 1)
                {
                    source = RemoveNonIsolatedGroupInitialBackdrop(
                        source,
                        groupBackdrop.GetAlpha(x, y),
                        groupAlpha,
                        seededInitialBackdrop.Get(parentX, parentY));
                }

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

                // The knockout reset has run; a zero-opacity element contributes
                // no colour beyond it (#1514). Deliberately AFTER the reset, and
                // after `source`/the §11.4.4 removal above, which must read C0
                // while this pixel still holds what was seeded (#1504).
                if (alpha <= 0)
                    continue;

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
                if (isContainedEntry && alpha < 1)
                {
                    // The retained CMYK backdrop carries INK, not paper: where nothing
                    // has painted yet its alpha is 0, so Get() returns the group's own
                    // colour at FULL strength and a partial group alpha never lightens
                    // toward the page. Painting straight onto the page that is harmless
                    // — the page IS the backdrop, and DeviceCmykBlendPathRenderTests
                    // pins it — but compositing a GROUP it is not. Measured on Ghent
                    // GWG168's drop shadow: group alpha 0.75 x mask 0.45 produced solid
                    // black where mutool and Ghostscript both draw light grey.
                    var dstR = UnpremultiplyChannel(rootPixels[rootOffset], dstAlphaByte) / 255.0;
                    var dstG = UnpremultiplyChannel(rootPixels[rootOffset + 1], dstAlphaByte) / 255.0;
                    var dstB = UnpremultiplyChannel(rootPixels[rootOffset + 2], dstAlphaByte) / 255.0;
                    var (sourceR, sourceG, sourceB) = DeviceCmykToRgb(blended);
                    r = (sourceR * alpha) + (dstR * (1 - alpha));
                    g = (sourceG * alpha) + (dstG * (1 - alpha));
                    b = (sourceB * alpha) + (dstB * (1 - alpha));
                }

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

    /// <summary>
    /// Remove a NON-isolated transparency group's initial backdrop from the
    /// group's accumulated colour, per ISO 32000-1/-2 <b>§11.4.4</b> "Group
    /// compositing computations" — <b>#1504</b>.
    ///
    /// <para>⚠️ #1504's title and body cite this as §11.4.6. That is wrong in
    /// BOTH editions: §11.4.6 is "Knockout groups" in ISO 32000-1:2008 and in
    /// ISO 32000-2:2020 alike, and the result formula below is §11.4.4 in
    /// both. Checked against the clause text rather than restated (#936).</para>
    ///
    /// <para>The elements of a non-isolated group are composited onto a
    /// backdrop that INCLUDES the group's initial backdrop, so the blend modes
    /// used inside the group have something to act on. The group's result must
    /// therefore have that backdrop taken back out before the result is itself
    /// composited onto the same backdrop, or the backdrop is counted twice.
    /// The clause's result formula is</para>
    ///
    /// <code>
    /// C = Cn + (Cn - C0) * (a0 / agn - a0)     f = fgn     a = agn
    /// </code>
    ///
    /// <para>where <c>Cn</c> is the accumulated colour after the last group
    /// element (backdrop included), <c>C0</c>/<c>a0</c> are the initial
    /// backdrop's colour and alpha, and <c>agn</c> is the group alpha — the
    /// accumulated alpha of the group's elements ONLY. The clause gives the
    /// same operation more intuitively as an inverse Normal composite,
    /// <c>C = (Cn - phi*C0) / (1 - phi)</c> with backdrop fraction
    /// <c>phi = (1 - agn) * a0 / Union(a0, agn)</c>; the form used here is the
    /// spec's own simplification of it.</para>
    ///
    /// <para><b>Why <c>a0</c> is derived and <c>C0</c> is read.</b> The child's
    /// accumulated alpha is <c>an = Union(a0, agn)</c>, so
    /// <c>a0 = (an - agn) / (1 - agn)</c>. Deriving it rather than reading the
    /// parent's alpha is not a shortcut — it is what makes this correct for the
    /// pixels <see cref="SyncDeviceCmykGroupBackdropFromGroupBitmap"/> rewrote.
    /// That sync folds Skia-RGB paint inside the child (text, gray/RGB fills,
    /// fills carrying their own <c>/SMask</c>) into the child backdrop at the
    /// group bitmap's own alpha, i.e. at <c>agn</c> with a colour that never
    /// included the seed. For those pixels the derivation yields
    /// <c>a0 = 0</c> and removal correctly does nothing, where reading the
    /// parent's <c>a0</c> would subtract a backdrop that is not in there.
    /// <c>C0</c> still comes from the seed source, which is the only place the
    /// seeded colour survives.</para>
    ///
    /// <para><b>Numerical behaviour at small <c>agn</c>.</b> The factor grows
    /// like <c>a0/agn</c>, so the 1/255 quantisation of <c>Cn</c> is amplified
    /// by up to 255x — but this colour is then composited at <c>agn</c>, so the
    /// error reaching the page is <c>agn * 0.004 * a0/agn = 0.004 * a0</c>, one
    /// quantisation step, independent of <c>agn</c>. No epsilon floor is
    /// needed and none is used; a floor would silently reinstate the double
    /// count. <c>agn</c> cannot be zero here: the caller has already skipped
    /// the pixel when the composite alpha is zero, and that alpha has
    /// <c>agn</c> as a factor. The clamps are for the gamut, not the algebra —
    /// removal legitimately extrapolates, and a component may land outside
    /// [0,1] when the true source colour sits on the gamut edge.</para>
    ///
    /// <para>Pure arithmetic and <c>internal</c> so the spec property can be
    /// pinned without a renderer: compose per §11.4.4 and remove, and the
    /// group's own source colour comes back —
    /// <c>NonIsolatedGroupBackdropRemovalTests</c>.</para>
    /// </summary>
    internal static DeviceCmykColor RemoveNonIsolatedGroupInitialBackdrop(
        DeviceCmykColor accumulated,
        double accumulatedAlpha,
        double groupAlpha,
        DeviceCmykColor initial)
    {
        // agn == 1 leaves the factor at a0 - a0 = 0 (an opaque group hides its
        // backdrop entirely) and makes the a0 derivation 0/0. Nothing to do.
        // agn <= 0 cannot reach the caller (the composite alpha has agn as a
        // factor and a zero composite alpha skips the pixel), but a direct
        // caller could, and 0 group alpha means 0 group contribution.
        if (groupAlpha >= 1 || groupAlpha <= 0)
            return accumulated;

        // Byte rounding can put `an` a step below `agn` on a pixel that was
        // never seeded, so clamp rather than trusting the subtraction's sign.
        var backdropAlpha = Math.Clamp((accumulatedAlpha - groupAlpha) / (1 - groupAlpha), 0, 1);
        if (backdropAlpha <= 0)
            return accumulated;

        var factor = (backdropAlpha / groupAlpha) - backdropAlpha;
        if (factor <= 0)
            return accumulated;

        return new DeviceCmykColor(
            Math.Clamp(accumulated.C + ((accumulated.C - initial.C) * factor), 0, 1),
            Math.Clamp(accumulated.M + ((accumulated.M - initial.M) * factor), 0, 1),
            Math.Clamp(accumulated.Y + ((accumulated.Y - initial.Y) * factor), 0, 1),
            Math.Clamp(accumulated.K + ((accumulated.K - initial.K) * factor), 0, 1));
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

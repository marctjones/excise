using Excise.Core.Content;
using Excise.Core.Primitives;
using Excise.Rendering.Transparency;

namespace Excise.Rendering;

internal partial class RenderContext
{
    /// <summary>
    /// Executes one already-parsed operator against this render context. There is one
    /// ordered stream walk and one execution entry point: family handlers below share
    /// this context's graphics, text, path, resource, and canvas authority.
    /// </summary>
    private void ExecuteContentOperator(ContentOperator op)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var route = ContentOperatorRoute.Resolve(op.Name);

        // Ordering is semantic, not organizational:
        // 1. Cancellation is observed before any operator can mutate render state.
        // 2. BMC/BDC/EMC always update scope, even while their enclosing optional
        //    content is hidden, so hidden-depth accounting cannot become unbalanced.
        // 3. Optional-content paint suppression can discard a pending paint path.
        // 4. The Type 3 clip-only pass drops shading, whose coverage is not a path.
        // 5. An uncolored Type 3 glyph ignores every color-family operator.
        // 6. Surviving operators enter exactly one reviewed family handler.
        if (route.IsMarkedContentScope)
        {
            ExecuteMarkedContentOperator(route.Kind, op);
            return;
        }

        if (IsOptionalContentSuppressed && SuppressHiddenOptionalContentPaint(route.Kind))
            return;

        if (_type3ClipOnlyPass && route.Kind == ContentOperatorKind.PaintShading)
            return;

        if (_type3GlyphColorLocked && route.IsColorSetting)
            return;

        switch (route.Family)
        {
            case ContentOperatorFamily.MarkedContent:
                ExecuteMarkedContentOperator(route.Kind, op);
                break;
            case ContentOperatorFamily.GraphicsState:
                ExecuteGraphicsStateOperator(route.Kind, op);
                break;
            case ContentOperatorFamily.Color:
                ExecuteColorOperator(route.Kind, op.Operands);
                break;
            case ContentOperatorFamily.Path:
                ExecutePathOperator(route.Kind, op.Operands);
                break;
            case ContentOperatorFamily.Text:
                ExecuteTextOperator(route.Kind, op.Operands);
                break;
            case ContentOperatorFamily.Resource:
                ExecuteResourceOperator(route.Kind, op.Operands);
                break;
            case ContentOperatorFamily.XObjectImage:
                ExecuteXObjectImageOperator(route.Kind, op);
                break;
            case ContentOperatorFamily.Type3:
                ExecuteType3Operator(route.Kind, op);
                break;
            case ContentOperatorFamily.Compatibility:
            case ContentOperatorFamily.Unknown:
            default:
                // Compatibility and unknown operators intentionally have no visual
                // effect. Keeping them routed preserves the old tolerant behavior.
                break;
        }
    }

    private void ExecuteMarkedContentOperator(ContentOperatorKind kind, ContentOperator op)
    {
        switch (kind)
        {
            case ContentOperatorKind.BeginMarkedContent:
                BeginMarkedContent(visible: true);
                break;
            case ContentOperatorKind.BeginMarkedContentWithProperties:
                BeginMarkedContent(ResolveMarkedContentVisibility(op));
                break;
            case ContentOperatorKind.EndMarkedContent:
                EndMarkedContent();
                break;
            case ContentOperatorKind.MarkedContentPoint:
            case ContentOperatorKind.MarkedContentPointWithProperties:
                // Structural point with no visual effect.
                break;
        }
    }

    private void ExecuteGraphicsStateOperator(ContentOperatorKind kind, ContentOperator op)
    {
        var operands = op.Operands;
        switch (kind)
        {
            case ContentOperatorKind.SaveGraphicsState:
                SaveState();
                break;
            case ContentOperatorKind.RestoreGraphicsState:
                RestoreState();
                break;
            case ContentOperatorKind.ConcatenateMatrix:
                if (operands.Count >= 6)
                    ApplyTransform(op);
                break;
            case ContentOperatorKind.SetLineWidth:
                if (operands.Count >= 1)
                    _state.LineWidth = Number(operands, 0);
                break;
            case ContentOperatorKind.SetLineCap:
                if (operands.Count >= 1)
                    _state.LineCap = (int)Number(operands, 0);
                break;
            case ContentOperatorKind.SetLineJoin:
                if (operands.Count >= 1)
                    _state.LineJoin = (int)Number(operands, 0);
                break;
            case ContentOperatorKind.SetMiterLimit:
                if (operands.Count >= 1)
                    _state.MiterLimit = (float)Number(operands, 0);
                break;
            case ContentOperatorKind.SetDashPattern:
                SetDashPattern(operands.Count > 0 ? operands[0] as PdfArray : null, Number(operands, 1));
                break;
            case ContentOperatorKind.SetRenderingIntent:
                // Rendering intent has no effect on rendering for now.
                break;
            case ContentOperatorKind.SetFlatness:
                // Flatness tolerance has no effect on rendering for now.
                break;
        }
    }

    private void ExecuteColorOperator(ContentOperatorKind kind, IReadOnlyList<PdfObject> operands)
    {
        switch (kind)
        {
            case ContentOperatorKind.SetFillGray:
                if (operands.Count >= 1)
                {
                    _state.FillColor = GrayToColor(Number(operands, 0));
                    _state.FillColorSpace = "DeviceGray";
                    _state.FillDeviceCmyk = null;
                    _state.FillPatternName = null;
                }
                break;
            case ContentOperatorKind.SetStrokeGray:
                if (operands.Count >= 1)
                {
                    _state.StrokeColor = GrayToColor(Number(operands, 0));
                    _state.StrokeColorSpace = "DeviceGray";
                    _state.StrokeDeviceCmyk = null;
                }
                break;
            case ContentOperatorKind.SetFillRgb:
                if (operands.Count >= 3)
                {
                    _state.FillColor = RgbToColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2));
                    _state.FillColorSpace = "DeviceRGB";
                    _state.FillDeviceCmyk = null;
                    _state.FillPatternName = null;
                }
                break;
            case ContentOperatorKind.SetStrokeRgb:
                if (operands.Count >= 3)
                {
                    _state.StrokeColor = RgbToColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2));
                    _state.StrokeColorSpace = "DeviceRGB";
                    _state.StrokeDeviceCmyk = null;
                }
                break;
            case ContentOperatorKind.SetFillCmyk:
                if (operands.Count >= 4)
                {
                    var color = new DeviceCmykColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2),
                        Number(operands, 3));
                    _state.FillColor = DeviceCmykToColor(color);
                    _state.FillColorSpace = "DeviceCMYK";
                    _state.FillDeviceCmyk = color;
                    _state.FillPatternName = null;
                }
                break;
            case ContentOperatorKind.SetStrokeCmyk:
                if (operands.Count >= 4)
                {
                    var color = new DeviceCmykColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2),
                        Number(operands, 3));
                    _state.StrokeColor = DeviceCmykToColor(color);
                    _state.StrokeColorSpace = "DeviceCMYK";
                    _state.StrokeDeviceCmyk = color;
                }
                break;
            case ContentOperatorKind.SetStrokeColorSpace:
                if (operands.Count >= 1)
                    _state.StrokeColorSpace = Name(operands, 0);
                break;
            case ContentOperatorKind.SetFillColorSpace:
                if (operands.Count >= 1)
                    _state.FillColorSpace = Name(operands, 0);
                break;
            case ContentOperatorKind.SetStrokeColor:
            case ContentOperatorKind.SetStrokeColorExtended:
                SetStrokingColor(operands);
                break;
            case ContentOperatorKind.SetFillColor:
            case ContentOperatorKind.SetFillColorExtended:
                SetNonStrokingColor(operands);
                break;
        }
    }

    private void ExecutePathOperator(ContentOperatorKind kind, IReadOnlyList<PdfObject> operands)
    {
        switch (kind)
        {
            case ContentOperatorKind.MoveTo:
                if (operands.Count >= 2)
                    MoveTo(Number(operands, 0), Number(operands, 1));
                break;
            case ContentOperatorKind.LineTo:
                if (operands.Count >= 2)
                    LineTo(Number(operands, 0), Number(operands, 1));
                break;
            case ContentOperatorKind.CurveTo:
                if (operands.Count >= 6)
                    CurveTo(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3),
                        Number(operands, 4), Number(operands, 5));
                break;
            case ContentOperatorKind.CurveToV:
                if (operands.Count >= 4)
                    CurveToV(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3));
                break;
            case ContentOperatorKind.CurveToY:
                if (operands.Count >= 4)
                    CurveToY(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3));
                break;
            case ContentOperatorKind.ClosePath:
                ClosePath();
                break;
            case ContentOperatorKind.Rectangle:
                if (operands.Count >= 4)
                    Rectangle(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3));
                break;
            case ContentOperatorKind.StrokePath:
                StrokePath();
                break;
            case ContentOperatorKind.CloseAndStrokePath:
                ClosePath();
                StrokePath();
                break;
            case ContentOperatorKind.FillPath:
            case ContentOperatorKind.FillPathLegacy:
                FillPath(false);
                break;
            case ContentOperatorKind.FillPathEvenOdd:
                FillPath(true);
                break;
            case ContentOperatorKind.FillAndStrokePath:
                FillAndStroke(false);
                break;
            case ContentOperatorKind.FillAndStrokePathEvenOdd:
                FillAndStroke(true);
                break;
            case ContentOperatorKind.CloseFillAndStrokePath:
                ClosePath();
                FillAndStroke(false);
                break;
            case ContentOperatorKind.CloseFillAndStrokePathEvenOdd:
                ClosePath();
                FillAndStroke(true);
                break;
            case ContentOperatorKind.EndPath:
                ApplyPendingClipToCurrentPath();
                _currentPath?.Dispose();
                _currentPath = null;
                break;
            case ContentOperatorKind.ClipPath:
                SetClippingPath(false);
                break;
            case ContentOperatorKind.ClipPathEvenOdd:
                SetClippingPath(true);
                break;
        }
    }

    private void ExecuteTextOperator(ContentOperatorKind kind, IReadOnlyList<PdfObject> operands)
    {
        switch (kind)
        {
            case ContentOperatorKind.BeginText:
                BeginText();
                break;
            case ContentOperatorKind.EndText:
                EndText();
                break;
            case ContentOperatorKind.SetTextFont:
                if (operands.Count >= 2)
                    SetFont(Name(operands, 0), Number(operands, 1));
                break;
            case ContentOperatorKind.MoveText:
                if (operands.Count >= 2)
                    TextMove(Number(operands, 0), Number(operands, 1));
                break;
            case ContentOperatorKind.MoveTextAndSetLeading:
                if (operands.Count >= 2)
                {
                    _textState.TextLeading = -(float)Number(operands, 1);
                    TextMove(Number(operands, 0), Number(operands, 1));
                }
                break;
            case ContentOperatorKind.SetTextMatrix:
                if (operands.Count >= 6)
                    SetTextMatrix(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3),
                        Number(operands, 4), Number(operands, 5));
                break;
            case ContentOperatorKind.MoveToNextTextLine:
                TextNewLine();
                break;
            case ContentOperatorKind.SetCharacterSpacing:
                if (operands.Count >= 1)
                    _textState.CharSpacing = (float)Number(operands, 0);
                break;
            case ContentOperatorKind.SetWordSpacing:
                if (operands.Count >= 1)
                    _textState.WordSpacing = (float)Number(operands, 0);
                break;
            case ContentOperatorKind.SetHorizontalTextScale:
                if (operands.Count >= 1)
                    _textState.HorizontalScale = (float)Number(operands, 0);
                break;
            case ContentOperatorKind.SetTextLeading:
                if (operands.Count >= 1)
                    _textState.TextLeading = (float)Number(operands, 0);
                break;
            case ContentOperatorKind.SetTextRenderMode:
                if (operands.Count >= 1)
                    _textState.RenderMode = (int)Number(operands, 0);
                break;
            case ContentOperatorKind.SetTextRise:
                if (operands.Count >= 1)
                    _textState.TextRise = (float)Number(operands, 0);
                break;
            case ContentOperatorKind.ShowText:
                if (operands.Count >= 1)
                    ShowText(operands[0] as PdfString);
                break;
            case ContentOperatorKind.ShowTextArray:
                ShowTextArray(operands.Count > 0 ? operands[0] as PdfArray : null);
                break;
            case ContentOperatorKind.MoveToNextLineAndShowText:
                TextNewLine();
                if (operands.Count >= 1)
                    ShowText(operands[0] as PdfString);
                break;
            case ContentOperatorKind.SetSpacingMoveAndShowText:
                if (operands.Count >= 3)
                {
                    _textState.WordSpacing = (float)Number(operands, 0);
                    _textState.CharSpacing = (float)Number(operands, 1);
                    TextNewLine();
                    ShowText(operands[2] as PdfString);
                }
                break;
        }
    }

    private void ExecuteResourceOperator(ContentOperatorKind kind, IReadOnlyList<PdfObject> operands)
    {
        switch (kind)
        {
            case ContentOperatorKind.ApplyExtendedGraphicsState:
                if (operands.Count >= 1)
                    ApplyExtGState(Name(operands, 0));
                break;
            case ContentOperatorKind.PaintShading:
                if (operands.Count >= 1)
                    RenderShading(Name(operands, 0));
                break;
        }
    }

    private void ExecuteXObjectImageOperator(ContentOperatorKind kind, ContentOperator op)
    {
        var operands = op.Operands;
        switch (kind)
        {
            case ContentOperatorKind.PaintXObject:
                if (operands.Count >= 1)
                    RenderXObject(Name(operands, 0));
                break;
            case ContentOperatorKind.PaintInlineImage:
                if (operands.Count >= 1
                    && operands[0] is PdfDictionary imageParams
                    && op.InlineImageData is { } inlineImageData)
                    RenderInlineImage(imageParams, inlineImageData);
                break;
        }
    }

    private void ExecuteType3Operator(ContentOperatorKind kind, ContentOperator op)
    {
        switch (kind)
        {
            case ContentOperatorKind.SetType3GlyphWidth:
                // d0's wx metric is consumed by PeekType3CharProcWx when /Widths
                // does not cover the code. A stray d0 outside a CharProc is ignored.
                break;
            case ContentOperatorKind.SetType3GlyphWidthAndBounds:
                if (_type3GlyphStack.Count == 0)
                    break;

                _type3GlyphColorLocked = true;
                ApplyType3GlyphBBoxClip(op);
                break;
        }
    }
}

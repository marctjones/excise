namespace Excise.Rendering;

/// <summary>
/// Stable routing boundary for the PDF operators executed by <see cref="RenderContext"/>.
/// Names are resolved once so the hot path can dispatch without delegate allocations or
/// a second content-stream interpreter.
/// </summary>
internal readonly record struct ContentOperatorRoute(
    ContentOperatorFamily Family,
    ContentOperatorKind Kind)
{
    public static ContentOperatorRoute Resolve(string name) => name switch
    {
        // Marked-content scope operators must run before optional-content paint
        // suppression so a hidden scope can still be exited correctly.
        "BMC" => MarkedContent(ContentOperatorKind.BeginMarkedContent),
        "BDC" => MarkedContent(ContentOperatorKind.BeginMarkedContentWithProperties),
        "EMC" => MarkedContent(ContentOperatorKind.EndMarkedContent),
        "MP" => MarkedContent(ContentOperatorKind.MarkedContentPoint),
        "DP" => MarkedContent(ContentOperatorKind.MarkedContentPointWithProperties),

        // Device-independent graphics state. Resource-backed gs is routed through
        // Resource so its lookup boundary remains visible.
        "q" => GraphicsState(ContentOperatorKind.SaveGraphicsState),
        "Q" => GraphicsState(ContentOperatorKind.RestoreGraphicsState),
        "cm" => GraphicsState(ContentOperatorKind.ConcatenateMatrix),
        "w" => GraphicsState(ContentOperatorKind.SetLineWidth),
        "J" => GraphicsState(ContentOperatorKind.SetLineCap),
        "j" => GraphicsState(ContentOperatorKind.SetLineJoin),
        "M" => GraphicsState(ContentOperatorKind.SetMiterLimit),
        "d" => GraphicsState(ContentOperatorKind.SetDashPattern),
        "ri" => GraphicsState(ContentOperatorKind.SetRenderingIntent),
        "i" => GraphicsState(ContentOperatorKind.SetFlatness),

        "g" => Color(ContentOperatorKind.SetFillGray),
        "G" => Color(ContentOperatorKind.SetStrokeGray),
        "rg" => Color(ContentOperatorKind.SetFillRgb),
        "RG" => Color(ContentOperatorKind.SetStrokeRgb),
        "k" => Color(ContentOperatorKind.SetFillCmyk),
        "K" => Color(ContentOperatorKind.SetStrokeCmyk),
        "CS" => Color(ContentOperatorKind.SetStrokeColorSpace),
        "cs" => Color(ContentOperatorKind.SetFillColorSpace),
        "SC" => Color(ContentOperatorKind.SetStrokeColor),
        "SCN" => Color(ContentOperatorKind.SetStrokeColorExtended),
        "sc" => Color(ContentOperatorKind.SetFillColor),
        "scn" => Color(ContentOperatorKind.SetFillColorExtended),

        "m" => Path(ContentOperatorKind.MoveTo),
        "l" => Path(ContentOperatorKind.LineTo),
        "c" => Path(ContentOperatorKind.CurveTo),
        "v" => Path(ContentOperatorKind.CurveToV),
        "y" => Path(ContentOperatorKind.CurveToY),
        "h" => Path(ContentOperatorKind.ClosePath),
        "re" => Path(ContentOperatorKind.Rectangle),
        "S" => Path(ContentOperatorKind.StrokePath),
        "s" => Path(ContentOperatorKind.CloseAndStrokePath),
        "f" => Path(ContentOperatorKind.FillPath),
        "F" => Path(ContentOperatorKind.FillPathLegacy),
        "f*" => Path(ContentOperatorKind.FillPathEvenOdd),
        "B" => Path(ContentOperatorKind.FillAndStrokePath),
        "B*" => Path(ContentOperatorKind.FillAndStrokePathEvenOdd),
        "b" => Path(ContentOperatorKind.CloseFillAndStrokePath),
        "b*" => Path(ContentOperatorKind.CloseFillAndStrokePathEvenOdd),
        "n" => Path(ContentOperatorKind.EndPath),
        "W" => Path(ContentOperatorKind.ClipPath),
        "W*" => Path(ContentOperatorKind.ClipPathEvenOdd),

        "BT" => Text(ContentOperatorKind.BeginText),
        "ET" => Text(ContentOperatorKind.EndText),
        "Tf" => Text(ContentOperatorKind.SetTextFont),
        "Td" => Text(ContentOperatorKind.MoveText),
        "TD" => Text(ContentOperatorKind.MoveTextAndSetLeading),
        "Tm" => Text(ContentOperatorKind.SetTextMatrix),
        "T*" => Text(ContentOperatorKind.MoveToNextTextLine),
        "Tc" => Text(ContentOperatorKind.SetCharacterSpacing),
        "Tw" => Text(ContentOperatorKind.SetWordSpacing),
        "Tz" => Text(ContentOperatorKind.SetHorizontalTextScale),
        "TL" => Text(ContentOperatorKind.SetTextLeading),
        "Tr" => Text(ContentOperatorKind.SetTextRenderMode),
        "Ts" => Text(ContentOperatorKind.SetTextRise),
        "Tj" => Text(ContentOperatorKind.ShowText),
        "TJ" => Text(ContentOperatorKind.ShowTextArray),
        "'" => Text(ContentOperatorKind.MoveToNextLineAndShowText),
        "\"" => Text(ContentOperatorKind.SetSpacingMoveAndShowText),

        // These operators resolve named page/form resources.
        "gs" => Resource(ContentOperatorKind.ApplyExtendedGraphicsState),
        "sh" => Resource(ContentOperatorKind.PaintShading),

        // Do resolves a named image/form XObject; BI carries its image payload
        // inline but enters the same image execution boundary.
        "Do" => XObjectImage(ContentOperatorKind.PaintXObject),
        "BI" => XObjectImage(ContentOperatorKind.PaintInlineImage),

        "d0" => Type3(ContentOperatorKind.SetType3GlyphWidth),
        "d1" => Type3(ContentOperatorKind.SetType3GlyphWidthAndBounds),

        "BX" => Compatibility(ContentOperatorKind.BeginCompatibility),
        "EX" => Compatibility(ContentOperatorKind.EndCompatibility),

        _ => default,
    };

    public bool IsMarkedContentScope => Kind is
        ContentOperatorKind.BeginMarkedContent or
        ContentOperatorKind.BeginMarkedContentWithProperties or
        ContentOperatorKind.EndMarkedContent;

    public bool IsColorSetting => Family == ContentOperatorFamily.Color;

    private static ContentOperatorRoute MarkedContent(ContentOperatorKind kind)
        => new(ContentOperatorFamily.MarkedContent, kind);

    private static ContentOperatorRoute GraphicsState(ContentOperatorKind kind)
        => new(ContentOperatorFamily.GraphicsState, kind);

    private static ContentOperatorRoute Color(ContentOperatorKind kind)
        => new(ContentOperatorFamily.Color, kind);

    private static ContentOperatorRoute Path(ContentOperatorKind kind)
        => new(ContentOperatorFamily.Path, kind);

    private static ContentOperatorRoute Text(ContentOperatorKind kind)
        => new(ContentOperatorFamily.Text, kind);

    private static ContentOperatorRoute Resource(ContentOperatorKind kind)
        => new(ContentOperatorFamily.Resource, kind);

    private static ContentOperatorRoute XObjectImage(ContentOperatorKind kind)
        => new(ContentOperatorFamily.XObjectImage, kind);

    private static ContentOperatorRoute Type3(ContentOperatorKind kind)
        => new(ContentOperatorFamily.Type3, kind);

    private static ContentOperatorRoute Compatibility(ContentOperatorKind kind)
        => new(ContentOperatorFamily.Compatibility, kind);
}

internal enum ContentOperatorFamily
{
    Unknown,
    MarkedContent,
    GraphicsState,
    Color,
    Path,
    Text,
    Resource,
    XObjectImage,
    Type3,
    Compatibility,
}

internal enum ContentOperatorKind
{
    Unknown,
    BeginMarkedContent,
    BeginMarkedContentWithProperties,
    EndMarkedContent,
    MarkedContentPoint,
    MarkedContentPointWithProperties,
    SaveGraphicsState,
    RestoreGraphicsState,
    ConcatenateMatrix,
    SetLineWidth,
    SetLineCap,
    SetLineJoin,
    SetMiterLimit,
    SetDashPattern,
    SetRenderingIntent,
    SetFlatness,
    SetFillGray,
    SetStrokeGray,
    SetFillRgb,
    SetStrokeRgb,
    SetFillCmyk,
    SetStrokeCmyk,
    SetStrokeColorSpace,
    SetFillColorSpace,
    SetStrokeColor,
    SetStrokeColorExtended,
    SetFillColor,
    SetFillColorExtended,
    MoveTo,
    LineTo,
    CurveTo,
    CurveToV,
    CurveToY,
    ClosePath,
    Rectangle,
    StrokePath,
    CloseAndStrokePath,
    FillPath,
    FillPathLegacy,
    FillPathEvenOdd,
    FillAndStrokePath,
    FillAndStrokePathEvenOdd,
    CloseFillAndStrokePath,
    CloseFillAndStrokePathEvenOdd,
    EndPath,
    ClipPath,
    ClipPathEvenOdd,
    BeginText,
    EndText,
    SetTextFont,
    MoveText,
    MoveTextAndSetLeading,
    SetTextMatrix,
    MoveToNextTextLine,
    SetCharacterSpacing,
    SetWordSpacing,
    SetHorizontalTextScale,
    SetTextLeading,
    SetTextRenderMode,
    SetTextRise,
    ShowText,
    ShowTextArray,
    MoveToNextLineAndShowText,
    SetSpacingMoveAndShowText,
    ApplyExtendedGraphicsState,
    PaintShading,
    PaintXObject,
    PaintInlineImage,
    SetType3GlyphWidth,
    SetType3GlyphWidthAndBounds,
    BeginCompatibility,
    EndCompatibility,
}

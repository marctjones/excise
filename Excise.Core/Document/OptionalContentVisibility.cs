using Excise.Core.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Document;

/// <summary>
/// Resolves the default-configuration visibility of optional content referenced
/// from a marked-content <c>/OC</c> span (BDC). Handles Optional Content Groups
/// (<c>/Type /OCG</c>) and Optional Content Membership Dictionaries
/// (<c>/Type /OCMD</c>, including <c>/P</c> visibility policy and <c>/VE</c>
/// And/Or/Not visibility expressions), per ISO 32000-2 §8.11.
///
/// OCG membership is matched by object reference against the catalog
/// <c>/OCProperties /D</c> default configuration (<c>/OFF</c>, <c>/ON</c>,
/// <c>/BaseState</c>), so two OCGs that share a <c>/Name</c> are distinguished.
///
/// This is the single source of truth shared by text extraction (which sets
/// <see cref="Excise.Core.Text.Letter.IsInHiddenOptionalContent"/>) and any other
/// consumer that must agree with a compliant viewer on which layers are hidden by
/// default. See issue #336. The SkiaSharp renderer carries an equivalent
/// resolver (Excise.Rendering/SkiaRenderer.cs); collapsing it onto this one is a
/// separate render-test-gated refactor.
/// </summary>
internal static class OptionalContentVisibility
{
    /// <summary>
    /// Returns <c>true</c> if content marked with the given optional-content
    /// property object is visible in the document's default configuration.
    /// Non-optional-content objects (and anything that cannot be resolved) are
    /// treated as visible.
    /// </summary>
    /// <param name="document">The owning document (for reference resolution).</param>
    /// <param name="ocPropertyObject">
    /// The property object named by the <c>/OC</c> span, taken from the active
    /// <c>/Properties</c> resource. Pass it <b>un-resolved</b> (typically a
    /// <see cref="PdfReference"/>) so reference-identity matching against the
    /// default configuration's <c>/OFF</c>/<c>/ON</c> arrays works.
    /// </param>
    public static bool IsVisibleByDefault(PdfDocument document, PdfObject? ocPropertyObject)
    {
        if (ocPropertyObject == null)
            return true;

        PdfObject resolved;
        try { resolved = document.Resolve(ocPropertyObject); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return true; }

        // Rare: a /Properties entry that is a bare name rather than an OCG dict.
        // Preserve the historical name-based lookup for this edge case.
        if (resolved is PdfName directName)
            return !document.GetOptionalContentGroupConfig().OffByDefault.Contains(directName.Value);

        if (resolved is not PdfDictionary dict)
            return true;

        return dict.GetNameOrNull("Type") switch
        {
            "OCG" => IsOptionalContentGroupVisible(document, ocPropertyObject, dict),
            "OCMD" => IsMembershipVisible(document, dict),
            // Some producers point /OC at a wrapper dict carrying a nested /OC.
            _ => dict.GetOptional("OC") is { } nested
                ? IsVisibleByDefault(document, nested)
                : true,
        };
    }

    private static bool IsOptionalContentGroupVisible(PdfDocument document, PdfObject ocgObject, PdfDictionary ocg)
    {
        var defaultConfig = GetDefaultConfig(document);
        if (defaultConfig == null)
            return true;

        if (IsOcgListed(document, defaultConfig.GetOptional("OFF"), ocgObject, ocg))
            return false;

        if (IsOcgListed(document, defaultConfig.GetOptional("ON"), ocgObject, ocg))
            return true;

        // OCGs not named in ON or OFF follow BaseState (default ON).
        return !string.Equals(defaultConfig.GetNameOrNull("BaseState"), "OFF", StringComparison.Ordinal);
    }

    private static bool IsMembershipVisible(PdfDocument document, PdfDictionary membership)
    {
        // A visibility expression, when present, takes precedence over /P (§8.11.2.3).
        if (membership.GetOptional("VE") is { } visibilityExpression)
            return EvaluateVisibilityExpression(document, visibilityExpression);

        var ocgsObj = membership.GetOptional("OCGs");
        if (ocgsObj == null)
            return true;

        var visibilities = new List<bool>();
        var resolvedOcgs = document.Resolve(ocgsObj);
        if (resolvedOcgs is PdfArray ocgArray)
        {
            foreach (var ocg in ocgArray)
                visibilities.Add(IsVisibleByDefault(document, ocg));
        }
        else
        {
            visibilities.Add(IsVisibleByDefault(document, ocgsObj));
        }

        if (visibilities.Count == 0)
            return true;

        var policy = membership.GetNameOrNull("P") ?? "AnyOn";
        return policy switch
        {
            "AllOn" => visibilities.All(v => v),
            "AnyOff" => visibilities.Any(v => !v),
            "AllOff" => visibilities.All(v => !v),
            _ => visibilities.Any(v => v),  // AnyOn (default)
        };
    }

    private static bool EvaluateVisibilityExpression(PdfDocument document, PdfObject expressionObject)
    {
        var resolved = document.Resolve(expressionObject);

        // A leaf of the expression tree is an OCG (or OCMD) dictionary.
        if (resolved is PdfDictionary dict)
            return IsVisibleByDefault(document, dict);

        if (resolved is not PdfArray expression || expression.Count == 0)
            return true;

        if (expression[0] is not PdfName op)
            return true;

        return op.Value switch
        {
            "And" => VisibilityOperands(document, expression).All(v => v),
            "Or" => VisibilityOperands(document, expression).Any(v => v),
            "Not" => expression.Count < 2 || !EvaluateVisibilityExpression(document, expression[1]),
            _ => true,
        };
    }

    private static IEnumerable<bool> VisibilityOperands(PdfDocument document, PdfArray expression)
    {
        for (var i = 1; i < expression.Count; i++)
            yield return EvaluateVisibilityExpression(document, expression[i]);
    }

    private static PdfDictionary? GetDefaultConfig(PdfDocument document)
    {
        var ocPropsObj = document.Catalog.GetOptional("OCProperties");
        if (document.Resolve(ocPropsObj ?? PdfNull.Instance) is not PdfDictionary ocProps)
            return null;

        return document.Resolve(ocProps.GetOptional("D") ?? PdfNull.Instance) as PdfDictionary;
    }

    private static bool IsOcgListed(PdfDocument document, PdfObject? listObject, PdfObject ocgObject, PdfDictionary ocg)
    {
        if (document.Resolve(listObject ?? PdfNull.Instance) is not PdfArray list)
            return false;

        foreach (var item in list)
        {
            if (ReferencesSameObject(document, item, ocgObject, ocg))
                return true;
        }

        return false;
    }

    private static bool ReferencesSameObject(PdfDocument document, PdfObject item, PdfObject ocgObject, PdfDictionary ocg)
    {
        if (item is PdfReference itemRef && ocgObject is PdfReference ocgRef)
            return itemRef == ocgRef;

        if (item is PdfReference refItem &&
            ocg.ObjectNumber == refItem.ObjectNum &&
            ocg.GenerationNumber == refItem.Generation)
            return true;

        var resolvedItem = document.Resolve(item);
        if (resolvedItem is PdfDictionary itemDict)
        {
            if (itemDict.ObjectNumber.HasValue && ocg.ObjectNumber.HasValue)
                return itemDict.ObjectNumber == ocg.ObjectNumber &&
                       itemDict.GenerationNumber == ocg.GenerationNumber;

            return ReferenceEquals(itemDict, ocg);
        }

        return false;
    }
}

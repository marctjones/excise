using Excise.Core.Primitives;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Document;

/// <summary>
/// Parser for Optional Content Groups (OCGs) from the PDF catalog.
/// ISO 32000-2 §8.11 specifies the /OCProperties structure in the document catalog.
/// </summary>
internal static class PdfOcgParser
{
    /// <summary>
    /// Parse optional content groups from the document catalog.
    /// Returns (ocgs, config) tuple, or (empty list, empty config) if no /OCProperties.
    /// </summary>
    public static (IReadOnlyList<PdfOcg> ocgs, PdfOcgConfig config) ParseOptionalContentGroups(PdfDocument doc)
    {
        var ocPropsObj = doc.Catalog.GetOptional("OCProperties");
        if (ocPropsObj == null)
            return (System.Array.Empty<PdfOcg>(), new PdfOcgConfig(System.Array.Empty<PdfOcg>(), System.Collections.Immutable.ImmutableHashSet<string>.Empty));

        var ocPropsDict = doc.Resolve(ocPropsObj) as PdfDictionary;
        if (ocPropsDict == null)
            return (System.Array.Empty<PdfOcg>(), new PdfOcgConfig(System.Array.Empty<PdfOcg>(), System.Collections.Immutable.ImmutableHashSet<string>.Empty));

        // Parse OCGs array
        var ocgsArray = ocPropsDict.GetOptional("OCGs");
        var ocgs = ParseOcgsArray(doc, ocgsArray);

        // Parse default configuration
        var defaultConfigObj = ocPropsDict.GetOptional("D");
        var config = ParseDefaultConfig(doc, defaultConfigObj, ocgs);

        return (ocgs, config);
    }

    /// <summary>
    /// Parse the /OCGs array, which contains references to OCG dictionaries.
    /// </summary>
    private static IReadOnlyList<PdfOcg> ParseOcgsArray(PdfDocument doc, PdfObject? ocgsArrayObj)
    {
        if (ocgsArrayObj == null) return System.Array.Empty<PdfOcg>();

        var ocgsArray = doc.Resolve(ocgsArrayObj) as PdfArray;
        if (ocgsArray == null) return System.Array.Empty<PdfOcg>();

        var result = new List<PdfOcg>();
        foreach (var item in ocgsArray)
        {
            var ocgDict = doc.Resolve(item) as PdfDictionary;
            if (ocgDict == null) continue;

            var ocg = ParseOcgDictionary(ocgDict);
            if (ocg != null)
                result.Add(ocg);
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Parse a single OCG dictionary.
    /// Required: /Name (string or text stream)
    /// Optional: /Type (should be /OCG), /Intent, /Usage, etc.
    /// </summary>
    private static PdfOcg? ParseOcgDictionary(PdfDictionary dict)
    {
        // Get the OCG name
        var nameObj = dict.GetOptional("Name");
        if (nameObj == null) return null;

        var name = nameObj switch
        {
            PdfString str => str.Value,
            PdfName n => n.Value,
            _ => null
        };

        if (string.IsNullOrEmpty(name)) return null;

        // For now, we assume all OCGs are visible by default unless explicitly turned off
        // in the default config. The per-OCG visibility is determined by the config.
        return new PdfOcg(name, true, dict);
    }

    /// <summary>
    /// Parse the default configuration dictionary (/Catalog/OCProperties/D).
    /// This dict may contain /ON, /OFF, /Intent, /BaseState, /Order, etc.
    /// </summary>
    private static PdfOcgConfig ParseDefaultConfig(
        PdfDocument doc,
        PdfObject? defaultObj,
        IReadOnlyList<PdfOcg> allOcgs)
    {
        if (defaultObj == null)
            return new PdfOcgConfig(allOcgs, System.Collections.Immutable.ImmutableHashSet<string>.Empty);

        var defaultDict = doc.Resolve(defaultObj) as PdfDictionary;
        if (defaultDict == null)
            return new PdfOcgConfig(allOcgs, System.Collections.Immutable.ImmutableHashSet<string>.Empty);

        // Parse /OFF array (OCGs that are OFF by default)
        var offSet = ParseOcgNameArray(doc, defaultDict.GetOptional("OFF"));

        // Parse /Intent
        var intentObj = defaultDict.GetOptional("Intent");
        var intent = intentObj switch
        {
            PdfName n => n.Value,
            _ => "View"
        };

        // Parse /BaseState
        var baseStateObj = defaultDict.GetOptional("BaseState");
        var baseState = baseStateObj switch
        {
            PdfName n => n.Value,
            _ => "ON"
        };

        // Parse /Order (layer hierarchy) — treat as opaque for now
        var layerOrder = defaultDict.GetOptional("Order");

        return new PdfOcgConfig(allOcgs, offSet, intent, baseState, layerOrder);
    }

    /// <summary>
    /// Parse an array of OCG references/names and return the set of OCG names.
    /// Each element can be either:
    /// - A reference to an OCG dictionary
    /// - A name (rare)
    /// - An array (for hierarchical layer order)
    /// </summary>
    private static IReadOnlySet<string> ParseOcgNameArray(PdfDocument doc, PdfObject? arrayObj)
    {
        if (arrayObj == null) return System.Collections.Immutable.ImmutableHashSet<string>.Empty;

        var array = doc.Resolve(arrayObj) as PdfArray;
        if (array == null) return System.Collections.Immutable.ImmutableHashSet<string>.Empty;

        var result = new HashSet<string>();

        foreach (var item in array)
        {
            // Try to resolve as OCG dict and extract name
            var ocgDict = doc.Resolve(item) as PdfDictionary;
            if (ocgDict != null)
            {
                var nameObj = ocgDict.GetOptional("Name");
                if (nameObj is PdfString str)
                    result.Add(str.Value);
                else if (nameObj is PdfName name)
                    result.Add(name.Value);
            }
            // Also handle direct name objects
            else if (item is PdfName directName)
            {
                result.Add(directName.Value);
            }
        }

        return result;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// RECORDS what excise's own object API exposes for every ISO 32000-2 key the
/// Arlington model defines, over a corpus of real documents. It asserts almost
/// nothing on purpose (#1703).
///
/// WHY A RECORDER AND NOT A TEST. The capability registry says what excise
/// supports because a person wrote a row saying so and cited a test they also
/// wrote. That is how one round-trip check came to carry 45% of the "verified"
/// score for two claims it could not make. 3,973 keys cannot be hand-cited,
/// and hand-cited status is exactly what drifts. So status here is OBSERVED:
/// the spec model supplies the questions, real documents supply the input, and
/// excise's answer is written down rather than claimed.
///
/// HOW THE TRAVERSAL IS TYPED. Arlington's Link column gives the object types
/// each key may point at, so the walk starts at FileTrailer and descends the
/// SPEC's graph, not a guessed one: at each node we know which Arlington
/// object we are standing on and therefore exactly which keys are defined
/// there. A key is recorded as:
///
///   surfaced  — excise's parser exposes it AND the value's kind is compatible
///               with the Type the model declares. Presence alone is not
///               enough: exposing a dictionary where an array is required is
///               not "recognize, validate and expose".
///   typeMismatch — exposed, but the wrong kind. A finding, not a pass.
///   absent    — the containing object was visited and the key was not there.
///               Says nothing about excise: the key is simply optional and
///               this document did not use it.
///
/// A key whose containing object was never reached in any document is
/// UNMEASURED and is reported as such rather than counted either way.
///
/// This measures parse-level exposure only. It is deliberately not evidence
/// that any key is handled CORRECTLY -- that needs an oracle, and belongs in
/// the targeted differentials.
/// </summary>
public class ArlingtonKeyObservationTests
{
    private const string EnableVar = "EXCISE_OBSERVE_ARLINGTON";

    [Fact]
    public void ObserveArlingtonKeyExposureAcrossTheCorpus()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(EnableVar) != "1",
            $"recorder, not a gate: set {EnableVar}=1 to regenerate the observation " +
            "(scripts/observe-arlington-keys.sh)");

        var inventoryPath = TestRepoLayout.FindFile("test-pdfs/manifests/arlington-inventory.json");
        Assert.SkipWhen(inventoryPath == null, TestRepoLayout.AbsenceReason(
            "arlington-inventory.json (scripts/build-arlington-inventory.py)",
            "test-pdfs/manifests/arlington-inventory.json"));

        var corpus = TestRepoLayout.FindDirectory("test-pdfs/smoke");
        Assert.SkipWhen(corpus == null, TestRepoLayout.AbsenceReason(
            "smoke corpus (scripts/download-smoke-corpus.sh)", "test-pdfs/smoke"));

        var model = LoadModel(inventoryPath!);
        var observation = new Dictionary<string, KeyObservation>(StringComparer.Ordinal);
        var visitedObjects = new Dictionary<string, int>(StringComparer.Ordinal);
        var ambiguousLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var documents = Directory.GetFiles(corpus!, "*.pdf").OrderBy(p => p).ToList();
        var perDocument = new List<object>();

        foreach (var path in documents)
        {
            try
            {
                using var doc = PdfDocument.Open(path);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var walked = new HashSet<(string, PdfDictionary)>();
                Walk(doc, "FileTrailer", doc.Trailer, model, observation, seen,
                     visitedObjects, walked, ambiguousLinks, depth: 0,
                     currentFile: Path.GetFileName(path));
                perDocument.Add(new { file = Path.GetFileName(path), keysSurfaced = seen.Count });
            }
            catch (Exception ex)
            {
                perDocument.Add(new { file = Path.GetFileName(path), error = ex.GetType().Name });
            }
        }

        var rows = observation.Values
            .OrderByDescending(o => o.Documents).ThenByDescending(o => o.Occurrences)
            .ThenBy(o => o.Object, StringComparer.Ordinal)
            .ThenBy(o => o.Key, StringComparer.Ordinal)
            .Select(o => new
            {
                o.Object, o.Key, o.Type, o.SinceVersion, o.NewInPdf20,
                o.AlwaysInScope, o.Occurrences, o.Documents, o.TypeMismatches,
            })
            .ToList();

        var outPath = Path.Combine(Path.GetDirectoryName(inventoryPath!)!,
                                   "arlington-observation.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            generatedBy = "Excise.Rendering.Tests/Differential/ArlingtonKeyObservationTests.cs",
            policy = "Observed exposure of ISO 32000-2 object-model keys by excise's own " +
                     "parser over a real-document corpus. Parse-level exposure only; not " +
                     "evidence of correct handling.",
            corpus = new { directory = "test-pdfs/smoke", documents = documents.Count },
            summary = new
            {
                modelKeys = model.Sum(kv => kv.Value.Count),
                objectsVisited = visitedObjects.Count,
                keysSurfacedAtLeastOnce = rows.Count(r => r.Occurrences > 0),
                keysWithATypeMismatch = rows.Count(r => r.TypeMismatches > 0),
                ambiguousLinkSites = ambiguousLinks.Count,
            },
            perDocument,
            objectsVisited = visitedObjects.OrderByDescending(kv => kv.Value)
                                           .ToDictionary(kv => kv.Key, kv => kv.Value),
            ambiguousLinks = ambiguousLinks.OrderByDescending(kv => kv.Value)
                                           .ToDictionary(kv => kv.Key, kv => kv.Value),
            keys = rows,
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");

        // The ONE assertion, and it is about the recorder rather than about
        // excise: a run that reached nothing would write an empty file that
        // reads exactly like "excise exposes nothing", which is the #1527
        // shape. Everything else is data.
        rows.Count(r => r.Occurrences > 0).Should_BeAtLeast(50, outPath);
    }

    private static void Walk(
        PdfDocument doc, string objectName, PdfDictionary dict,
        IReadOnlyDictionary<string, List<ModelKey>> model,
        Dictionary<string, KeyObservation> observation,
        HashSet<string> seenThisDoc,
        Dictionary<string, int> visitedObjects,
        HashSet<(string, PdfDictionary)> walked,
        Dictionary<string, int> ambiguous,
        int depth,
        string currentFile = "")
    {
        // The spec graph has cycles (Page -> Parent -> Kids) AND diamonds (many
        // objects link to the same Resources). Bounding by depth alone is not
        // enough -- measured, the first version of this did not finish on a
        // 10-document corpus in two minutes, because a diamond re-walks the
        // same subtree once per path that reaches it. Memoising on the
        // (Arlington type, actual object) pair makes it linear in the graph:
        // each object is examined once per type it is reached as, which is the
        // question being asked. Reference equality is what we want -- two
        // distinct dictionaries with equal content are two objects.
        if (depth > 24 || !model.TryGetValue(objectName, out var keys)) return;
        if (!walked.Add((objectName, dict))) return;
        visitedObjects[objectName] = visitedObjects.GetValueOrDefault(objectName) + 1;

        foreach (var mk in keys)
        {
            var id = $"{objectName}.{mk.Key}";
            var row = observation.TryGetValue(id, out var existing) ? existing
                    : observation[id] = new KeyObservation(objectName, mk);

            var raw = mk.Key == "*" ? null : dict.GetOptional(mk.Key);
            if (raw == null) continue;

            PdfObject? value;
            try { value = doc.Resolve(raw); } catch { continue; }
            if (value == null) continue;

            if (TypeMatches(mk.Type, value)) { row.Occurrences++; row.DocumentSet.Add(currentFile); seenThisDoc.Add(id); }
            else row.TypeMismatches++;

            if (value is PdfDictionary child)
                Descend(doc, mk.Links, child, model, observation, seenThisDoc,
                        visitedObjects, walked, ambiguous, depth, currentFile);
            else if (value is PdfArray arr)
                foreach (var item in arr.Take(32))
                    if (doc.Resolve(item) is PdfDictionary ad)
                        Descend(doc, mk.Links, ad, model, observation, seenThisDoc,
                                visitedObjects, walked, ambiguous, depth, currentFile);
        }
    }

    /// <summary>
    /// Walks <paramref name="child"/> as the ONE Arlington type it actually
    /// is, never as all the types the Link column allows.
    ///
    /// ⚠️ This is the difference between findings and noise, and the first
    /// version of this file got it wrong. XObjectMap.* links to four types
    /// (XObjectFormPS, XObjectFormPSpassthrough, XObjectFormType1,
    /// XObjectImage). Walking every candidate examined each XObject as all
    /// four, so keys legitimately absent from the type an object actually is
    /// were recorded as excise exposing the wrong kind -- three "type
    /// mismatches" in the first run, every one of them manufactured here
    /// rather than found in excise.
    ///
    /// So: disambiguate on the object's own /Subtype or /Type. If exactly one
    /// candidate survives, descend into it. If the link is unambiguous to
    /// begin with, descend. Otherwise descend into NOTHING and count it --
    /// under-reporting is recoverable, a false accusation against the product
    /// is not, and a silent one is worse still.
    /// </summary>
    private static void Descend(
        PdfDocument doc, IReadOnlyList<string> links, PdfDictionary child,
        IReadOnlyDictionary<string, List<ModelKey>> model,
        Dictionary<string, KeyObservation> observation,
        HashSet<string> seenThisDoc,
        Dictionary<string, int> visitedObjects,
        HashSet<(string, PdfDictionary)> walked,
        Dictionary<string, int> ambiguous,
        int depth,
        string currentFile = "")
    {
        if (links.Count == 0) return;

        var chosen = links.Count == 1 ? links[0] : Disambiguate(links, child);
        if (chosen == null)
        {
            var id = string.Join("|", links);
            ambiguous[id] = ambiguous.GetValueOrDefault(id) + 1;
            return;
        }
        Walk(doc, chosen, child, model, observation, seenThisDoc,
             visitedObjects, walked, ambiguous, depth + 1, currentFile);
    }

    /// <summary>
    /// The single Arlington type <paramref name="dict"/> actually is, or null
    /// when that cannot be decided — in which case the caller descends into
    /// nothing rather than guessing.
    ///
    /// The rules are structural or spec-defined, never heuristic:
    ///
    ///  • An "ArrayOf..." candidate cannot describe a dictionary. Dropping
    ///    those resolves ArrayOfStructElem|StructElem outright.
    ///  • §12.6.3: an action's /S names its subtype, which is what separates
    ///    the ~20 Action* candidates. The Arlington name is not always the /S
    ///    value (/S /JavaScript is ActionECMAScript), so the exceptions are
    ///    listed rather than pattern-matched.
    ///  • §14.7.2: /Type distinguishes the structure-tree trio, again with
    ///    Arlington names that differ from the PDF names (/MCR, /OBJR).
    ///  • Otherwise a candidate whose name contains the /Subtype or /Type
    ///    value, when exactly one does.
    /// </summary>
    private static string? Disambiguate(IReadOnlyList<string> links, PdfDictionary dict)
    {
        var candidates = links.Where(l => !l.StartsWith("ArrayOf", StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0) candidates = links.ToList();
        if (candidates.Count == 1) return candidates[0];

        if (dict.GetOptional("S") is PdfName s &&
            candidates.Any(c => c.StartsWith("Action", StringComparison.Ordinal)))
        {
            var wanted = s.Value switch
            {
                "JavaScript" => "ActionECMAScript",
                "SetOCGState" => "ActionSetOCGState",
                "Rendition" => "ActionRendition",
                "Trans" => "ActionTransition",
                _ => "Action" + s.Value,
            };
            var hit = candidates.FirstOrDefault(c => string.Equals(c, wanted, StringComparison.Ordinal));
            if (hit != null) return hit;
        }

        if (dict.GetOptional("Type") is PdfName t)
        {
            var alias = t.Value switch
            {
                "MCR" => "MarkedContentReference",
                "OBJR" => "ObjectReference",
                "Outlines" => "Outline",
                _ => t.Value,
            };
            var exact = candidates.Where(c => string.Equals(c, alias, StringComparison.Ordinal)).ToList();
            if (exact.Count == 1) return exact[0];
        }

        foreach (var discriminator in new[] { "Subtype", "Type" })
        {
            if (dict.GetOptional(discriminator) is not PdfName n) continue;
            var matches = candidates
                .Where(l => l.Contains(n.Value, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1) return matches[0];
        }

        // §12.3.3: an outline ROOT has no /Title; an item must have one.
        if (candidates.Count == 2 && candidates.Contains("Outline") && candidates.Contains("OutlineItem"))
            return dict.ContainsKey("Title") ? "OutlineItem" : "Outline";

        return null;
    }

    /// <summary>
    /// Arlington's Type cell is a semicolon-separated set of permitted types
    /// ("dictionary;stream"). A value matches if it is any one of them. Types
    /// excise has no distinct representation for are accepted rather than
    /// reported as mismatches: a false finding here would be worse than a
    /// missing one, because the whole point is that this file can be trusted
    /// without re-deriving it.
    /// </summary>
    private static bool TypeMatches(string declared, PdfObject value)
    {
        foreach (var t in declared.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = t.Trim().ToLowerInvariant();
            if (name.StartsWith("fn:")) return true;
            var ok = name switch
            {
                "dictionary" => value is PdfDictionary and not PdfStream,
                "stream" => value is PdfStream,
                "array" => value is PdfArray,
                "name" => value is PdfName,
                "string" or "string-text" or "string-byte" or "string-ascii"
                    => value is PdfString,
                "integer" => value is PdfInteger,
                "number" => value is PdfInteger or PdfReal,
                "boolean" => value is PdfBoolean,
                "null" => value is PdfNull,
                "date" => value is PdfString,
                "rectangle" or "matrix" => value is PdfArray,
                "name-tree" or "number-tree" => value is PdfDictionary,
                "bitmask" => value is PdfInteger,
                _ => true,
            };
            if (ok) return true;
        }
        return false;
    }

    private static Dictionary<string, List<ModelKey>> LoadModel(string path)
    {
        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream);
        var model = new Dictionary<string, List<ModelKey>>(StringComparer.Ordinal);
        foreach (var e in json.RootElement.GetProperty("entries").EnumerateArray())
        {
            var obj = e.GetProperty("object").GetString()!;
            if (!model.TryGetValue(obj, out var list)) model[obj] = list = new List<ModelKey>();
            list.Add(new ModelKey(
                e.GetProperty("key").GetString()!,
                e.GetProperty("type").GetString() ?? "",
                e.GetProperty("sinceVersion").GetString() ?? "",
                e.GetProperty("newInPdf20").GetBoolean(),
                e.GetProperty("alwaysInScope").GetBoolean(),
                e.GetProperty("links").EnumerateArray().Select(l => l.GetString()!).ToArray()));
        }
        return model;
    }

    private sealed record ModelKey(string Key, string Type, string SinceVersion,
                                   bool NewInPdf20, bool AlwaysInScope, string[] Links);

    private sealed class KeyObservation
    {
        public KeyObservation(string obj, ModelKey mk)
        {
            Object = obj; Key = mk.Key; Type = mk.Type;
            SinceVersion = mk.SinceVersion; NewInPdf20 = mk.NewInPdf20;
            AlwaysInScope = mk.AlwaysInScope;
        }
        public string Object { get; }
        public string Key { get; }
        public string Type { get; }
        public string SinceVersion { get; }
        public bool NewInPdf20 { get; }
        public bool AlwaysInScope { get; }
        public int Occurrences { get; set; }
        public int TypeMismatches { get; set; }
        public HashSet<string> DocumentSet { get; } = new(StringComparer.Ordinal);
        public int Documents => DocumentSet.Count;
    }
}

internal static class ObservationAssertions
{
    public static void Should_BeAtLeast(this int actual, int floor, string outPath)
        => Assert.True(actual >= floor,
            $"the recorder reached only {actual} keys (floor {floor}); an empty or nearly " +
            $"empty observation reads identically to 'excise exposes nothing'. See {outPath}");
}

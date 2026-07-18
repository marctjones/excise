using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Inlines Form XObjects (<c>Do</c> targets whose <c>/Subtype</c> is
/// <c>/Form</c>) into a page's operator list so that the text and graphics
/// they draw become part of the page content stream. This is the
/// "flatten" half of the flatten-then-redact contract for #355: the
/// existing <see cref="GlyphRemover"/> and <see cref="ImageRedactor"/>
/// passes only see the page stream, so without flattening any content a
/// form paints over the redaction area is merely covered, not removed —
/// a security leak.
/// </summary>
/// <remarks>
/// <para>
/// Invoking a form via <c>Do</c> is equivalent to (ISO 32000-2 §8.10.1):
/// <c>q</c> · concat <c>/Matrix</c> to the CTM · clip to <c>/BBox</c> ·
/// paint the form's content stream · <c>Q</c>. We reproduce that exactly,
/// emitting <c>q  &lt;Matrix&gt; cm  &lt;BBox&gt; re W n  …form ops…  Q</c>,
/// so the flattened result renders identically to the original.
/// </para>
/// <para>
/// Forms carry their own <c>/Resources</c>. Their content references
/// resource names (<c>/F1</c>, <c>/Im0</c>, …) that are private to the
/// form's namespace and frequently collide with same-spelled page
/// resources pointing at <em>different</em> objects. We merge each form's
/// resources into the page's, renaming on collision, and rewrite the
/// inlined operators to the page-space names. Nested forms recurse with a
/// depth cap.
/// </para>
/// <para>
/// Flattening copies form content into the page; it never mutates the
/// shared XObject, so forms reused on other pages are unaffected. A form
/// whose page-space bounding box provably does not intersect the redaction
/// area is left as a <c>Do</c> (no bloat); anything overlapping or
/// indeterminate is inlined (bias toward inlining — a missed overlap would
/// be a redaction leak).
/// </para>
/// </remarks>
internal static class FormXObjectFlattener
{
    /// <summary>Hard cap on form-within-form recursion (cycle / abuse guard).</summary>
    private const int MaxDepth = 16;

    /// <summary>Resource categories whose named references we know how to rewrite.</summary>
    private static readonly string[] RewritableCategories =
        { "Font", "XObject", "ExtGState", "Shading", "Pattern", "ColorSpace", "Properties" };

    /// <summary>
    /// Produce a flattened copy of <paramref name="operations"/> with every
    /// Form XObject overlapping <paramref name="redactionArea"/> inlined.
    /// </summary>
    /// <param name="inlinedFormObjects">Object numbers of every Form XObject
    /// inlined (top-level and nested). After the caller rewrites the page,
    /// pass these to <see cref="PruneInlinedForms"/> so orphaned originals are
    /// dropped from the file rather than leaking their (now-redacted) content.</param>
    /// <returns><c>true</c> if any form was inlined (so <paramref name="output"/>
    /// differs and the caller should rewrite the page); <c>false</c> if there
    /// was nothing to flatten.</returns>
    public static bool FlattenOverlapping(
        PdfPage page,
        IReadOnlyList<ContentOperator> operations,
        PdfRectangle redactionArea,
        out List<ContentOperator> output,
        out HashSet<int> inlinedFormObjects)
    {
        // Fast path: no Form XObjects referenced anywhere → nothing to do.
        if (!ReferencesAnyForm(page, operations))
        {
            output = operations.ToList();
            inlinedFormObjects = new HashSet<int>();
            return false;
        }

        var ctx = new Context(page);
        // The top level resolves names against the page's own resources;
        // merging that into itself yields an identity rename map.
        var pageResources = ctx.PageResources;
        output = Flatten(ctx, operations, pageResources, Matrix.Identity, redactionArea,
                         applyOverlapGate: true, depth: 0);
        inlinedFormObjects = ctx.InlinedFormObjects;
        return ctx.Changed;
    }

    /// <summary>
    /// Free every inlined Form XObject in <paramref name="inlinedFormObjects"/>
    /// that is no longer reachable, so the writer does not re-emit the orphan
    /// (and leak the content the redaction just removed). Call after the page
    /// content has been rewritten with the flattened operators.
    /// </summary>
    /// <param name="finalOps">The page's operator list after flattening — used
    /// to determine which <c>Do</c> names are still referenced.</param>
    public static void PruneInlinedForms(
        PdfPage page,
        IReadOnlyList<ContentOperator> finalOps,
        HashSet<int> inlinedFormObjects)
    {
        if (inlinedFormObjects.Count == 0) return;

        var doc = page.Document;

        // /XObject names still invoked by a surviving Do (e.g. a non-overlapping
        // instance of a form we also inlined elsewhere). Those entries — and the
        // objects behind them — must stay.
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in finalOps)
            if (op.Name == "Do" && op.Operands.Count > 0 && op.Operands[0] is PdfName n)
                usedNames.Add(n.Value);

        // Drop the page's resource references to inlined forms no longer used,
        // operating on a page-local clone of the /XObject dict so we never
        // mutate a Resources node shared via inheritance.
        var ownResourcesObj = page.Dictionary.GetOptional("Resources");
        var ownResources = ownResourcesObj != null ? doc.Resolve(ownResourcesObj) as PdfDictionary : null;
        var xobjObj = ownResources?.GetOptional("XObject");
        if (ownResources != null && xobjObj != null &&
            doc.Resolve(xobjObj) is PdfDictionary xobj && xobj.Count > 0)
        {
            var local = new PdfDictionary();
            foreach (var kvp in xobj) local[kvp.Key] = kvp.Value;

            foreach (var key in local.Keys.ToList())
            {
                if (usedNames.Contains(key.Value)) continue;
                if (local.GetOptional(key.Value) is PdfReference r &&
                    inlinedFormObjects.Contains(r.ObjectNum))
                    local.Remove(key.Value);
            }
            ownResources["XObject"] = local;
        }

        // Mark-and-sweep from the trailer, but conservatively free ONLY the
        // forms we inlined — never arbitrary unreachable objects, which guards
        // against any blind spot in the reachability walk.
        var reachable = doc.ComputeReachableObjects();
        foreach (var objNum in inlinedFormObjects)
            if (!reachable.Contains(objNum))
                doc.RemoveObject(objNum);
    }

    private static bool ReferencesAnyForm(PdfPage page, IReadOnlyList<ContentOperator> ops)
    {
        var resources = page.Resources;
        if (resources == null) return false;
        foreach (var op in ops)
        {
            if (op.Name != "Do" || op.Operands.Count == 0) continue;
            if (op.Operands[0] is not PdfName n) continue;
            if (ResolveXObject(page.Document, resources, n.Value) is PdfStream s &&
                string.Equals(s.GetNameOrNull("Subtype"), "Form", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Flatten one operator list. <paramref name="sourceResources"/> is the
    /// resource dict the list's names resolve against; the returned operators
    /// are fully in page space (names merged + renamed into the page's
    /// resources). <paramref name="ctm"/> and <paramref name="applyOverlapGate"/>
    /// are only meaningful at the top level, where they decide whether a form
    /// is far enough from the redaction area to leave as a <c>Do</c>.
    /// </summary>
    private static List<ContentOperator> Flatten(
        Context ctx,
        IReadOnlyList<ContentOperator> ops,
        PdfDictionary sourceResources,
        Matrix ctm,
        PdfRectangle redactionArea,
        bool applyOverlapGate,
        int depth)
    {
        // Merge this level's resources into the page and get the rename map.
        var rename = ctx.MergeResources(sourceResources);

        var output = new List<ContentOperator>(ops.Count);
        var ctmStack = new Stack<Matrix>();

        foreach (var op in ops)
        {
            switch (op.Name)
            {
                case "q":
                    ctmStack.Push(ctm);
                    output.Add(op);
                    continue;
                case "Q":
                    if (ctmStack.Count > 0) ctm = ctmStack.Pop();
                    output.Add(op);
                    continue;
                case "cm":
                    if (op.Operands.Count >= 6)
                        ctm = Matrix.FromOperands(op).Multiply(ctm);
                    output.Add(op);
                    continue;
                case "Do":
                    HandleDo(ctx, op, sourceResources, rename, ctm, redactionArea,
                             applyOverlapGate, depth, output);
                    continue;
                default:
                    output.Add(Rewrite(op, rename));
                    continue;
            }
        }

        return output;
    }

    private static void HandleDo(
        Context ctx,
        ContentOperator op,
        PdfDictionary sourceResources,
        RenameMap rename,
        Matrix ctm,
        PdfRectangle redactionArea,
        bool applyOverlapGate,
        int depth,
        List<ContentOperator> output)
    {
        string? name = op.Operands.Count > 0 ? (op.Operands[0] as PdfName)?.Value : null;
        var target = name != null ? ResolveXObject(ctx.Doc, sourceResources, name) : null;

        bool isForm = target is PdfStream s &&
                      string.Equals(s.GetNameOrNull("Subtype"), "Form", StringComparison.Ordinal);

        if (!isForm || depth >= MaxDepth)
        {
            // Image XObject, unresolved, or too deep: keep the invocation,
            // rewriting its name into page space.
            output.Add(Rewrite(op, rename));
            return;
        }

        var form = (PdfStream)target!;
        var formMatrix = Matrix.FromArray(form.GetArrayOrNull("Matrix"));

        // Overlap gate (top level only): skip inlining a form whose page-space
        // BBox provably misses the redaction area. Indeterminate → inline.
        if (applyOverlapGate && !FormMayOverlap(form, formMatrix.Multiply(ctm), redactionArea))
        {
            output.Add(Rewrite(op, rename));
            return;
        }

        // --- Inline the form. ---
        ctx.Changed = true;

        // Record the underlying object so the caller can free it once the page
        // no longer references it (otherwise the orphan leaks on save).
        if (sourceResources.GetDictionaryOrNull("XObject")?.GetOptional(name!) is PdfReference formRef)
            ctx.InlinedFormObjects.Add(formRef.ObjectNum);

        byte[] formBytes;
        try { formBytes = form.DecodedData; }
        catch (Exception __ex) when (__ex is not OutOfMemoryException) { output.Add(Rewrite(op, rename)); return; } // can't decode → leave as-is

        var formOps = new ContentStreamParser(formBytes, null).Parse().Operators;
        var formResources = ctx.Resolve(form.GetOptional("Resources")) as PdfDictionary
                            ?? new PdfDictionary();

        // Recurse: nested forms are always inlined (no overlap gate below the
        // top level). The result is already fully in page space.
        var inlined = Flatten(ctx, formOps, formResources, Matrix.Identity, redactionArea,
                              applyOverlapGate: false, depth: depth + 1);

        output.Add(ContentOperator.SaveState());
        if (!formMatrix.IsIdentity)
            output.Add(ContentOperator.Transform(
                formMatrix.A, formMatrix.B, formMatrix.C, formMatrix.D, formMatrix.E, formMatrix.F));
        EmitBBoxClip(form, output);
        output.AddRange(inlined);
        output.Add(ContentOperator.RestoreState());
    }

    /// <summary>Emit <c>x y w h re W n</c> clipping to the form's /BBox, if present.</summary>
    private static void EmitBBoxClip(PdfStream form, List<ContentOperator> output)
    {
        var bbox = form.GetArrayOrNull("BBox");
        if (bbox == null || bbox.Count < 4) return;

        double x0 = NumberAt(bbox, 0), y0 = NumberAt(bbox, 1);
        double x1 = NumberAt(bbox, 2), y1 = NumberAt(bbox, 3);
        output.Add(ContentOperator.Rectangle(
            Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
        output.Add(new ContentOperator("W"));
        output.Add(new ContentOperator("n"));
    }

    /// <summary>
    /// Conservative overlap test for the gate: AABB of the form's /BBox after
    /// the given matrix vs. the redaction area. Missing/degenerate BBox →
    /// <c>true</c> (inline rather than risk a leak).
    /// </summary>
    private static bool FormMayOverlap(PdfStream form, Matrix m, PdfRectangle area)
    {
        var bbox = form.GetArrayOrNull("BBox");
        if (bbox == null || bbox.Count < 4) return true;

        double x0 = NumberAt(bbox, 0), y0 = NumberAt(bbox, 1);
        double x1 = NumberAt(bbox, 2), y1 = NumberAt(bbox, 3);

        var c1 = m.Transform(x0, y0);
        var c2 = m.Transform(x1, y0);
        var c3 = m.Transform(x1, y1);
        var c4 = m.Transform(x0, y1);

        double minX = Math.Min(Math.Min(c1.x, c2.x), Math.Min(c3.x, c4.x));
        double maxX = Math.Max(Math.Max(c1.x, c2.x), Math.Max(c3.x, c4.x));
        double minY = Math.Min(Math.Min(c1.y, c2.y), Math.Min(c3.y, c4.y));
        double maxY = Math.Max(Math.Max(c1.y, c2.y), Math.Max(c3.y, c4.y));

        var formBox = new PdfRectangle(minX, minY, maxX, maxY).Normalize();
        var a = area.Normalize();
        return formBox.Left < a.Right && formBox.Right > a.Left &&
               formBox.Bottom < a.Top && formBox.Top > a.Bottom;
    }

    // ---- operator name rewriting ----

    /// <summary>
    /// Return a copy of <paramref name="op"/> with any resource-name operand
    /// remapped through <paramref name="rename"/>. Returns the original
    /// instance unchanged when nothing needs rewriting (the common case, e.g.
    /// the identity map at the top level).
    /// </summary>
    private static ContentOperator Rewrite(ContentOperator op, RenameMap rename)
    {
        if (rename.IsEmpty) return op;

        switch (op.Name)
        {
            case "Tf": return RemapOperand(op, 0, "Font", rename);
            case "Do": return RemapOperand(op, 0, "XObject", rename);
            case "gs": return RemapOperand(op, 0, "ExtGState", rename);
            case "sh": return RemapOperand(op, 0, "Shading", rename);
            case "cs":
            case "CS": return RemapOperand(op, 0, "ColorSpace", rename);
            case "scn":
            case "SCN":
                // The pattern name, if any, is the last operand.
                return RemapOperand(op, op.Operands.Count - 1, "Pattern", rename);
            case "BDC":
            case "DP":
                return RemapOperand(op, 1, "Properties", rename);
            case "BI":
                return RewriteInlineImageColorSpace(op, rename);
            default:
                return op;
        }
    }

    private static ContentOperator RemapOperand(
        ContentOperator op, int index, string category, RenameMap rename)
    {
        if (index < 0 || index >= op.Operands.Count) return op;
        if (op.Operands[index] is not PdfName n) return op;
        if (!rename.TryGet(category, n.Value, out var newName)) return op;

        var operands = op.Operands.ToArray();
        operands[index] = new PdfName(newName);
        return new ContentOperator(op.Name, operands)
        {
            BoundingBox = op.BoundingBox,
            InlineImageData = op.InlineImageData,
        };
    }

    /// <summary>
    /// An inline image's /CS value may name a colour space in the form's
    /// ColorSpace resources (§8.9.5.2). Rewrite it if it was renamed.
    /// </summary>
    private static ContentOperator RewriteInlineImageColorSpace(ContentOperator op, RenameMap rename)
    {
        if (op.Operands.Count == 0 || op.Operands[0] is not PdfDictionary dict) return op;
        if (dict.GetOptional("CS") is not PdfName cs) return op;
        if (!rename.TryGet("ColorSpace", cs.Value, out var newName)) return op;

        var clone = new PdfDictionary();
        foreach (var kvp in dict) clone[kvp.Key] = kvp.Value;
        clone["CS"] = new PdfName(newName);
        return new ContentOperator("BI", new PdfObject[] { clone })
        {
            BoundingBox = op.BoundingBox,
            InlineImageData = op.InlineImageData,
        };
    }

    private static PdfObject? ResolveXObject(PdfDocument doc, PdfDictionary resources, string name)
    {
        var xobjects = resources.GetDictionaryOrNull("XObject");
        var obj = xobjects?.GetOptional(name);
        return obj != null ? doc.Resolve(obj) : null;
    }

    private static double NumberAt(PdfArray arr, int i) => arr[i] switch
    {
        PdfInteger n => n.Value,
        PdfReal r => r.Value,
        _ => 0,
    };

    /// <summary>Per-flatten mutable state: the page, its resources, name allocation.</summary>
    private sealed class Context
    {
        public readonly PdfDocument Doc;
        public readonly PdfDictionary PageResources;
        public bool Changed;

        /// <summary>Object numbers of every inlined form (top-level + nested).</summary>
        public readonly HashSet<int> InlinedFormObjects = new();

        // Categories already cloned into page-local dicts (so we never mutate
        // a category dictionary shared with an inherited Resources node).
        private readonly HashSet<string> _localized = new();
        private int _counter;

        public Context(PdfPage page)
        {
            Doc = page.Document;
            PageResources = EnsurePageOwnedResources(page);
        }

        public PdfObject? Resolve(PdfObject? o) => o == null ? null : Doc.Resolve(o);

        /// <summary>
        /// Merge <paramref name="source"/>'s rewritable categories into the
        /// page resources, returning the name remapping. A name already
        /// present and pointing at the same object maps to itself; a collision
        /// with a different object gets a fresh unique name.
        /// </summary>
        public RenameMap MergeResources(PdfDictionary source)
        {
            var map = new RenameMap();
            if (ReferenceEquals(source, PageResources)) return map; // top level: identity

            foreach (var category in RewritableCategories)
            {
                var srcCat = Resolve(source.GetOptional(category)) as PdfDictionary;
                if (srcCat == null || srcCat.Count == 0) continue;

                foreach (var key in srcCat.Keys.ToList())
                {
                    var srcVal = srcCat.GetOptional(key.Value);
                    if (srcVal == null) continue;

                    var pageCat = Resolve(PageResources.GetOptional(category)) as PdfDictionary;
                    var existing = pageCat?.GetOptional(key.Value);

                    if (existing == null)
                    {
                        // No collision: copy under the same name.
                        EnsureCategory(category)[key.Value] = srcVal;
                    }
                    else if (SameTarget(existing, srcVal))
                    {
                        // Identical resource already present: reuse the name.
                    }
                    else
                    {
                        // Collision with a different object: allocate a new name.
                        string fresh = UniqueName(category, key.Value);
                        EnsureCategory(category)[fresh] = srcVal;
                        map.Add(category, key.Value, fresh);
                    }
                }
            }
            return map;
        }

        private static bool SameTarget(PdfObject a, PdfObject b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is PdfReference ra && b is PdfReference rb) return ra.Equals(rb);
            return false;
        }

        private PdfDictionary EnsureCategory(string category)
        {
            var cat = Resolve(PageResources.GetOptional(category)) as PdfDictionary;
            if (cat == null)
            {
                cat = new PdfDictionary();
                PageResources[category] = cat;
                _localized.Add(category);
                return cat;
            }
            if (_localized.Add(category))
            {
                // First write to a pre-existing (possibly inherited/shared)
                // category dict: clone it page-local before mutating.
                var local = new PdfDictionary();
                foreach (var kvp in cat) local[kvp.Key] = kvp.Value;
                PageResources[category] = local;
                return local;
            }
            return cat;
        }

        private string UniqueName(string category, string baseName)
        {
            var pageCat = Resolve(PageResources.GetOptional(category)) as PdfDictionary;
            while (true)
            {
                string candidate = $"{baseName}_fx{++_counter}";
                if (pageCat == null || !pageCat.ContainsKey(candidate))
                    return candidate;
            }
        }

        /// <summary>
        /// Return the page's own /Resources, creating a page-local dictionary
        /// (shallow-copying any inherited entries) when the page relies on an
        /// inherited Resources node, so we never mutate a shared parent.
        /// </summary>
        private static PdfDictionary EnsurePageOwnedResources(PdfPage page)
        {
            var own = page.Document.Resolve(page.Dictionary.GetOptional("Resources")!) as PdfDictionary;
            if (own != null) return own;

            var local = new PdfDictionary();
            var inherited = page.Resources;
            if (inherited != null)
                foreach (var kvp in inherited) local[kvp.Key] = kvp.Value;
            page.Dictionary["Resources"] = local;
            return local;
        }
    }

    /// <summary>Per-level resource name remapping, keyed by (category, oldName).</summary>
    private sealed class RenameMap
    {
        private readonly Dictionary<string, Dictionary<string, string>> _byCategory = new();

        public bool IsEmpty => _byCategory.Count == 0;

        public void Add(string category, string oldName, string newName)
        {
            if (!_byCategory.TryGetValue(category, out var inner))
                _byCategory[category] = inner = new Dictionary<string, string>();
            inner[oldName] = newName;
        }

        public bool TryGet(string category, string oldName, out string newName)
        {
            newName = oldName;
            return _byCategory.TryGetValue(category, out var inner) &&
                   inner.TryGetValue(oldName, out newName!);
        }
    }

    /// <summary>Minimal 2×3 affine matrix (PDF spec 8.3.3): <c>a b c d e f</c>.</summary>
    private readonly struct Matrix
    {
        public readonly double A, B, C, D, E, F;

        public Matrix(double a, double b, double c, double d, double e, double f)
        { A = a; B = b; C = c; D = d; E = e; F = f; }

        public static Matrix Identity => new(1, 0, 0, 1, 0, 0);

        public bool IsIdentity =>
            A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0;

        public static Matrix FromOperands(ContentOperator op) => new(
            op.GetNumber(0), op.GetNumber(1), op.GetNumber(2),
            op.GetNumber(3), op.GetNumber(4), op.GetNumber(5));

        public static Matrix FromArray(PdfArray? a)
        {
            if (a == null || a.Count < 6) return Identity;
            return new Matrix(NumberAt(a, 0), NumberAt(a, 1), NumberAt(a, 2),
                              NumberAt(a, 3), NumberAt(a, 4), NumberAt(a, 5));
        }

        public (double x, double y) Transform(double x, double y)
            => (A * x + C * y + E, B * x + D * y + F);

        public Matrix Multiply(Matrix o) => new(
            A * o.A + B * o.C,
            A * o.B + B * o.D,
            C * o.A + D * o.C,
            C * o.B + D * o.D,
            E * o.A + F * o.C + o.E,
            E * o.B + F * o.D + o.F);
    }
}

using System.Collections.Generic;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Validation;

/// <summary>
/// Scans a page content stream for text-showing operators that are neither
/// inside the structure tree (a marked-content span whose /MCID the tree
/// references) nor marked as an <c>/Artifact</c>. Such text is "untagged real
/// content", the PDF/UA §7.1 violation.
///
/// <para>Self-contained on purpose: it re-walks the raw operator stream rather
/// than going through <see cref="Text.TextExtractor"/>, so it can distinguish an
/// artifact from untagged content (which <see cref="Text.Letter"/> cannot) and
/// touches nothing on the extraction path.</para>
/// </summary>
internal static class ContentTaggingScanner
{
    private readonly struct Frame
    {
        public readonly bool Artifact;
        public readonly int? Mcid;
        public Frame(bool artifact, int? mcid) { Artifact = artifact; Mcid = mcid; }
    }

    /// <summary>
    /// Count text-showing operators on <paramref name="page"/> that draw visible
    /// text yet fall outside both the structure tree and any /Artifact span.
    /// </summary>
    public static int CountUntaggedTextRuns(
        PdfPage page,
        int pageNumber,
        HashSet<(int, int)> taggedQualified,
        HashSet<int> taggedPageAgnostic)
    {
        byte[] bytes;
        try { bytes = page.GetContentStreamBytes(); }
        catch { return 0; }
        if (bytes.Length == 0) return 0;

        ContentStream stream;
        try
        {
            stream = new ContentStreamParser(bytes) { ComputeOperatorMetadata = false }.Parse();
        }
        catch { return 0; }

        var mcStack = new Stack<Frame>();
        int untagged = 0;

        foreach (var op in stream.Operators)
        {
            switch (op.Name)
            {
                case "BDC":
                case "BMC":
                {
                    string tag = op.Operands.Count > 0 && op.Operands[0] is PdfName n ? n.Value : "";
                    int? mcid = op.Name == "BDC" && op.Operands.Count > 1 && op.Operands[1] is PdfDictionary props
                        && props.GetOptional("MCID") is PdfInteger m ? (int)m.Value : (int?)null;

                    bool parentArtifact = mcStack.Count > 0 && mcStack.Peek().Artifact;
                    int? parentMcid = mcStack.Count > 0 ? mcStack.Peek().Mcid : null;
                    mcStack.Push(new Frame(parentArtifact || tag == "Artifact", mcid ?? parentMcid));
                    break;
                }
                case "EMC":
                    if (mcStack.Count > 0) mcStack.Pop();
                    break;
                case "Tj":
                case "TJ":
                case "'":
                case "\"":
                    if (!HasVisibleText(op.Operands)) break;
                    var frame = mcStack.Count > 0 ? mcStack.Peek() : new Frame(false, null);
                    if (frame.Artifact) break;                                   // artifact — fine
                    if (frame.Mcid is int id &&
                        (taggedQualified.Contains((pageNumber, id)) || taggedPageAgnostic.Contains(id)))
                        break;                                                   // inside struct tree — fine
                    untagged++;
                    break;
            }
        }

        return untagged;
    }

    private static bool HasVisibleText(IReadOnlyList<PdfObject> operands)
    {
        foreach (var o in operands)
        {
            if (o is PdfString s && s.Bytes.Length > 0) return true;
            if (o is PdfArray arr)
                foreach (var e in arr)
                    if (e is PdfString es && es.Bytes.Length > 0) return true;
        }
        return false;
    }
}

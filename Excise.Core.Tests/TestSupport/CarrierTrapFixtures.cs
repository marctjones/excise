using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Excise.TestSupport;

/// <summary>
/// Synthetic carrier TRAPS for the unredact certain channel and the redaction
/// bench: one tiny PDF per place a secret can survive a redaction that only
/// rewrote the visible page — form values and appearances, XFA, attachments,
/// actions, metadata, orphan objects, an earlier revision. Generated in memory,
/// so nothing depends on a corpus being present.
/// </summary>
/// <remarks>
/// <para>One source for four consumers: <c>CarrierTextRecoveryTrapTests</c> (does
/// excise read it back), <c>CarrierTrapIndependentCorroborationTests</c> (does
/// qpdf/mutool agree the text is where excise says), the unredaction scorecard,
/// and <c>RedactionBenchmarkRunner</c> (does each redactor remove it). Each trap
/// names its token; the bench writes them as <c>&lt;id&gt;--&lt;TOKEN&gt;.pdf</c>
/// exactly like the Python adversarial corpus.</para>
/// <para>Tokens are unique upper-case words so a substring match cannot be
/// satisfied by anything else in the file.</para>
/// </remarks>
internal static class CarrierTrapFixtures
{
    /// <summary>How an independent tool can see the trap's text.</summary>
    internal enum Oracle
    {
        /// <summary>The token is in qpdf's JSON object dump (strings + decoded stream data).</summary>
        QpdfDump,

        /// <summary>The token is page text of a PDF attached to the trap (qpdf --show-attachment, then mutool).</summary>
        NestedPdfText,

        /// <summary>The token is page text of the file prefix ending at the first %%EOF, and absent from the current revision.</summary>
        PriorRevisionText,

        /// <summary>Nothing to decode: the trap is content that is PRESENT (reported as presence, not text).</summary>
        PresenceOnly,
    }

    /// <param name="Id">Stable identifier and bench file prefix.</param>
    /// <param name="Token">The secret.</param>
    /// <param name="ExpectedCarrier">Substring of the carrier label excise must report the token under.</param>
    /// <param name="Oracle">How qpdf/mutool corroborate it.</param>
    /// <param name="InBench">Whether a redactor can be scored on it (it has a findable token).</param>
    /// <param name="AttachmentName">For <see cref="Oracle.NestedPdfText"/>, the attachment key to extract.</param>
    /// <param name="ExpectVisible">True when the carrier is itself something a reader sees (the title,
    /// a bookmark), so the unredact channel classes it a visible duplicate rather than a hidden leak.</param>
    internal sealed record Trap(
        string Id,
        string Token,
        string ExpectedCarrier,
        Oracle Oracle,
        Func<bool, byte[]> Build,
        bool InBench = true,
        string? AttachmentName = null,
        bool ExpectVisible = false)
    {
        public override string ToString() => Id;
    }

    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    public static IReadOnlyList<Trap> All { get; } = BuildAll();

    public static Trap Get(string id) => All.Single(t => t.Id == id);

    private static IReadOnlyList<Trap> BuildAll()
    {
        var traps = new List<Trap>();
        void Add(string id, string token, string carrier, Oracle oracle, Func<string, bool, byte[]> build,
                 bool inBench = true, string? attachment = null, bool expectVisible = false) =>
            traps.Add(new Trap(id, token, carrier, oracle, visible => build(token, visible), inBench, attachment, expectVisible));

        // ── AcroForm ────────────────────────────────────────────────────
        Add("acroform-v", "FIELDVALUETRAP", "acroform /V", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Tx /T (name) /V ({t})"));
        Add("acroform-dv", "FIELDDEFAULTTRAP", "acroform /DV", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Tx /T (name) /DV ({t})"));
        Add("acroform-rv", "FIELDRICHVALUETRAP", "acroform /RV", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Tx /T (name) /RV (<body><p>{t}</p></body>)"));
        Add("acroform-tu", "FIELDTOOLTIPTRAP", "acroform /TU", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Tx /T (name) /TU (Enter {t} here)"));
        Add("acroform-opt-export", "OPTEXPORTTRAP", "acroform /Opt export value", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Ch /Ff 131072 /T (choice) /Opt [[({t}) (Shown choice)]]"));
        Add("acroform-opt-display", "OPTDISPLAYTRAP", "acroform /Opt", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Ch /Ff 131072 /T (choice) /Opt [(First) ({t})]"));
        // A HIDDEN widget (/F 2): its appearance is never painted, so its text is a leak.
        Add("widget-appearance", "WIDGETAPPEARANCETRAP", "widget /AP /N", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), "/FT /Tx /T (name) /F 2 /AP << /N 7 0 R >>",
                AppearanceStream($"BT /F1 10 Tf 2 4 Td ({t}) Tj ET", compress: true)));
        Add("widget-mk-ca", "CAPTIONNORMALTRAP", "widget /MK /CA", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Btn /Ff 65536 /T (button) /MK << /CA ({t}) >>"));
        Add("widget-mk-rc", "CAPTIONROLLOVERTRAP", "widget /MK /RC", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Btn /Ff 65536 /T (button) /MK << /CA (Go) /RC ({t}) >>"));
        Add("widget-mk-ac", "CAPTIONDOWNTRAP", "widget /MK /AC", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Btn /Ff 65536 /T (button) /MK << /CA (Go) /AC ({t}) >>"));
        Add("field-javascript", "FIELDSCRIPTTRAP", "JavaScript", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), $"/FT /Tx /T (name) /AA << /K << /S /JavaScript /JS (var who = \"{t}\";) >> >>"));
        Add("field-javascript-stream", "FIELDSCRIPTSTREAMTRAP", "JavaScript", Oracle.QpdfDump,
            (t, v) => Field(V(t, v), "/FT /Tx /T (name) /AA << /F << /S /JavaScript /JS 7 0 R >> >>",
                Stream("", $"event.value = \"{t}\";", compress: true)));
        // The classic leak: the appearance was redacted, the value was not.
        Add("acroform-v-appearance-redacted", "REDACTEDFIELDTRAP", "acroform /V", Oracle.QpdfDump,
            (t, v) => FilledForm(t, "XXXXXXXXXX", blackBoxOverField: true, visibleToken: V(t, v)));
        Add("nonterminal-field-javascript", "PARENTSCRIPTTRAP", "JavaScript (field /A", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: "/Annots [7 0 R]",
                catalog: "/AcroForm << /Fields [6 0 R] >>",
                extra: new[]
                {
                    $"<< /T (parent) /Kids [7 0 R] /A << /S /JavaScript /JS (log(\"{t}\")) >> >>",
                    "<< /Type /Annot /Subtype /Widget /FT /Tx /Parent 6 0 R /T (child) /Rect [72 600 272 620] /P 3 0 R >>",
                }));

        // ── Annotations and actions ─────────────────────────────────────
        Add("annotation-appearance", "ANNOTAPPEARANCETRAP", "annotation /AP /N", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: "/Annots [6 0 R]", extra: new[]
            {
                "<< /Type /Annot /Subtype /FreeText /F 2 /Rect [72 600 372 620] /DA (/F1 10 Tf) /AP << /N 7 0 R >> >>",
                AppearanceStream($"BT /F1 10 Tf 2 4 Td ({t}) Tj ET", compress: false),
            }));
        Add("annotation-author", "ANNOTAUTHORTRAP", "annotation /T (author)", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: "/Annots [6 0 R]", extra: new[]
            {
                $"<< /Type /Annot /Subtype /Text /Rect [72 600 92 620] /T ({t}) /Contents (Looks fine) >>",
            }));
        Add("annotation-subj", "ANNOTSUBJECTTRAP", "annotation /Subj", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: "/Annots [6 0 R]", extra: new[]
            {
                $"<< /Type /Annot /Subtype /Highlight /Rect [72 600 372 620] /Subj ({t}) /QuadPoints [72 620 372 620 72 600 372 600] >>",
            }));
        Add("redact-overlaytext", "OVERLAYTEXTTRAP", "annotation /OverlayText", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: "/Annots [6 0 R]", extra: new[]
            {
                $"<< /Type /Annot /Subtype /Redact /Rect [72 600 372 620] /OverlayText ({t}) >>",
            }));
        Add("link-uri", "LINKURITRAP", "action /URI", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: "/Annots [6 0 R]", extra: new[]
            {
                $"<< /Type /Annot /Subtype /Link /Rect [72 600 372 620] /A << /S /URI /URI (https://example.test/case/{t}) >> >>",
            }));
        Add("launch-action", "LAUNCHTARGETTRAP", "action /Launch file target", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: "/Annots [6 0 R]", extra: new[]
            {
                $"<< /Type /Annot /Subtype /Link /Rect [72 600 372 620] /A << /S /Launch /F ({t}.docx) >> >>",
            }));
        Add("document-javascript", "DOCSCRIPTTRAP", "JavaScript (document JavaScript)", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: "/Names << /JavaScript << /Names [(init) 6 0 R] >> >>", extra: new[]
            {
                $"<< /S /JavaScript /JS (app.alert(\"{t}\");) >>",
            }));
        Add("openaction-javascript", "OPENSCRIPTTRAP", "JavaScript (/OpenAction)", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: $"/OpenAction << /S /JavaScript /JS (this.info = \"{t}\";) >>"));
        Add("outline-uri", "OUTLINEURITRAP", "action /URI (outline /A)", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: "/Outlines 6 0 R", extra: new[]
            {
                "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
                $"<< /Title (Links) /Parent 6 0 R /A << /S /URI /URI (https://example.test/{t}) >> >>",
            }));

        // ── XFA ─────────────────────────────────────────────────────────
        Add("xfa-datasets", "XFADATATRAP", "XFA datasets value", Oracle.QpdfDump,
            (t, v) => Xfa(V(t, v), Xdp(template: XfaTemplate("<value><text>Default</text></value>", "Name"),
                datasets: $"<form1><name>{t}</name></form1>"), asArray: false));
        Add("xfa-template-default", "XFADEFAULTTRAP", "XFA template default value", Oracle.QpdfDump,
            (t, v) => Xfa(V(t, v), Xdp(template: XfaTemplate($"<value><text>{t}</text></value>", "Name"),
                datasets: "<form1><name>Filled</name></form1>"), asArray: false));
        Add("xfa-template-caption", "XFACAPTIONTRAP", "XFA template caption", Oracle.QpdfDump,
            (t, v) => Xfa(V(t, v), Xdp(template: XfaTemplate("<value><text>Default</text></value>", t),
                datasets: null), asArray: true));

        // ── Attachments ─────────────────────────────────────────────────
        Add("attachment-name", "ATTACHNAMETRAP", "attachment name", Oracle.QpdfDump,
            (t, v) => Attachment(V(t, v), treeKey: $"{t}.txt", fileSpec: "/F (note.txt)", payload: "hello", subtype: null));
        Add("attachment-desc", "ATTACHDESCTRAP", "attachment /Desc", Oracle.QpdfDump,
            (t, v) => Attachment(V(t, v), treeKey: "note.txt", fileSpec: $"/F (note.txt) /Desc (Statement of {t})", payload: "hello", subtype: null));
        Add("attachment-uf", "ATTACHFILENAMETRAP", "attachment /UF", Oracle.QpdfDump,
            (t, v) => Attachment(V(t, v), treeKey: "note.txt", fileSpec: $"/F (note.txt) /UF ({t}.txt)", payload: "hello", subtype: null));
        Add("attachment-payload-text", "ATTACHTEXTTRAP", "attachment payload (text)", Oracle.QpdfDump,
            (t, v) => Attachment(V(t, v), treeKey: "note.txt", fileSpec: "/F (note.txt) /UF (note.txt)", payload: $"call {t} tomorrow\n", subtype: null, compress: true));
        Add("attachment-payload-csv", "ATTACHCSVTRAP", "attachment payload (text)", Oracle.QpdfDump,
            (t, v) => Attachment(V(t, v), treeKey: "data", fileSpec: "/F (data) /UF (data)", payload: $"id,name\n1,{t}\n", subtype: "/text#2Fcsv"));
        Add("attachment-nested-pdf", "NESTEDPDFTRAP", "page text", Oracle.NestedPdfText,
            (t, v) => Attachment(V(t, v), treeKey: "inner.pdf", fileSpec: "/F (inner.pdf) /UF (inner.pdf)",
                payloadBytes: NestedPdf(t), subtype: "/application#2Fpdf"),
            attachment: "inner.pdf");
        Add("attachment-page-af", "PAGEAFTRAP", "attachment (page /AF) payload (text)", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: "/AF [6 0 R]", extra: new[]
            {
                "<< /Type /Filespec /F (source.txt) /UF (source.txt) /AFRelationship /Source /EF << /F 7 0 R >> >>",
                Stream("/Type /EmbeddedFile", $"source says {t}\n", compress: false),
            }));
        Add("file-attachment-annotation", "ANNOTFILEDESCTRAP", "attachment /Desc", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: "/Annots [6 0 R]", extra: new[]
            {
                "<< /Type /Annot /Subtype /FileAttachment /Rect [72 600 92 620] /FS 7 0 R >>",
                $"<< /Type /Filespec /F (clip.txt) /Desc (clip about {t}) /EF << /F 8 0 R >> >>",
                Stream("/Type /EmbeddedFile", "unrelated clip\n", compress: false),
            }));
        Add("attachment-opaque", "OPAQUEBLOBTRAP", "attachment payload (opaque)", Oracle.PresenceOnly,
            (t, v) => Attachment(V(t, v), treeKey: "blob.bin", fileSpec: "/F (blob.bin) /UF (blob.bin)",
                payloadBytes: OpaqueBlob(t), subtype: "/application#2Foctet-stream"));

        // ── Leftovers ───────────────────────────────────────────────────
        Add("orphan-dictionary", "ORPHANDICTTRAP", "orphan object", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), extra: new[] { $"<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] /Contents ({t}) >>" }));
        Add("orphan-content-stream", "ORPHANSTREAMTRAP", "orphan object (content stream)", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), extra: new[] { Stream("", $"BT /F1 12 Tf 72 500 Td ({t}) Tj ET", compress: true) }));
        Add("prior-revision", "PRIORREVISIONTRAP", "prior revision 1 of 2 page text", Oracle.PriorRevisionText,
            PriorRevision);
        Add("info-title", "INFOTITLETRAP", "/Info /Title", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), info: $"<< /Title (Memo on {t}) /Producer (TrapGen) >>"), expectVisible: true);
        Add("info-custom-key", "INFOCUSTOMTRAP", "/Info /CaseName", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), info: $"<< /CaseName ({t}) /Producer (TrapGen) >>"));
        Add("xmp-description", "XMPDESCRIPTIONTRAP", "XMP dc:description", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: "/Metadata 6 0 R", extra: new[] { Xmp($"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">About {t}</rdf:li></rdf:Alt></dc:description>") }));
        Add("xmp-attribute", "XMPATTRIBUTETRAP", "XMP pdf:Keywords", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: "/Metadata 6 0 R", extra: new[] { Xmp("", descriptionAttributes: $"pdf:Keywords=\"{t}\"") }));
        Add("outline-title", "OUTLINETITLETRAP", "outline /Title", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: "/Outlines 6 0 R", extra: new[]
            {
                "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
                $"<< /Title (Chapter on {t}) /Parent 6 0 R /Dest [3 0 R /Fit] >>",
            }), expectVisible: true);
        Add("ocg-hidden", "HIDDENLAYERTRAP", "optional content (hidden by default)", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), extraContent: $"/OC /MC0 BDC BT /F1 12 Tf 72 500 Td ({t}) Tj ET EMC",
                resources: "/Properties << /MC0 6 0 R >>",
                catalog: "/OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>",
                extra: new[] { "<< /Type /OCG /Name (Draft) >>" }));
        // #1586: the SAME hidden layer, one level down — a hidden /OC span
        // inside a VISIBLE form XObject, whose /Properties live in the form's
        // own resources. The page-level pass cannot see it, and the report row
        // ("hidden optional-content span(s)") would have overstated what was
        // removed.
        Add("ocg-hidden-in-form", "HIDDENFORMLAYERTRAP", "optional content (hidden",
            Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), extraContent: "/Fm0 Do",
                resources: "/XObject << /Fm0 7 0 R >>",
                catalog: "/OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>",
                extra: new[]
                {
                    "<< /Type /OCG /Name (Draft) >>",
                    Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] "
                        + "/Resources << /Font << /F1 5 0 R >> /Properties << /MC0 6 0 R >> >>",
                        $"/OC /MC0 BDC BT /F1 12 Tf 72 460 Td ({t}) Tj ET EMC", compress: false),
                }));
        Add("structure-actualtext", "STRUCTACTUALTRAP", "structure-tree /ActualText", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: "/StructTreeRoot 6 0 R", extra: new[]
            {
                "<< /Type /StructTreeRoot /K 7 0 R >>",
                $"<< /Type /StructElem /S /P /P 6 0 R /ActualText ({t}) >>",
            }));
        Add("structure-title", "STRUCTTITLETRAP", "structure-tree /T", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: "/StructTreeRoot 6 0 R", extra: new[]
            {
                "<< /Type /StructTreeRoot /K 7 0 R >>",
                $"<< /Type /StructElem /S /Sect /P 6 0 R /T (Section {t}) >>",
            }));
        // #1586: a Figure whose /Alt DESCRIBES a redacted image, with no MCID
        // link between the two. Both of StructureTreeRedactionScrubber's passes
        // are blind to it by construction: pass 1 needs a structural (/MCID or
        // /OBJR) link to the redaction area, and pass 2 content-matches the
        // carrier against text the glyph pass REMOVED — and an image redaction
        // removes no text, so there is nothing to compare against.
        Add("figure-alt-image-no-mcid", "FIGUREALTIMAGETRAP", "structure-tree /Alt", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: "/StructTreeRoot 6 0 R",
                resources: "/XObject << /Im0 8 0 R >>",
                extraContent: "q 200 0 0 50 72 500 cm /Im0 Do Q",
                extra: new[]
                {
                    "<< /Type /StructTreeRoot /K 7 0 R >>",
                    $"<< /Type /StructElem /S /Figure /P 6 0 R /Alt (Scan of the letter naming {t}) >>",
                    Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8",
                        "\u00ff\u0000\u0000\u00ff", compress: false),
                }));
        // #1586: a redaction of ONE OR TWO characters in a structure element
        // with no structural link. Below StructureTreeRedactionScrubber's
        // MinMatchLength (3) and below PdfDocumentSanitizer's MinTermLength (3),
        // so neither the content-match pass nor the document-level scrub will
        // act on it. The floor is deliberate — excising "of" from every /Alt in
        // a document would delete the structure tree — so the right outcome is
        // a REPORT, not a lower floor.
        Add("structure-alt-short-term", "QX", "structure-tree /Alt", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), catalog: "/StructTreeRoot 6 0 R", extra: new[]
            {
                "<< /Type /StructTreeRoot /K 7 0 R >>",
                $"<< /Type /StructElem /S /P /P 6 0 R /Alt (Exhibit {t} summary) >>",
            }),
            inBench: false);
        Add("pieceinfo", "PIECEINFOTRAP", "/PieceInfo /ExciseTrap /Private", Oracle.QpdfDump,
            (t, v) => Doc(V(t, v), page: $"/PieceInfo << /ExciseTrap << /LastModified (D:20260917000000Z) /Private << /Draft ({t}) >> >> >>"));
        Add("page-thumbnail", "THUMBNAILTRAP", "page /Thumb", Oracle.PresenceOnly,
            (t, v) => Doc(V(t, v), page: "/Thumb 6 0 R", extra: new[]
            {
                Stream("/Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8", new string('', 16), compress: false),
            }),
            inBench: false);

        return traps;
    }

    // ── Clean control ───────────────────────────────────────────────────

    /// <summary>
    /// A benign document shaped like real output: tool-written /Info and XMP,
    /// no interactive content, and an incremental update that changes only
    /// the /Info modification date. The certain channel must report NOTHING.
    /// </summary>
    public static byte[] Clean()
    {
        var xmp = Xmp("<xmp:CreatorTool>TrapGen 1.0</xmp:CreatorTool><pdf:Producer>TrapGen</pdf:Producer>" +
                      "<xmp:CreateDate>2026-09-17T00:00:00Z</xmp:CreateDate>" +
                      "<xmpMM:DocumentID>uuid:1b4e28ba-2fa1-11d2-883f-0016d3cca427</xmpMM:DocumentID>" +
                      "<xmpMM:History><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><stEvt:action>created</stEvt:action>" +
                      "<stEvt:softwareAgent>TrapGen 1.0</stEvt:softwareAgent></rdf:li></rdf:Seq></xmpMM:History>");
        var original = Doc(null,
            catalog: "/Metadata 6 0 R",
            info: "<< /Producer (TrapGen) /Creator (TrapGen Writer) /CreationDate (D:20260917000000Z) >>",
            extra: new[] { xmp });
        // Object 7 is /Info (Doc appends it after the extras).
        return AppendUpdate(original,
            new Dictionary<int, string>
            {
                [7] = "<< /Producer (TrapGen) /Creator (TrapGen Writer) /CreationDate (D:20260917000000Z) /ModDate (D:20260917010000Z) >>",
            },
            size: 8, trailerExtra: "/Info 7 0 R");
    }

    /// <summary>
    /// A PDF attached inside a PDF, <paramref name="depth"/> levels deep; the
    /// innermost page shows <paramref name="token"/>. Exercises the recursion bound.
    /// </summary>
    public static byte[] NestedChain(int depth, string token)
    {
        var bytes = NestedPdf(token);
        for (var i = 0; i < depth; i++)
            bytes = Attachment(null, treeKey: $"level{i}.pdf", fileSpec: $"/F (level{i}.pdf)",
                payloadBytes: bytes, subtype: "/application#2Fpdf", compress: true);
        return bytes;
    }

    /// <summary>A plain document whose trailer /Info is <paramref name="infoDictionary"/>.</summary>
    public static byte[] WithInfo(string infoDictionary) => Doc(null, info: infoDictionary);

    // ── Builders ────────────────────────────────────────────────────────

    /// <summary>
    /// Catalog(1) Pages(2) Page(3) Contents(4) Font(5), then <paramref name="extra"/>
    /// from object 6, then /Info (when given) as the last object.
    /// </summary>
    private static byte[] Doc(
        string? visibleToken, string page = "", string catalog = "", string resources = "",
        string? info = null, string[]? extra = null, string extraContent = "")
    {
        // The bench wants the token ALSO on the page, so every redactor's find
        // step fires on it and the survey shows whether the carrier copy
        // survived the same redaction. Recovery tests leave it off.
        var visibleLine = visibleToken is null ? "" : $"BT /F1 12 Tf 72 680 Td (Reference {visibleToken}) Tj ET\n";
        var content = "BT /F1 12 Tf 72 720 Td (Case file, public summary) Tj ET\n" + visibleLine + extraContent + "\n";
        var objs = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> {resources} >> /Contents 4 0 R {page} >>",
            Stream("", content, compress: false),
            Font,
        };
        if (extra != null) objs.AddRange(extra);
        int? infoNum = null;
        if (info != null) { objs.Add(info); infoNum = objs.Count; }
        return Assemble(objs, infoNum is null ? "" : $"/Info {infoNum} 0 R");
    }

    private static string? V(string token, bool visible) => visible ? token : null;

    /// <summary>
    /// A filled text field whose value is <paramref name="value"/> and whose
    /// painted appearance says <paramref name="appearanceText"/>. Equal strings
    /// are an ordinary filled form; different strings are a redacted
    /// appearance over an unredacted value.
    /// </summary>
    public static byte[] FilledForm(string value, string appearanceText, bool blackBoxOverField, string? visibleToken = null) =>
        FieldWithContent(visibleToken,
            blackBoxOverField ? "0 0 0 rg 70 598 204 24 re f" : "",
            $"/FT /Tx /T (name) /V ({value}) /AP << /N 7 0 R >>",
            AppearanceStream($"BT /F1 10 Tf 2 4 Td ({appearanceText}) Tj ET", compress: false));

    private static byte[] Field(string? visibleToken, string fieldEntries, params string[] extra) =>
        FieldWithContent(visibleToken, "", fieldEntries, extra);

    private static byte[] FieldWithContent(string? visibleToken, string extraContent, string fieldEntries, params string[] extra)
    {
        var all = new List<string>
        {
            $"<< /Type /Annot /Subtype /Widget {fieldEntries} /Rect [72 600 272 620] /P 3 0 R >>",
        };
        all.AddRange(extra);
        return Doc(visibleToken, page: "/Annots [6 0 R]",
            catalog: "/AcroForm << /Fields [6 0 R] /DR << /Font << /F1 5 0 R >> >> /DA (/F1 10 Tf 0 g) >>",
            extra: all.ToArray(), extraContent: extraContent);
    }

    private static string AppearanceStream(string content, bool compress) =>
        Stream("/Type /XObject /Subtype /Form /BBox [0 0 300 20] /Resources << /Font << /F1 5 0 R >> >>", content, compress);

    private static byte[] Xfa(string? visibleToken, string xdp, bool asArray)
    {
        if (!asArray)
            return Doc(visibleToken, catalog: "/AcroForm << /Fields [] /XFA 6 0 R >>",
                extra: new[] { Stream("", xdp, compress: true) });
        // Split the XDP into two packet streams at the template boundary.
        var cut = xdp.IndexOf("<template", StringComparison.Ordinal);
        return Doc(visibleToken, catalog: "/AcroForm << /Fields [] /XFA [(preamble) 6 0 R (template) 7 0 R] >>",
            extra: new[] { Stream("", xdp[..cut], compress: false), Stream("", xdp[cut..], compress: true) });
    }

    private static string Xdp(string template, string? datasets) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">" +
        template +
        (datasets is null ? "" :
            "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data>" + datasets + "</xfa:data></xfa:datasets>") +
        "</xdp:xdp>";

    private static string XfaTemplate(string fieldValue, string caption) =>
        "<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\">" +
        "<subform name=\"form1\" layout=\"tb\"><pageSet><pageArea name=\"P1\"><contentArea w=\"8in\" h=\"10in\"/>" +
        "<medium stock=\"letter\"/></pageArea></pageSet>" +
        "<field name=\"name\" w=\"3in\" h=\"0.3in\"><ui><textEdit/></ui>" +
        $"<caption><value><text>{caption}</text></value></caption>{fieldValue}</field>" +
        "</subform></template>";

    private static byte[] Attachment(
        string? visibleToken, string treeKey, string fileSpec, string? payload = null, string? subtype = null,
        bool compress = false, byte[]? payloadBytes = null)
    {
        var data = payloadBytes ?? Encoding.UTF8.GetBytes(payload ?? "");
        var subtypeEntry = subtype is null ? "" : $"/Subtype {subtype}";
        return Doc(visibleToken,
            catalog: $"/Names << /EmbeddedFiles << /Names [({treeKey}) 6 0 R] >> >>",
            extra: new[]
            {
                $"<< /Type /Filespec {fileSpec} /EF << /F 7 0 R >> >>",
                StreamBytes($"/Type /EmbeddedFile {subtypeEntry}", data, compress),
            });
    }

    private static byte[] NestedPdf(string token)
    {
        var inner = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("", $"BT /F1 12 Tf 72 700 Td (Unredacted original: {token}) Tj ET", compress: true),
            Font,
        };
        return Assemble(inner, "");
    }

    private static byte[] OpaqueBlob(string token)
    {
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 37 % 251);
        bytes[0] = 0; // unmistakably binary
        var tokenBytes = Encoding.ASCII.GetBytes(token);
        tokenBytes.CopyTo(bytes, 100);
        return bytes;
    }

    private static string Xmp(string properties, string descriptionAttributes = "") =>
        Stream("/Type /Metadata /Subtype /XML", XmpPacket(properties, descriptionAttributes), compress: false);

    private static string XmpPacket(string properties, string descriptionAttributes) =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" " +
        "xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\" xmlns:stEvt=\"http://ns.adobe.com/xap/1.0/sType/ResourceEvent#\" " +
        descriptionAttributes + ">" +
        properties +
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>
    /// Revision 1 shows the token; revision 2 (an incremental update) replaces
    /// the content stream, leaving revision 1's object in the file.
    /// </summary>
    private static byte[] PriorRevision(string token, bool visible)
    {
        var rev1 = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("", $"BT /F1 12 Tf 72 700 Td (Claimant {token} draft) Tj ET", compress: true),
            Font,
        };
        var original = Assemble(rev1, "");
        var current = visible
            ? $"BT /F1 12 Tf 72 700 Td (Claimant {token} final) Tj ET"
            : "BT /F1 12 Tf 72 700 Td (Claimant XXXXXXXX final) Tj ET";
        return AppendUpdate(original, new Dictionary<int, string> { [4] = Stream("", current, compress: false) }, size: 6, trailerExtra: "");
    }

    // ── Low-level assembly ──────────────────────────────────────────────

    private static string Stream(string dictEntries, string data, bool compress) =>
        StreamBytes(dictEntries, Encoding.Latin1.GetBytes(data), compress);

    private static string StreamBytes(string dictEntries, byte[] data, bool compress)
    {
        var body = compress ? Deflate(data) : data;
        var filter = compress ? " /Filter /FlateDecode" : "";
        return $"<< {dictEntries}{filter} /Length {body.Length} >>\nstream\n" + Latin1(body) + "\nendstream";
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] Assemble(List<string> objs, string trailerExtra)
    {
        var sb = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new int[objs.Count];
        for (var i = 0; i < objs.Count; i++)
        {
            offsets[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Count + 1).Append(" /Root 1 0 R ").Append(trailerExtra)
          .Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Append an incremental update replacing <paramref name="replaced"/> objects (§7.5.6).</summary>
    private static byte[] AppendUpdate(byte[] original, Dictionary<int, string> replaced, int size, string trailerExtra)
    {
        var text = Encoding.Latin1.GetString(original);
        var sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var prev = int.Parse(text[(sx + 9)..].Trim().Split('\n')[0].Trim());

        var sb = new StringBuilder(text);
        var offsets = new SortedDictionary<int, int>();
        foreach (var (num, body) in replaced)
        {
            offsets[num] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(num).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 1\n0000000000 65535 f \n");
        foreach (var (num, off) in offsets)
            sb.Append(num).Append(" 1\n").Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R /Prev ").Append(prev).Append(' ')
          .Append(trailerExtra).Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}

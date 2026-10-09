using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.TestSupport;

/// <summary>
/// Certified forms (#2024): the shape Designer and Acrobat give IMM 5257e, the Ohio expense
/// report and the HSBC form. A catalog <c>/Perms</c> naming a DocMDP certification signature
/// (ISO 32000-2 §12.8.2.2) and a UR3 usage rights signature (§12.8.2.3); the DocMDP signature is
/// the value of a signature field filed under a subform field, with a zero-size widget on the first
/// page (IMM's <c>form1[0].SignatureField4[0]</c>); <c>/SigFlags 3</c>; and, optionally, a
/// <c>/Legal</c> attestation (Ohio), an approval signature <c>/Perms</c> does not name, and a
/// <c>/Lock</c> on the certification field. Every value that names a signer is a marker a byte
/// scanner can look for: <see cref="DocMdpSigner"/>, <see cref="Ur3Signer"/>,
/// <see cref="LegalMarker"/>, <see cref="ApprovalSigner"/>.
/// </summary>
internal static class CertifiedFormFixtures
{
    public const string DocMdpSigner = "DOCMDPSIGNERMARK";
    public const string Ur3Signer = "URTHREESIGNERMARK";
    public const string LegalMarker = "LEGALATTESTATIONMARK";
    public const string ApprovalSigner = "APPROVALSIGNERMARK";
    public const string SignatureAppearanceMarker = "SIGNEDAPPEARANCEMARK";

    /// <summary>The certification field's partial names: <c>form1[0]</c> then <c>SignatureField4[0]</c>.</summary>
    public const string ParentName = "form1[0]";
    public const string SignatureFieldName = "SignatureField4[0]";
    public const string SignatureFieldFullName = ParentName + "." + SignatureFieldName;
    public const string ApprovalFieldName = "Approval";

    /// <summary>A dynamic XFA form with a text field and an XFA signature field named like IMM's.</summary>
    public static byte[] DynamicForm() => XfaTestForms.BuildPdf(
        XfaTestForms.Template(
            "<field name=\"Name\" x=\"1in\" y=\"1in\" w=\"3in\" h=\"0.3in\"><ui><textEdit/></ui></field>"
            + "<field name=\"SignatureField4\" x=\"1in\" y=\"2in\" w=\"3in\" h=\"0.5in\"><ui><signature/></ui></field>",
            layout: "position"),
        XfaTestForms.Data("<Name>Ada Lovelace</Name>"));

    public sealed record Options
    {
        public bool DocMdp { get; init; } = true;
        public bool Ur3 { get; init; } = true;
        /// <summary>UR3 as a direct dictionary inside <c>/Perms</c> (Ohio), not a reference (IMM).</summary>
        public bool Ur3Direct { get; init; }
        /// <summary><c>/Perms</c> as an indirect object (HSBC's <c>59 0 R</c>).</summary>
        public bool PermsIndirect { get; init; }
        public bool Legal { get; init; } = true;
        public bool Approval { get; init; }
        public bool Lock { get; init; }
        /// <summary>
        /// List the certification widget in the first page's <c>/Annots</c> (true, IMM) or in no
        /// page's (false): then removing the placeholder page does not prune the field.
        /// </summary>
        public bool WidgetInAnnots { get; init; } = true;
        /// <summary>Give the widget a drawn appearance (a visible signature).</summary>
        public bool SignedAppearance { get; init; } = true;
    }

    /// <summary>Add the certification <paramref name="options"/> describe to <paramref name="pdf"/>.</summary>
    public static byte[] Certify(byte[] pdf, Options? options = null, string? password = null)
    {
        options ??= new Options();
        using var document = PdfDocument.Open(pdf, new PdfOpenOptions { UserPassword = password });
        var catalog = document.Catalog;
        var page = document.Pages[0];

        PdfDictionary acroForm;
        if (document.Resolve(catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary existing)
        {
            acroForm = existing;
        }
        else
        {
            acroForm = new PdfDictionary { ["Fields"] = new PdfArray() };
            catalog["AcroForm"] = document.AddIndirectObject(acroForm);
        }
        var fields = (PdfArray)document.Resolve(acroForm.GetOptional("Fields")!);
        var annots = document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) as PdfArray ?? new PdfArray();

        var perms = new PdfDictionary();
        if (options.DocMdp)
        {
            var signature = document.AddIndirectObject(Signature(DocMdpSigner, Transform("DocMDP",
                new PdfDictionary { ["Type"] = new PdfName("TransformParams"), ["P"] = new PdfInteger(2), ["V"] = new PdfName("1.2") })));
            perms["DocMDP"] = signature;

            var parent = new PdfDictionary { ["T"] = new PdfString(ParentName) };
            var parentRef = document.AddIndirectObject(parent);
            var field = new PdfDictionary
            {
                ["Type"] = new PdfName("Annot"),
                ["Subtype"] = new PdfName("Widget"),
                ["FT"] = new PdfName("Sig"),
                ["T"] = new PdfString(SignatureFieldName),
                ["Parent"] = parentRef,
                ["V"] = signature,
                ["Rect"] = new PdfArray { 0, 0, 0, 0 },
                ["F"] = new PdfInteger(132),
            };
            if (page.Reference is { } pageRef)
                field["P"] = pageRef;
            if (options.SignedAppearance)
            {
                var appearance = PdfStream.CreateCompressed(Encoding.ASCII.GetBytes(
                    $"BT /Helv 8 Tf 0 0 Td ({SignatureAppearanceMarker}) Tj ET"));
                appearance["Type"] = new PdfName("XObject");
                appearance["Subtype"] = new PdfName("Form");
                appearance["BBox"] = new PdfArray { 0, 0, 100, 20 };
                field["AP"] = new PdfDictionary { ["N"] = document.AddIndirectObject(appearance) };
            }
            if (options.Lock)
            {
                field["Lock"] = document.AddIndirectObject(new PdfDictionary
                {
                    ["Type"] = new PdfName("SigFieldLock"),
                    ["Action"] = new PdfName("All"),
                });
            }
            var fieldRef = document.AddIndirectObject(field);
            parent["Kids"] = new PdfArray { fieldRef };
            fields.Add(parentRef);
            if (options.WidgetInAnnots)
                annots.Add(fieldRef);
        }

        if (options.Ur3)
        {
            var ur3 = Signature(Ur3Signer, Transform("UR3", new PdfDictionary
            {
                ["Type"] = new PdfName("TransformParams"),
                ["V"] = new PdfName("2.2"),
                ["Document"] = new PdfArray { (PdfObject)new PdfName("FullSave") },
            }));
            perms["UR3"] = options.Ur3Direct ? ur3 : document.AddIndirectObject(ur3);
        }

        if (options.Approval)
        {
            var approval = document.AddIndirectObject(Signature(ApprovalSigner, null));
            var field = document.AddIndirectObject(new PdfDictionary
            {
                ["Type"] = new PdfName("Annot"),
                ["Subtype"] = new PdfName("Widget"),
                ["FT"] = new PdfName("Sig"),
                ["T"] = new PdfString(ApprovalFieldName),
                ["V"] = approval,
                ["Rect"] = new PdfArray { 0, 0, 0, 0 },
                ["F"] = new PdfInteger(132),
            });
            fields.Add(field);
            annots.Add(field);
        }

        page.Dictionary["Annots"] = annots;
        acroForm["SigFlags"] = new PdfInteger(3);
        if (perms.Count > 0)
            catalog["Perms"] = options.PermsIndirect ? document.AddIndirectObject(perms) : perms;
        if (options.Legal)
        {
            catalog["Legal"] = new PdfDictionary
            {
                ["JavaScriptActions"] = new PdfInteger(1),
                ["Attestation"] = new PdfString(LegalMarker),
            };
        }
        return document.SaveToBytes(document.GetReEncryptionOptions(password));
    }

    private static PdfDictionary Transform(string method, PdfDictionary parameters) => new()
    {
        ["Type"] = new PdfName("SigRef"),
        ["TransformMethod"] = new PdfName(method),
        ["TransformParams"] = parameters,
    };

    /// <summary>A signature dictionary (Table 255) whose <c>/Name</c> and <c>/Contents</c> carry <paramref name="signer"/>.</summary>
    private static PdfDictionary Signature(string signer, PdfDictionary? reference)
    {
        var signature = new PdfDictionary
        {
            ["Type"] = new PdfName("Sig"),
            ["Filter"] = new PdfName("Adobe.PPKLite"),
            ["SubFilter"] = new PdfName("adbe.pkcs7.detached"),
            ["ByteRange"] = new PdfArray { 0, 100, 200, 300 },
            ["Contents"] = new PdfString(Encoding.ASCII.GetBytes(signer + "CONTENTS"), isHex: true),
            ["Name"] = new PdfString(signer),
        };
        if (reference != null)
            signature["Reference"] = new PdfArray { reference };
        return signature;
    }
}

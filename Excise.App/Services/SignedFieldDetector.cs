using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.App.Services;

/// <summary>
/// Cheap in-memory check for "does this document have a signed /Sig field" --
/// shared by <see cref="SignatureApplicationService"/>'s already-signed guard
/// (issue #623) and <see cref="PdfDocumentService.HasSignatures"/> (#1415).
/// This is not cryptographic verification -- <see cref="SignatureVerificationService"/>
/// does that, from a saved file path. This only asks whether editing risks
/// invalidating something, so it stays a plain AcroForm/Fields walk.
/// </summary>
internal static class SignedFieldDetector
{
    public static bool HasSignedField(PdfDocument document)
    {
        var acroForm = document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) as PdfDictionary;
        var fields = acroForm != null
            ? document.Resolve(acroForm.GetOptional("Fields") ?? PdfNull.Instance) as PdfArray
            : null;
        if (fields == null)
            return false;

        foreach (var item in fields)
        {
            if (document.Resolve(item) is PdfDictionary fieldDict &&
                fieldDict.GetNameOrNull("FT") == "Sig" &&
                fieldDict.GetOptional("V") != null)
            {
                return true;
            }
        }
        return false;
    }
}

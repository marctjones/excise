namespace Excise.Core.Document;

/// <summary>
/// Type of PDF form field, based on the /FT entry in the field dictionary.
/// PDF spec §12.7.3.1.
/// </summary>
public enum PdfFieldType
{
    /// <summary>
    /// Field type is unknown or could not be determined.
    /// </summary>
    Unknown,

    /// <summary>
    /// Button field (/FT /Btn) — push button, check box, or radio button.
    /// </summary>
    Button,

    /// <summary>
    /// Text field (/FT /Tx) — single-line or multi-line text input.
    /// </summary>
    Text,

    /// <summary>
    /// Choice field (/FT /Ch) — list box or combo box.
    /// </summary>
    Choice,

    /// <summary>
    /// Signature field (/FT /Sig) — digital signature or certification.
    /// </summary>
    Signature,
}

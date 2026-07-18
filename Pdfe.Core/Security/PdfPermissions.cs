namespace Pdfe.Core.Security;

/// <summary>
/// Decoded view of the Standard Security Handler's <c>/P</c> permission
/// bitmask (ISO 32000-2 §7.6.4.2, Table 22). Issue #642.
///
/// <c>/P</c> is a SIGNED 32-bit integer whose high bits (13-32) are
/// required to be 1, so real-world values are negative (e.g. <c>-4</c> =
/// everything allowed, <c>-3392</c> = only extract-for-accessibility
/// allowed). Some writers store the same bit pattern as an unsigned value
/// (e.g. <c>4294963196</c>); this type normalizes on the low 32 bits, so
/// both spellings decode identically. Bit numbering below is the spec's
/// 1-based numbering (bit 3 = integer value 4). Bit semantics were
/// verified against qpdf 12's <c>--show-encryption</c> report on the
/// poppler test corpus (<c>P=-1028</c> denies exactly assembly;
/// <c>P=-3392</c> denies everything except accessibility extraction).
/// </summary>
/// <remarks>
/// <para><b>Enforcement policy (who checks this, and when).</b>
/// Permissions are enforced at the ACTION layer — GUI commands
/// (copy/export/edit/annotate), CLI verbs, and the scripting surface —
/// never inside the engine. <c>PdfPage.Text</c>, <c>Letters</c>, search,
/// rendering, and redaction remain permission-blind: pdfe's own internal
/// extraction powers search, the screen-reader/automation surface, and
/// redaction, and blanket-blocking it would lock assistive technology out
/// (the bit 10 accessibility carve-out) and break pdfe's core redaction
/// purpose. What bit 5 gates is user-initiated copy/export of content.</para>
/// <para><b>User vs owner password.</b> Per spec, permissions bind only a
/// user opening the document with the user password (including the empty
/// user password); the owner password unlocks everything. pdfe's decrypt
/// path currently verifies only the USER password — owner-password-only
/// opening is #324 and unsupported — so every successful open today is a
/// user-level open and <see cref="Document.PdfDocument.OpenedWithOwnerPassword"/>
/// is always false. When #324 lands, an owner-level open sets that flag and
/// <see cref="Document.PdfDocument.EffectivePermissions"/> becomes
/// <see cref="AllAllowed"/>, disabling enforcement naturally.</para>
/// <para><b>Explicit overrides.</b> Because the legitimate owner may only
/// hold the user password (or none, for empty-user-password files), every
/// enforcement point offers an explicit override: <c>--ignore-permissions</c>
/// on CLI verbs, <c>ignorePermissions: true</c> on batch-automation steps,
/// and <c>IgnoreDocumentPermissions</c> on the GUI scripting surface. Overrides are deliberate and loud (stderr/log),
/// mirroring the <c>--allow-decrypt</c> precedent from #638.</para>
/// </remarks>
public readonly struct PdfPermissions : IEquatable<PdfPermissions>
{
    /// <summary>Low 32 bits of /P, sign-normalized.</summary>
    private readonly uint _bits;

    /// <summary>
    /// Decode a raw <c>/P</c> value. Accepts either the spec's signed
    /// 32-bit form (negative) or the same bit pattern stored unsigned;
    /// only the low 32 bits are significant.
    /// </summary>
    public PdfPermissions(long rawValue) => _bits = unchecked((uint)rawValue);

    /// <summary>
    /// All permissions granted: raw value <c>-4</c> (0xFFFFFFFC — every
    /// permission bit set, the two reserved low-order bits zero), the
    /// conventional "no restrictions" mask. Also the effective permission
    /// set of an unencrypted document and of an owner-password open (#324).
    /// </summary>
    public static PdfPermissions AllAllowed { get; } = new(-4);

    /// <summary>
    /// The /P value as the spec's signed 32-bit integer (e.g. <c>-4</c>,
    /// <c>-3392</c>), regardless of whether the file stored it signed or
    /// unsigned.
    /// </summary>
    public int RawValue => unchecked((int)_bits);

    private bool Bit(int oneBasedBit) => (_bits & (1u << (oneBasedBit - 1))) != 0;

    /// <summary>Bit 3: print the document (possibly degraded; see <see cref="CanPrintHighQuality"/>).</summary>
    public bool CanPrint => Bit(3);

    /// <summary>Bit 4: modify the contents (other than the operations controlled by bits 6, 9, 11).</summary>
    public bool CanModify => Bit(4);

    /// <summary>
    /// Bit 5: copy or otherwise extract text and graphics. Gates
    /// user-initiated copy/export, NOT pdfe's internal extraction (search,
    /// rendering, accessibility, redaction) — see the enforcement-policy
    /// remarks on this type and the bit 10 carve-out
    /// (<see cref="CanExtractForAccessibility"/>).
    /// </summary>
    public bool CanCopy => Bit(5);

    /// <summary>Bit 6: add or modify text annotations and fill interactive form fields.</summary>
    public bool CanAnnotate => Bit(6);

    /// <summary>
    /// Fill in existing interactive form fields: bit 9 (fill-in even when
    /// annotation editing is denied) or bit 6 (annotate, which includes
    /// form fill-in per Table 22).
    /// </summary>
    public bool CanFillForms => Bit(9) || Bit(6);

    /// <summary>
    /// Bit 10: extract text/graphics in support of accessibility to users
    /// with disabilities. When set while bit 5 is clear, extraction *for
    /// accessibility* stays available even though general copy/export is
    /// denied (e.g. the CLI's <c>--for-accessibility</c> flag). Deprecated
    /// in PDF 2.0 (readers should always allow accessibility extraction),
    /// which is one more reason engine-internal extraction is never gated.
    /// </summary>
    public bool CanExtractForAccessibility => Bit(10);

    /// <summary>Bit 11: assemble the document (insert, rotate, delete pages; create outline items/thumbnails).</summary>
    public bool CanAssemble => Bit(11);

    /// <summary>
    /// Bit 12 (with bit 3): print to a representation from which a faithful
    /// digital copy could be generated. False whenever <see cref="CanPrint"/>
    /// is false; when printing is allowed but this is not, only degraded
    /// printing is permitted.
    /// </summary>
    public bool CanPrintHighQuality => Bit(3) && Bit(12);

    /// <inheritdoc/>
    public bool Equals(PdfPermissions other) => _bits == other._bits;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PdfPermissions other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (int)_bits;

    public static bool operator ==(PdfPermissions left, PdfPermissions right) => left.Equals(right);

    public static bool operator !=(PdfPermissions left, PdfPermissions right) => !left.Equals(right);

    /// <summary>
    /// Diagnostic string listing the raw value and any denied permissions,
    /// e.g. <c>"/P -3392 (denied: print, modify, copy, annotate, fill-forms, assemble, high-quality print)"</c>.
    /// </summary>
    public override string ToString()
    {
        var denied = new List<string>(8);
        if (!CanPrint) denied.Add("print");
        if (!CanModify) denied.Add("modify");
        if (!CanCopy) denied.Add("copy");
        if (!CanAnnotate) denied.Add("annotate");
        if (!CanFillForms) denied.Add("fill-forms");
        if (!CanExtractForAccessibility) denied.Add("extract-for-accessibility");
        if (!CanAssemble) denied.Add("assemble");
        if (!CanPrintHighQuality) denied.Add("high-quality print");
        return denied.Count == 0
            ? $"/P {RawValue} (all allowed)"
            : $"/P {RawValue} (denied: {string.Join(", ", denied)})";
    }
}

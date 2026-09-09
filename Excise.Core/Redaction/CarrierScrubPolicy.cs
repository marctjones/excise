using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Operations;

/// <summary>
/// How the term scrub treats ONE document-level carrier (#1188/#1169).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one policy is not enough.</b> Substring-stripping the term is right
/// for free-form prose and can be actively harmful for a KNOWN or STRUCTURED
/// string: redacting <c>your</c> turns
/// <c>https://www.irs.gov/your-account</c> into
/// <c>https://www.irs.gov/-account</c>, and anyone who knows the site's URL
/// scheme reads the removed word straight back out of the surrounding fixed
/// text. The residue REVEALS the secret the redaction was for (#1169).
/// </para>
/// <para>
/// ⚠️ The default stays <see cref="Strip"/> on every carrier, because #1187
/// requires the defaults to reproduce the pre-option behaviour exactly. That
/// #1169 argues URLs and structured metadata SHOULD default to
/// <see cref="RemoveWhole"/> is a product decision, not one this type makes
/// silently.
/// </para>
/// </remarks>
public enum CarrierScrubMode
{
    /// <summary>
    /// Cut the matched substring out and keep the rest of the value. The
    /// pre-#1188 behaviour and the default. Correct for free-form prose; leaves
    /// a length/structure residue in a known string.
    /// </summary>
    Strip,

    /// <summary>
    /// Drop the ENTIRE value the term was found in — the whole <c>/URI</c>,
    /// bookmark title, metadata field — so no surrounding structure survives to
    /// infer the removed text from. Destroys more of the document than
    /// <see cref="Strip"/>, deliberately.
    /// </summary>
    RemoveWhole,

    /// <summary>
    /// Change nothing; REPORT that the carrier holds the term and let the user
    /// decide. The "surface, don't guess" half of the decided carrier policy.
    /// </summary>
    /// <remarks>
    /// ⚠️ This LEAVES THE TERM IN THE DOCUMENT. A redaction run with a
    /// report-only carrier has not finished until the human acts on the report.
    /// </remarks>
    ReportOnly,
}

/// <summary>
/// What the term scrub did to one carrier — the per-carrier half of the
/// "visible in the result" rule (#1052/#1169): a user who cannot tell which
/// policy ran cannot reason about what was left behind.
/// </summary>
/// <param name="Carrier">The single carrier flag this row is about.</param>
/// <param name="Mode">The mode that actually ran.</param>
/// <param name="TermFound">
/// True when the term was present in this carrier. Under
/// <see cref="CarrierScrubMode.ReportOnly"/> this is the whole point of the run;
/// under the other modes it says the carrier was a real hit rather than a no-op.
/// </param>
/// <param name="Modified">True when the document was actually changed.</param>
/// <param name="RefusedReason">
/// Non-null when the requested mode could NOT be honoured — the carrier is
/// reported unscrubbed rather than silently downgraded to a mode the caller did
/// not ask for.
/// </param>
public sealed record CarrierScrubResult(
    RedactionCarriers Carrier,
    CarrierScrubMode Mode,
    bool TermFound,
    bool Modified,
    string? RefusedReason = null);

/// <summary>
/// The result of one <c>PdfDocumentSanitizer.ScrubTerms</c> pass: whether
/// anything changed, and one row per carrier that was in scope.
/// </summary>
public sealed record CarrierScrubOutcome(
    bool Changed,
    IReadOnlyList<CarrierScrubResult> Carriers)
{
    /// <summary>Nothing in scope, nothing done.</summary>
    public static CarrierScrubOutcome Empty { get; } =
        new(false, Array.Empty<CarrierScrubResult>());

    /// <summary>The row for <paramref name="carrier"/>, or null when it was out of scope.</summary>
    public CarrierScrubResult? For(RedactionCarriers carrier) =>
        Carriers.FirstOrDefault(c => c.Carrier == carrier);

    /// <summary>
    /// Carriers that hold the term and were deliberately NOT changed
    /// (<see cref="CarrierScrubMode.ReportOnly"/>) or could not be. These are
    /// the rows a caller must show a human.
    /// </summary>
    public IReadOnlyList<CarrierScrubResult> NeedingAttention =>
        Carriers.Where(c => (c.TermFound && !c.Modified) || c.RefusedReason != null).ToList();
}

/// <summary>
/// Per-carrier scrub modes (#1188's engine surface, #1169's policy model).
/// Immutable; build one with <see cref="Default"/> / <see cref="Uniform"/> and
/// narrow it with <see cref="With"/>.
/// </summary>
/// <example>
/// <code>
/// // Leave a known URL alone and tell me about it instead of shredding it.
/// var policy = CarrierScrubPolicy.Default
///     .With(RedactionCarriers.ActionUris, CarrierScrubMode.RemoveWhole);
/// </code>
/// </example>
public sealed class CarrierScrubPolicy : IEquatable<CarrierScrubPolicy>
{
    /// <summary>Every individual carrier flag, in declaration order.</summary>
    public static readonly IReadOnlyList<RedactionCarriers> AllCarriers = new[]
    {
        RedactionCarriers.Info,
        RedactionCarriers.Xmp,
        RedactionCarriers.Xfa,
        RedactionCarriers.Outlines,
        RedactionCarriers.Annotations,
        RedactionCarriers.FormFields,
        RedactionCarriers.StructTree,
        RedactionCarriers.JavaScript,
        RedactionCarriers.EmbeddedFiles,
        RedactionCarriers.ActionUris,
    };

    private readonly CarrierScrubMode[] _modes;

    private CarrierScrubPolicy(CarrierScrubMode[] modes) => _modes = modes;

    /// <summary>
    /// <see cref="CarrierScrubMode.Strip"/> everywhere — the pre-#1188
    /// behaviour, unchanged.
    /// </summary>
    public static CarrierScrubPolicy Default { get; } = Uniform(CarrierScrubMode.Strip);

    /// <summary>The same mode on every carrier.</summary>
    public static CarrierScrubPolicy Uniform(CarrierScrubMode mode)
    {
        var modes = new CarrierScrubMode[AllCarriers.Count];
        Array.Fill(modes, mode);
        return new CarrierScrubPolicy(modes);
    }

    /// <summary>The mode for one carrier. Unknown flags read as <see cref="CarrierScrubMode.Strip"/>.</summary>
    public CarrierScrubMode ModeFor(RedactionCarriers carrier)
    {
        var index = IndexOf(carrier);
        return index < 0 ? CarrierScrubMode.Strip : _modes[index];
    }

    /// <summary>
    /// A copy with <paramref name="mode"/> applied to every flag set in
    /// <paramref name="carriers"/> (a combination is allowed).
    /// </summary>
    public CarrierScrubPolicy With(RedactionCarriers carriers, CarrierScrubMode mode)
    {
        var modes = (CarrierScrubMode[])_modes.Clone();
        for (var i = 0; i < AllCarriers.Count; i++)
            if ((carriers & AllCarriers[i]) != 0)
                modes[i] = mode;
        return new CarrierScrubPolicy(modes);
    }

    /// <summary>True when every carrier uses <paramref name="mode"/>.</summary>
    public bool IsUniform(CarrierScrubMode mode) => _modes.All(m => m == mode);

    private static int IndexOf(RedactionCarriers carrier)
    {
        for (var i = 0; i < AllCarriers.Count; i++)
            if (AllCarriers[i] == carrier) return i;
        return -1;
    }

    /// <inheritdoc/>
    public bool Equals(CarrierScrubPolicy? other) =>
        other != null && _modes.AsSpan().SequenceEqual(other._modes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as CarrierScrubPolicy);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var mode in _modes) hash.Add(mode);
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        IsUniform(CarrierScrubMode.Strip) ? "all Strip"
        : string.Join(", ", AllCarriers.Select((c, i) => $"{c}={_modes[i]}"));
}

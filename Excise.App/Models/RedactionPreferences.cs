using System.Text.Json.Serialization;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;

namespace Excise.App.Models;

/// <summary>
/// The user's redaction preferences: the ONE record window.json persists (as
/// the nested <c>Redaction</c> object), the view models hold and the
/// Preferences dialog edits (#1840). A new redaction preference is one
/// property here plus its control.
/// </summary>
/// <remarks>
/// Settable so the dialog can bind to its own copy; a committed instance is
/// replaced, never edited. Enums persist as their names. Every default is the
/// pre-option behaviour (#1187): changing one is a product decision.
/// </remarks>
internal sealed record RedactionPreferences
{
    /// <summary>
    /// Match whole words only (#1052). Default false — substring matching, the
    /// #1000 decision: correct for a case number inside a longer citation,
    /// wrong for <c>Lee</c> inside <c>Sleeman</c>. Reaches the text-redaction
    /// path AND the document-carrier scrub together (a rule honoured in one
    /// path and not the other is #896).
    /// </summary>
    public bool WholeWord { get; set; }

    /// <summary>
    /// Keep the document's attachments in a redacted copy (#1572). Default
    /// false: since 2026-09-17 every redacted copy is written without them.
    /// When on, kept text attachments have the redacted text cut out, nested
    /// PDFs are redacted too, and anything else is listed in the report as not
    /// checked. A PDF portfolio is refused when this is off.
    /// </summary>
    public bool KeepAttachments { get; set; }

    /// <summary>
    /// How the removed run's WIDTH is handled (#1189): a layout choice and a
    /// SECURITY choice at once. The default box is drawn to the exact extent of
    /// the removed run, which makes it a ruler for the removed string's length
    /// (#1140). Overshoot rounds the box up; CloseGap removes the advance
    /// entirely, the only option that also closes the content-stream channel;
    /// FixedMarker (#1755) does that and always draws a visible mark (#1725),
    /// but is not the default: see the remark on <see cref="RedactionOptions.Width"/>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<WidthPolicy>))]
    public WidthPolicy Width { get; set; } = WidthPolicy.CollapsePreserveLayout;

    /// <summary>
    /// The output profile (#1586). ⚠️ Maximum produces output that is no longer
    /// accessible or interactive: every surface that offers it must SAY so; the
    /// redacted-copy report carries the line.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RedactionProfile>))]
    public RedactionProfile Profile { get; set; } = RedactionProfile.Standard;

    /// <summary>
    /// How a link's <c>/A /URI</c> holding the redacted term is handled (#1169).
    /// ⚠️ A SECURITY choice: cutting the term out of a URL whose shape is public
    /// knowledge can hand it back (<c>https://www.irs.gov/your-account</c> minus
    /// <c>your</c>). RemoveWhole drops the whole target; ReportOnly changes
    /// nothing and lists the carrier in the redacted-copy report.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<CarrierScrubMode>))]
    public CarrierScrubMode LinkUriPolicy { get; set; } = CarrierScrubMode.Strip;

    /// <summary>The same for /Info and the XMP packet (#1169).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<CarrierScrubMode>))]
    public CarrierScrubMode MetadataPolicy { get; set; } = CarrierScrubMode.Strip;

    /// <summary>
    /// The options these preferences describe, for the engine pass and the
    /// redacted-copy safety pass alike (#1830).
    /// </summary>
    public RedactionOptions ToOptions()
    {
        // #1586: start from the PROFILE's policy, not the all-Strip default.
        // Maximum's whole point is RemoveWhole on every kept carrier, and
        // rebuilding from Default here would silently throw that away.
        var options = RedactionOptions.ForProfile(Profile);
        var policy = options.CarrierPolicy;

        // ⚠️ A per-carrier preference overrides the profile only when the user
        // MOVED it off Strip. Both default to Strip, so applying them
        // unconditionally would put link targets and metadata back to Strip
        // under Maximum. The enum has no "follow the profile" value, so "still
        // at the default" means exactly that.
        if (LinkUriPolicy != CarrierScrubMode.Strip)
            policy = policy.With(RedactionCarriers.ActionUris, LinkUriPolicy);
        if (MetadataPolicy != CarrierScrubMode.Strip)
            policy = policy.With(RedactionCarriers.Info | RedactionCarriers.Xmp, MetadataPolicy);

        return options with
        {
            CarrierPolicy = policy,
            WholeWord = WholeWord,
            KeepAttachments = KeepAttachments,
            Width = Width,
        };
    }
}

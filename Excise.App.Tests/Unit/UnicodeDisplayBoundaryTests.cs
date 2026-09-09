using System.Collections.ObjectModel;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1205 — the App-side interpretation boundaries.
///
/// <para>Each of these is a place where excise takes text the DOCUMENT wrote and
/// shows it to the user as a name they act on: a bookmark they navigate by, a
/// signer they decide to trust. A bidi override in such a string makes the
/// display say something different from the underlying bytes — the whole point
/// of the attack — so the controls are made explicit at the display, and only at
/// the display.</para>
///
/// <para>What is deliberately NOT touched: page text, search results, redaction
/// previews, annotation note bodies and copy values. Those are prose, and the
/// issue's rule is to preserve them exactly.</para>
/// </summary>
public class UnicodeDisplayBoundaryTests
{
    private const string Rlo = "‮";

    [Fact]
    public void OutlineLabel_MakesABidiOverrideExplicit_WithoutChangingTheTitle()
    {
        var node = new OutlineNode($"Exhibit{Rlo}A", 7, new ObservableCollection<OutlineNode>());

        node.DisplayText.Should().Contain("[U+202E]",
            "a bookmark is a navigation label the user picks a destination by");
        node.DisplayText.Should().NotContain(Rlo);
        node.Title.Should().Be($"Exhibit{Rlo}A",
            "the underlying title keeps the document's exact characters — this is a " +
            "display policy, not a normalisation");
    }

    [Fact]
    public void OutlineLabel_LeavesOrdinaryTitlesAlone()
    {
        var node = new OutlineNode("Anexo I — Condições Gerais", 3,
            new ObservableCollection<OutlineNode>());
        node.DisplayText.Should().Be("Anexo I — Condições Gerais  p.3");
    }

    [Fact]
    public void SignatureSummary_EscapesTheSignerIdentity_AndWarnsAboutBidi()
    {
        // A signature summary is the display a trust decision is made on, and
        // the signer name comes out of the document's own certificate.
        var result = new SignatureVerificationResult
        {
            SignatureName = "Signature1",
            SignedBy = $"CN=Acme{Rlo}Bank, O=Acme",
            IsValid = true,
        };

        var summary = new SignatureVerificationSummaryFormatter().Format(new[] { result });

        summary.Should().Contain("[U+202E]", "the control is shown, not obeyed");
        summary.Should().NotContain(Rlo);
        summary.Should().Contain("text-direction control characters",
            "a reordered identity defeats the trust decision, so it gets its own warning");
    }

    [Fact]
    public void SignatureSummary_LeavesAnOrdinarySignerAlone()
    {
        var result = new SignatureVerificationResult
        {
            SignatureName = "Signature1",
            SignedBy = "CN=Renée Dupont, O=Société Générale",
            IsValid = true,
        };

        var summary = new SignatureVerificationSummaryFormatter().Format(new[] { result });

        summary.Should().Contain("CN=Renée Dupont, O=Société Générale");
        summary.Should().NotContain("text-direction control characters");
    }
}

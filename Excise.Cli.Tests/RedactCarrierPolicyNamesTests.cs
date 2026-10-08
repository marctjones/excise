using System.Collections;
using System.Reflection;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1896 — <c>--carrier-policy</c> names the document-level carriers a user can
/// give a per-carrier mode. The list in the help text and the parser's table once
/// stopped at marked-content while the engine went on to scrub and report
/// page labels, name-tree keys, signatures and optional content. These tests
/// reflect the ENGINE's carrier sets against the CLI list, so a carrier added to
/// the engine without a CLI name fails here instead of staying invisible.
/// This is help and parsing only; what gets scrubbed is the engine's business.
/// </summary>
public class RedactCarrierPolicyNamesTests
{
    /// <summary>The carriers the redaction report names, read off the engine's private table.</summary>
    private static IReadOnlyList<(string Display, RedactionCarriers Flag)> ReportedDocumentCarriers()
    {
        var field = typeof(PdfDocumentRedactionExtensions).GetField(
            "DocumentCarriers", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("the engine's reported-carrier table moved; update this reflection (#1896)");

        var rows = new List<(string, RedactionCarriers)>();
        foreach (var row in (IEnumerable)field!.GetValue(null)!)
        {
            var t = row.GetType();
            rows.Add(((string)t.GetField("Item1")!.GetValue(row)!,
                (RedactionCarriers)t.GetField("Item2")!.GetValue(row)!));
        }

        rows.Should().NotBeEmpty();
        return rows;
    }

    private static string HelpText() =>
        RedactCommand.Create().Options.Single(o => o.Name == "--carrier-policy").Description!;

    [Fact]
    public void EveryReportedDocumentCarrier_HasACliName()
    {
        var named = RedactCommand.CarrierNames.Select(c => c.Flag).ToHashSet();

        foreach (var (display, flag) in ReportedDocumentCarriers())
            named.Should().Contain(flag, $"the report names '{display}' but --carrier-policy has no name for it");
    }

    [Fact]
    public void EveryPolicyCarrierFlag_HasACliName()
    {
        // CarrierScrubPolicy.AllCarriers is every flag the policy can hold a mode for.
        var named = RedactCommand.CarrierNames.Select(c => c.Flag).ToHashSet();

        foreach (var flag in CarrierScrubPolicy.AllCarriers)
            named.Should().Contain(flag, $"the policy holds a mode for {flag} but the CLI cannot set it");
    }

    [Fact]
    public void EveryCliName_IsInTheHelpText_AndParses()
    {
        var help = HelpText();

        foreach (var (name, flag) in RedactCommand.CarrierNames)
        {
            help.Should().Contain(name, $"the help text must list '{name}'");

            RedactCommand.TryParseCarrierPolicy(new[] { $"{name}=report-only" }, out var policy, out var error)
                .Should().BeTrue($"'{name}' is listed, so it must parse (error: {error})");
            foreach (var single in CarrierScrubPolicy.AllCarriers.Where(f => (flag & f) != 0))
                policy.ModeFor(single).Should().Be(CarrierScrubMode.ReportOnly, name);
        }
    }

    [Theory]
    [InlineData("page-labels", RedactionCarriers.PageLabels)]
    [InlineData("name-tree-keys", RedactionCarriers.NameTreeKeys)]
    [InlineData("signatures", RedactionCarriers.Signatures)]
    [InlineData("optional-content", RedactionCarriers.OptionalContent)]
    public void NewlyListedCarrier_SetsOnlyThatCarrier(string name, RedactionCarriers carrier)
    {
        RedactCommand.TryParseCarrierPolicy(new[] { $"{name}=remove-whole" }, out var policy, out _)
            .Should().BeTrue();

        policy.ModeFor(carrier).Should().Be(CarrierScrubMode.RemoveWhole);
        policy.ModeFor(RedactionCarriers.Info).Should().Be(CarrierScrubMode.Strip, "other carriers keep their default");
    }

    [Fact]
    public void UnknownCarrierError_ListsTheSameNamesAsTheHelp()
    {
        RedactCommand.TryParseCarrierPolicy(new[] { "nonsense=strip" }, out _, out var error)
            .Should().BeFalse();

        error.Should().Contain(RedactCommand.CarrierNameList);
    }
}

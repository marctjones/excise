using System.Xml.Linq;
using AwesomeAssertions;
using Excise.Core.Xfa;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// Check-button on and off values in the dynamic-XFA merge (#2016). XFA 3.3 p759 (the
/// <c>items</c> property): the first value is "on", the second "off", the third "neutral"; a
/// second or third value that is not provided defaults to the null string.
/// </summary>
public class XfaCheckButtonValueTests
{
    private static XElement Field(string name, string items)
        => XElement.Parse($"<field xmlns=\"{XfaTestForms.TemplateNamespace}\" name=\"{name}\"><ui><checkButton/></ui>{items}</field>");

    [Fact]
    public void OneItem_OffIsTheNullString_PerXfa33Page759()
    {
        var field = Field("Solo", "<items><text>on</text></items>");

        XfaValues.OnValue(field).Should().Be("on");
        XfaValues.OffValue(field).Should().Be(string.Empty,
            "an items list without a second value has the null string as its off value (XFA 3.3 p759)");
    }

    [Fact]
    public void TwoItems_OffIsTheSecond()
    {
        var field = Field("Pair", "<items><text>Y</text><text>N</text></items>");

        XfaValues.OffValue(field).Should().Be("N");
    }

    /// <summary>
    /// No <c>items</c> at all: the pages of the spec read for #2016 (p650-651 checkButton, p758-760
    /// items) do not state a default; "1"/"0" is kept as before and is not spec-verified.
    /// </summary>
    [Fact]
    public void NoItems_KeepsOneAndZero()
    {
        var field = Field("Bare", string.Empty);

        XfaValues.OnValue(field).Should().Be("1");
        XfaValues.OffValue(field).Should().Be("0");
    }

    [Fact]
    public void ExclGroupMember_WithOneItem_TakesTheNullStringWhenAnotherMemberIsSelected()
    {
        var template = XElement.Parse(XfaTestForms.Template(
            "<exclGroup name=\"Sex\">"
            + "<field name=\"Male\"><ui><checkButton/></ui><items><text>M</text></items></field>"
            + "<field name=\"Female\"><ui><checkButton/></ui><items><text>F</text></items></field>"
            + "</exclGroup>"));
        var data = XElement.Parse(XfaTestForms.Data("<Sex>F</Sex>"));
        var budget = new XfaBudget(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var report = new XfaReport();

        var root = new XfaMerge(budget, report, data).Merge(new XfaTemplate(template, budget, report).Root);

        var group = root.Children.Single(c => c.Kind == XfaNodeKind.ExclGroup);
        var members = group.Children.Where(c => c.Kind == XfaNodeKind.Field).ToList();
        members.Select(m => m.Value).Should().Equal(new[] { string.Empty, "F" },
            "the unselected member takes its off value, the null string here (XFA 3.3 p759), not \"0\"");
    }
}

namespace Excise.Rendering.Tests;

public sealed class ContentOperatorRouteTests
{
    private static readonly IReadOnlyDictionary<ContentOperatorFamily, string[]> OperatorsByFamily =
        new Dictionary<ContentOperatorFamily, string[]>
        {
            [ContentOperatorFamily.MarkedContent] = ["BMC", "BDC", "EMC", "MP", "DP"],
            [ContentOperatorFamily.GraphicsState] = ["q", "Q", "cm", "w", "J", "j", "M", "d", "ri", "i"],
            [ContentOperatorFamily.Color] = ["g", "G", "rg", "RG", "k", "K", "CS", "cs", "SC", "SCN", "sc", "scn"],
            [ContentOperatorFamily.Path] = ["m", "l", "c", "v", "y", "h", "re", "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n", "W", "W*"],
            [ContentOperatorFamily.Text] = ["BT", "ET", "Tf", "Td", "TD", "Tm", "T*", "Tc", "Tw", "Tz", "TL", "Tr", "Ts", "Tj", "TJ", "'", "\""],
            [ContentOperatorFamily.Resource] = ["gs", "sh"],
            [ContentOperatorFamily.XObjectImage] = ["Do", "BI"],
            [ContentOperatorFamily.Type3] = ["d0", "d1"],
            [ContentOperatorFamily.Compatibility] = ["BX", "EX"],
        };

    [Fact]
    public void Resolve_RegistersEverySupportedOperatorExactlyOnce()
    {
        var routes = OperatorsByFamily
            .SelectMany(group => group.Value.Select(name => (Name: name, ExpectedFamily: group.Key)))
            .Select(expected => (expected.Name, Route: ContentOperatorRoute.Resolve(expected.Name), expected.ExpectedFamily))
            .ToArray();

        Assert.Equal(routes.Length, routes.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(routes.Length, routes.Select(item => item.Route.Kind).Distinct().Count());
        Assert.Equal(Enum.GetValues<ContentOperatorKind>().Length - 1, routes.Length);

        foreach (var item in routes)
        {
            Assert.Equal(item.ExpectedFamily, item.Route.Family);
            Assert.NotEqual(ContentOperatorKind.Unknown, item.Route.Kind);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("ID")]
    [InlineData("EI")]
    [InlineData("future-operator")]
    public void Resolve_LeavesUnknownOperatorsUnrouted(string name)
    {
        var route = ContentOperatorRoute.Resolve(name);

        Assert.Equal(ContentOperatorFamily.Unknown, route.Family);
        Assert.Equal(ContentOperatorKind.Unknown, route.Kind);
    }

    [Theory]
    [InlineData("BMC", true)]
    [InlineData("BDC", true)]
    [InlineData("EMC", true)]
    [InlineData("MP", false)]
    [InlineData("DP", false)]
    public void Resolve_IdentifiesOnlyMarkedContentScopeOperators(string name, bool expected)
    {
        Assert.Equal(expected, ContentOperatorRoute.Resolve(name).IsMarkedContentScope);
    }

    [Theory]
    [InlineData("g")]
    [InlineData("G")]
    [InlineData("rg")]
    [InlineData("RG")]
    [InlineData("k")]
    [InlineData("K")]
    [InlineData("CS")]
    [InlineData("cs")]
    [InlineData("SC")]
    [InlineData("SCN")]
    [InlineData("sc")]
    [InlineData("scn")]
    public void Resolve_IdentifiesEveryType3ColorLockOperator(string name)
    {
        Assert.True(ContentOperatorRoute.Resolve(name).IsColorSetting);
    }
}

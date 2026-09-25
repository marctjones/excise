using System.Globalization;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Primitives;

/// <summary>
/// ISO 32000-2 §7.9.4: <c>D:YYYYMMDDHHmmSSOHH'mm</c>; only the year is mandatory, the offset
/// apostrophes are optional (1.7 wrote a trailing one), a missing offset means GMT. The
/// expectations are the spec's own wording and its worked example, not the parser's output.
/// </summary>
public class PdfDateTests
{
    public static TheoryData<string, string> Cases() => new()
    {
        { "D:2024", "2024-01-01T00:00:00+00:00" },
        { "D:202403", "2024-03-01T00:00:00+00:00" },
        { "D:20240102", "2024-01-02T00:00:00+00:00" },
        { "D:2024010203", "2024-01-02T03:00:00+00:00" },
        { "D:202401020304", "2024-01-02T03:04:00+00:00" },
        { "D:20240102030405", "2024-01-02T03:04:05+00:00" },
        { "D:20240102030405Z", "2024-01-02T03:04:05+00:00" },
        { "D:20240102030405Z00'00'", "2024-01-02T03:04:05+00:00" },
        { "D:20240102030405+05'00'", "2024-01-02T03:04:05+05:00" },
        { "D:20240102030405+05'00", "2024-01-02T03:04:05+05:00" },
        { "D:20240102030405+05'", "2024-01-02T03:04:05+05:00" },
        { "D:20240102030405-08'30'", "2024-01-02T03:04:05-08:30" },
        { "D:20240102030405-08'30", "2024-01-02T03:04:05-08:30" },
        // §7.9.4 EXAMPLE: December 23, 1998, 7:52 PM, U.S. Pacific Standard Time.
        { "D:199812231952-08'00", "1998-12-23T19:52:00-08:00" },
        { "D:199812231952-08'00'", "1998-12-23T19:52:00-08:00" },
        { "20240115120000", "2024-01-15T12:00:00+00:00" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parse_ReadsEveryFormAllowedBySection794(string raw, string expectedIso) =>
        AssertSame(PdfDate.Parse(raw), expectedIso);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D:")]
    [InlineData("D:20")]
    [InlineData("InvalidDateString")]
    [InlineData("D:00000101")]
    [InlineData("D:20241301")]
    [InlineData("D:20240230")]
    [InlineData("D:20240102250000")]
    [InlineData("D:20240102030405+99'00'")]
    [InlineData("D:20240102030405+")]
    [InlineData("D:20240102030405+0530")] // §7.9.4 puts an apostrophe between HH and mm
    [InlineData("D:2024ab")]
    [InlineData("D:2024-01-02")] // ISO 8601, not a PDF date: '-' must not be read as an offset sign
    [InlineData("2024-01-02T03:04:05Z")]
    [InlineData("D:20240102T030405")]
    public void Parse_NotADate_ReturnsNull(string? raw) => PdfDate.Parse(raw).Should().BeNull();

    [Theory]
    [InlineData("2026-07-25T09:30:00+00:00", "D:20260725093000+00'00'")]
    [InlineData("2026-07-25T09:30:00+05:30", "D:20260725093000+05'30'")]
    [InlineData("2026-07-25T09:30:00-08:30", "D:20260725093000-08'30'")]
    public void Format_WritesTheLocalTimeWithItsOffset_AndParseReadsItBack(string iso, string expected)
    {
        var time = DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

        PdfDate.Format(time).Should().Be(expected);
        AssertSame(PdfDate.Parse(PdfDate.Format(time)), iso);
    }

    [Fact]
    public void Format_IsCultureInvariant()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH"); // Buddhist calendar: 2026 would print as 2569
            PdfDate.Format(new DateTimeOffset(2026, 7, 25, 9, 30, 0, TimeSpan.Zero)).Should().Be("D:20260725093000+00'00'");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void AddEmbeddedFile_StampsDatesTheParserReadsBack()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        doc.AddEmbeddedFile("notes.txt", [1, 2, 3]);

        var file = doc.GetEmbeddedFiles().Single();
        file.CreationDate.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        file.ModDate.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void AddSquareAnnotation_StampsDatesTheParserReadsBack()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddSquareAnnotation(1, new PdfRectangle(100, 500, 300, 600), "region");

        annotation.CreationDate.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    internal static void AssertSame(DateTimeOffset? actual, string expectedIso)
    {
        var expected = DateTimeOffset.Parse(expectedIso, CultureInfo.InvariantCulture);
        actual.Should().NotBeNull();
        actual!.Value.EqualsExact(expected).Should().BeTrue($"{actual} should be {expected}");
    }
}

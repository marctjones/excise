using AwesomeAssertions;
using Excise.Rendering.Differential;

namespace Excise.Rendering.Tests.Differential;

public class PdfiumReferenceRendererTests
{
    [Fact]
    public void BuildPdfiumTestArguments_WithUserPassword_PassesPasswordBeforePdfPath()
    {
        var args = PdfiumReferenceRenderer.BuildPdfiumTestArguments(
            "/tmp/input.pdf",
            zeroBasedPage: 2,
            scale: 2.0,
            userPassword: "user-secret");

        args.Should().Contain("--password=user-secret");
        args.Should().EndWith("/tmp/input.pdf");
        args.ToList().IndexOf("--password=user-secret").Should().BeLessThan(args.Count - 1,
            "pdfium_test options must be emitted before the input PDF path");
    }

    [Fact]
    public void BuildPdfiumTestArguments_WithoutUserPassword_DoesNotEmitPasswordOption()
    {
        var args = PdfiumReferenceRenderer.BuildPdfiumTestArguments(
            "/tmp/input.pdf",
            zeroBasedPage: 0,
            scale: 1.0,
            userPassword: null);

        args.Should().NotContain(arg => arg.StartsWith("--password=", StringComparison.Ordinal));
    }
}

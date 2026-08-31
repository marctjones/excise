using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Xunit;

namespace Excise.Core.Tests.Document;

public class PdfDocumentOpenPipelineTests
{
    [Fact]
    public void Open_ParseFailure_DisposesOwnedStream()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\nbroken"));

        var open = () => PdfDocument.Open(stream, ownsStream: true);

        open.Should().Throw<PdfParseException>();
        stream.CanRead.Should().BeFalse(
            "the open pipeline retains ownership until a document is constructed");
    }

    [Fact]
    public void Open_ParseFailure_LeavesBorrowedStreamOpen()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\nbroken"));

        var open = () => PdfDocument.Open(stream, ownsStream: false);

        open.Should().Throw<PdfParseException>();
        stream.CanRead.Should().BeTrue();
        stream.Position = 0;
        stream.ReadByte().Should().Be((byte)'%');
    }

    [Fact]
    public void Open_CatalogValidationFailure_DisposesConstructedObjectStore()
    {
        var stream = new MemoryStream(BuildPdfWithNonDictionaryRoot());

        var open = () => PdfDocument.Open(stream, ownsStream: true);

        open.Should().Throw<PdfParseException>()
            .WithMessage("*Could not load document catalog*");
        stream.CanRead.Should().BeFalse(
            "root validation happens after the object store takes stream ownership");
    }

    [Fact]
    public void CreateNew_UsesInitializedOpenPipelineAndSingleObjectIdentity()
    {
        using var document = PdfDocument.CreateNew("2.0");

        document.Version.Should().Be("2.0");
        document.GetObject(document.Trailer.GetReference("Root"))
            .Should().BeSameAs(document.Catalog);

        document.Pages.AddBlank();
        var page = document.Pages[0];
        var saved = document.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(1);
        reopened.GetObject(reopened.Trailer.GetReference("Root"))
            .Should().BeSameAs(reopened.Catalog);
        page.Dictionary.Should().NotBeSameAs(reopened.Pages[0].Dictionary);
    }

    private static byte[] BuildPdfWithNonDictionaryRoot()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(
            stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };

        writer.WriteLine("%PDF-1.7");
        writer.Flush();

        var rootOffset = stream.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("42");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefOffset = stream.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 2");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{rootOffset:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 2 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefOffset);
        writer.WriteLine("%%EOF");
        writer.Flush();

        return stream.ToArray();
    }
}

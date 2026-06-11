using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Editing;
using Pdfe.Core.Graphics;
using Xunit;

namespace Pdfe.Core.Tests.Editing;

public class PdfTypewriterTextOperationTests
{
    [Fact]
    public void Create_StoresTextBoundsStyleAndPendingState()
    {
        var bounds = new PdfRectangle(72, 650, 240, 690);
        var style = new PdfTypewriterTextStyle(
            PdfFont.StandardFonts.Courier,
            fontSize: 10,
            color: PdfColor.Blue,
            alignment: TextAlignment.Center);

        var operation = PdfTypewriterTextOperation.Create(1, bounds, "Typed value", style);

        operation.Id.Should().NotBe(Guid.Empty);
        operation.PageNumber.Should().Be(1);
        operation.Bounds.Should().Be(bounds);
        operation.Text.Should().Be("Typed value");
        operation.Style.Should().Be(style);
        operation.Status.Should().Be(PdfEditOperationStatus.Pending);
        operation.IsPending.Should().BeTrue();
        operation.HasText.Should().BeTrue();
        operation.EditOperation.Kind.Should().Be(PdfEditOperationKind.TypewriterText);
    }

    [Fact]
    public void WithTextAndBounds_PreserveOperationIdentity()
    {
        var operation = PdfTypewriterTextOperation.Create(
            1,
            new PdfRectangle(0, 0, 100, 20),
            "before");
        var newBounds = new PdfRectangle(10, 10, 130, 40);

        var updated = operation.WithText("after").WithBounds(newBounds);

        updated.Id.Should().Be(operation.Id);
        updated.Text.Should().Be("after");
        updated.Bounds.Should().Be(newBounds);
        operation.Text.Should().Be("before");
    }

    [Fact]
    public void Apply_FlattensTextIntoPageContentAndMarksApplied()
    {
        using var document = PdfDocument.CreateNew();
        document.Pages.AddBlank(300, 400);
        var operation = PdfTypewriterTextOperation.Create(
            1,
            new PdfRectangle(40, 250, 260, 290),
            "Office worker note");

        var applied = PdfTypewriterTextApplier.Apply(document, operation);

        applied.Status.Should().Be(PdfEditOperationStatus.Applied);
        document.GetPage(1).Text.Should().Contain("Office worker note");
    }

    [Fact]
    public void Apply_SurvivesSaveAndReopenAsFlatText()
    {
        using var document = PdfDocument.CreateNew();
        document.Pages.AddBlank(300, 400);
        var operation = PdfTypewriterTextOperation.Create(
            1,
            new PdfRectangle(40, 250, 260, 290),
            "Saved typewriter text");

        PdfTypewriterTextApplier.Apply(document, operation);

        using var reopened = PdfDocument.Open(document.SaveToBytes());
        reopened.GetPage(1).Text.Should().Contain("Saved typewriter text");
    }

    [Fact]
    public void Apply_SkipsEmptyText()
    {
        using var document = PdfDocument.CreateNew();
        document.Pages.AddBlank(300, 400);
        var operation = PdfTypewriterTextOperation.Create(
            1,
            new PdfRectangle(40, 250, 260, 290),
            "   ");

        var applied = PdfTypewriterTextApplier.Apply(document, new[] { operation });

        applied.Should().BeEmpty();
        document.GetPage(1).Text.Should().BeEmpty();
    }
}

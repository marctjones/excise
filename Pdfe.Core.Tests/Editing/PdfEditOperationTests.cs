using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Editing;
using Xunit;

namespace Pdfe.Core.Tests.Editing;

public class PdfEditOperationTests
{
    [Fact]
    public void Create_StoresOperationMetadata()
    {
        var bounds = new PdfRectangle(10, 20, 110, 40);

        var operation = PdfEditOperation.Create(
            PdfEditOperationKind.TypewriterText,
            pageNumber: 2,
            bounds,
            description: "typed date");

        operation.Id.Should().NotBe(Guid.Empty);
        operation.Kind.Should().Be(PdfEditOperationKind.TypewriterText);
        operation.PageNumber.Should().Be(2);
        operation.Bounds.Should().Be(bounds);
        operation.Status.Should().Be(PdfEditOperationStatus.Pending);
        operation.IsPending.Should().BeTrue();
        operation.CanFlatten.Should().BeTrue();
        operation.Description.Should().Be("typed date");
    }

    [Fact]
    public void WithStatus_ReturnsUpdatedImmutableOperation()
    {
        var operation = PdfEditOperation.Create(
            PdfEditOperationKind.FormFill,
            1,
            new PdfRectangle(0, 0, 50, 20));

        var applied = operation.WithStatus(PdfEditOperationStatus.Applied);

        applied.Should().NotBeSameAs(operation);
        applied.Id.Should().Be(operation.Id);
        applied.Status.Should().Be(PdfEditOperationStatus.Applied);
        applied.IsPending.Should().BeFalse();
        operation.Status.Should().Be(PdfEditOperationStatus.Pending);
    }

    [Fact]
    public void WithBounds_ReturnsUpdatedImmutableOperationWithSameIdentity()
    {
        var operation = PdfEditOperation.Create(
            PdfEditOperationKind.TypewriterText,
            1,
            new PdfRectangle(0, 0, 50, 20));
        var newBounds = new PdfRectangle(10, 20, 110, 50);

        var moved = operation.WithBounds(newBounds);

        moved.Id.Should().Be(operation.Id);
        moved.PageNumber.Should().Be(operation.PageNumber);
        moved.Bounds.Should().Be(newBounds);
        operation.Bounds.Should().Be(new PdfRectangle(0, 0, 50, 20));
    }

    [Fact]
    public void Create_RejectsInvalidPageNumber()
    {
        var action = () => PdfEditOperation.Create(
            PdfEditOperationKind.Annotation,
            0,
            new PdfRectangle(0, 0, 10, 10));

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_RejectsUnnormalizedBounds()
    {
        var action = () => PdfEditOperation.Create(
            PdfEditOperationKind.Annotation,
            1,
            new PdfRectangle(20, 0, 10, 10));

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Bounds must be normalized*");
    }
}

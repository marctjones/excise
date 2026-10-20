using System.IO;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #2023: the thumbnail cache identity of a laid-out XFA form must carry its data. The file part of
/// the identity is path, length and mtime only (ThumbnailCacheService.BuildCacheIdentity, to keep
/// open fast), so a file whose datasets change while its length and mtime do not (a same-length
/// value, a copy that keeps the mtime) reused the old form's thumbnails: stale pictures of the old
/// filled values. The salt was the constant "xfa-layout-v1".
/// </summary>
public sealed class XfaThumbnailCacheSaltTests
{
    private static readonly XfaLayoutResult LaidOut = new() { Status = XfaLayoutStatus.LaidOut, PageCount = 1 };

    private static readonly string Template = XfaTestForms.Template(
        "<field name=\"Name\" w=\"3in\" h=\"0.3in\"><ui><textEdit/></ui></field>");

    private static PdfDocument Open(byte[] bytes) => PdfDocument.Open(new MemoryStream(bytes));

    [Fact]
    public void DifferentDatasets_SameLength_GiveDifferentSalts()
    {
        using var alpha = Open(XfaTestForms.BuildPdf(Template, XfaTestForms.Data("<Name>ALPHA</Name>")));
        using var bravo = Open(XfaTestForms.BuildPdf(Template, XfaTestForms.Data("<Name>BRAVO</Name>")));

        XfaThumbnailCacheSalt.For(alpha, LaidOut).Should().NotBe(XfaThumbnailCacheSalt.For(bravo, LaidOut),
            "thumbnails of one form's values must not be served for another's");
    }

    [Fact]
    public void SameBytes_GiveTheSameSalt_SoTheCacheStillHits()
    {
        var bytes = XfaTestForms.BuildPdf(Template, XfaTestForms.Data("<Name>ALPHA</Name>"));
        using var first = Open(bytes);
        using var second = Open(bytes);

        XfaThumbnailCacheSalt.For(first, LaidOut).Should().NotBeNull()
            .And.Be(XfaThumbnailCacheSalt.For(second, LaidOut));
    }

    [Fact]
    public void NotLaidOut_HasNoSalt_TheFileThumbnailsApply()
    {
        using var doc = Open(XfaTestForms.BuildPdf(Template, XfaTestForms.Data("<Name>ALPHA</Name>")));

        XfaThumbnailCacheSalt.For(doc, null).Should().BeNull();
        XfaThumbnailCacheSalt.For(doc, new XfaLayoutResult { Status = XfaLayoutStatus.Failed }).Should().BeNull();
    }
}

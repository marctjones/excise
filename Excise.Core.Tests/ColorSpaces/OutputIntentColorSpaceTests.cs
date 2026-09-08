using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.ColorSpaces;

/// <summary>
/// Pins <see cref="PdfColorSpace"/>'s OutputIntent resolution (ISO 32000-2
/// §14.11.5): a document-level <c>/OutputIntents</c> ICC profile becomes
/// the rendering intent for bare <c>DeviceCMYK</c> content. Previously
/// exercised only by corpus fixtures CI does not download; these use the
/// same synthetic ICC builders as <see cref="PdfIccProfileTests"/>, so the
/// coverage is environment-independent.
/// </summary>
public class OutputIntentColorSpaceTests
{
    private static PdfDocument WithOutputIntent(byte[]? iccBytes, bool wrapInDict = true)
    {
        var doc = PdfDocument.CreateNew();
        var intents = new PdfArray();
        if (wrapInDict)
        {
            var intent = new PdfDictionary
            {
                ["Type"] = new PdfName("OutputIntent"),
                ["S"] = new PdfName("GTS_PDFA1"),
            };
            if (iccBytes != null)
            {
                var streamDict = new PdfDictionary { ["N"] = new PdfInteger(4) };
                intent["DestOutputProfile"] = doc.AddIndirectObject(new PdfStream(streamDict, iccBytes));
            }
            intents.Add(intent);
        }
        else
        {
            // A malformed entry that is not a dictionary at all.
            intents.Add((PdfObject)new PdfName("NotAnIntent"));
        }
        doc.Catalog["OutputIntents"] = intents;
        return doc;
    }

    [Fact]
    public void FromName_DeviceCmykWithOutputIntentProfile_UsesTheIccProfile()
    {
        var icc = PdfIccProfileTests.BuildLut16CmykProfile();
        using var doc = WithOutputIntent(icc);

        var cs = PdfColorSpace.FromName("DeviceCMYK", doc);
        cs.Type.Should().Be(PdfColorSpaceType.DeviceCMYK);
        cs.Components.Should().Be(4);

        // The ICC LUT16 conversion must actually differ from the naive
        // DeviceCMYK formula — that difference IS the OutputIntent taking
        // effect, not just a profile being carried along.
        var naive = PdfColorSpace.DeviceCMYK.ToRgb([0.2, 0.4, 0.6, 0.1]);
        var profiled = cs.ToRgb([0.2, 0.4, 0.6, 0.1]);
        profiled.Should().NotBe(naive,
            "a document OutputIntent ICC profile must change bare DeviceCMYK conversion");
    }

    [Fact]
    public void FromName_DeviceCmykOutputIntentProfile_IsCachedPerDocument()
    {
        var icc = PdfIccProfileTests.BuildLut16CmykProfile();
        using var doc = WithOutputIntent(icc);

        var first = PdfColorSpace.FromName("DeviceCMYK", doc).ToRgb([0.1, 0.2, 0.3, 0.0]);
        var second = PdfColorSpace.FromName("CMYK", doc).ToRgb([0.1, 0.2, 0.3, 0.0]);
        second.Should().Be(first,
            "the OutputIntent profile is resolved once per document (ConditionalWeakTable) " +
            "and both DeviceCMYK spellings must agree");
    }

    [Fact]
    public void FromName_DeviceCmykOutputIntentProfile_ReturnsTheSameInstanceAcrossCalls()
    {
        // #1425: distinct from the value-equality test above. The renderer's
        // per-pixel DeviceCMYK->RGB conversion cache (Excise.Rendering's
        // ImageColorConverter) is keyed by THIS PdfColorSpace instance's
        // reference identity, not its value -- a fresh wrapper object on
        // every call would silently defeat that cache even though every
        // wrapper carries the same (already document-cached) ICC profile
        // and produces identical ToRgb output. This is called once per
        // RenderContext construction, i.e. once per transparency-group child
        // -- dozens of times on a real page.
        var icc = PdfIccProfileTests.BuildLut16CmykProfile();
        using var doc = WithOutputIntent(icc);

        var first = PdfColorSpace.FromName("DeviceCMYK", doc);
        var second = PdfColorSpace.FromName("CMYK", doc);
        var third = PdfColorSpace.Parse(new PdfName("DeviceCMYK"), doc);

        ReferenceEquals(first, second).Should().BeTrue("both DeviceCMYK spellings must return the same cached instance");
        ReferenceEquals(first, third).Should().BeTrue("Parse must route through the same per-document cache as FromName");
    }

    [Fact]
    public void FromName_DeviceCmykOutputIntentProfile_DifferentDocumentsGetDifferentInstances()
    {
        // The cache must be per-document, not global -- two documents with
        // their own (even identical-content) OutputIntent profiles must not
        // share a PdfColorSpace instance.
        var icc = PdfIccProfileTests.BuildLut16CmykProfile();
        using var docA = WithOutputIntent(icc);
        using var docB = WithOutputIntent(icc);

        var csA = PdfColorSpace.FromName("DeviceCMYK", docA);
        var csB = PdfColorSpace.FromName("DeviceCMYK", docB);

        ReferenceEquals(csA, csB).Should().BeFalse("each document must get its own cached instance");
    }

    [Fact]
    public void FromName_MalformedOutputIntents_FallBackToPlainDeviceCmyk()
    {
        // Entry is not a dictionary.
        using (var doc = WithOutputIntent(null, wrapInDict: false))
        {
            var cs = PdfColorSpace.FromName("DeviceCMYK", doc);
            cs.ToRgb([0, 0, 0, 0]).Should().Be(PdfColorSpace.DeviceCMYK.ToRgb([0, 0, 0, 0]));
        }

        // Intent dictionary without a DestOutputProfile.
        using (var doc = WithOutputIntent(null))
        {
            var cs = PdfColorSpace.FromName("DeviceCMYK", doc);
            cs.ToRgb([0.5, 0.5, 0.5, 0.5]).Should().Be(PdfColorSpace.DeviceCMYK.ToRgb([0.5, 0.5, 0.5, 0.5]));
        }

        // DestOutputProfile that is not a parseable ICC profile.
        using (var doc = WithOutputIntent([1, 2, 3, 4]))
        {
            var cs = PdfColorSpace.FromName("DeviceCMYK", doc);
            cs.ToRgb([1, 0, 0, 0]).Should().Be(PdfColorSpace.DeviceCMYK.ToRgb([1, 0, 0, 0]),
                "an unparseable profile must degrade to the naive conversion, never throw");
        }
    }

    [Fact]
    public void FromName_NoOutputIntents_UsesPlainDeviceCmyk()
    {
        using var doc = PdfDocument.CreateNew();
        var cs = PdfColorSpace.FromName("DeviceCMYK", doc);
        cs.ToRgb([0, 0, 0, 1]).Should().Be(PdfColorSpace.DeviceCMYK.ToRgb([0, 0, 0, 1]));
    }
}

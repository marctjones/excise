using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Preserve-mode evidence for test-pdfs/manifests/pdf-spec-registry/sections/
/// image-requirements.json. <c>PdfDocumentWriter.WriteStream</c> always
/// serializes <c>stream.EncodedData</c> and the stream's own dictionary
/// opaquely (Excise.Core/Writing/PdfDocumentWriter.cs) -- it never re-decodes
/// or re-encodes a stream on save, and it never special-cases a dictionary key
/// by name. That single write-path guarantee is what every "does filter X /
/// key Y survive a save" capability in this section is actually asking about,
/// so rather than assert it once in the abstract these tests exercise it
/// against a spread of real filters, DecodeParms, references, and image-
/// dictionary keys -- a regression in a specific key's handling would show up
/// here even though the underlying mechanism is uniform.
/// </summary>
public class PdfImagePreservationTests
{
    private static PdfDocument SaveAndReopen(PdfDocument doc)
    {
        var bytes = doc.SaveToBytes();
        return PdfDocument.Open(bytes);
    }

    private static PdfReference AddImageObject(PdfDocument doc, PdfDictionary dict, byte[] data)
    {
        dict.SetName("Type", "XObject");
        if (!dict.ContainsKey("Subtype")) dict.SetName("Subtype", "Image");
        var stream = new PdfStream(dict, data);
        return doc.AddIndirectObject(stream);
    }

    private static void PlaceOnNewPage(PdfDocument doc, PdfReference imageRef, string name = "Im0")
    {
        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects[name] = imageRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(System.Text.Encoding.ASCII.GetBytes($"q 2 0 0 3 5 7 cm /{name} Do Q"));
    }

    private static PdfStream ReopenAndGetImage(PdfDocument doc, out PdfDocument reopened, string name = "Im0")
    {
        reopened = SaveAndReopen(doc);
        var image = reopened.GetPage(1).GetXObject(name).Should().BeOfType<PdfStream>().Subject;
        return image;
    }

    /// <summary>
    /// Covers stream-filter:FlateDecode(.predictor-png), image-dictionary:
    /// BitsPerComponent/Decode/Interpolate, image-color:Indexed, and
    /// image-placement:CTM preserve -- one real image combining all of them,
    /// which is stronger evidence than isolated fixtures since it proves they
    /// coexist and all survive together.
    /// </summary>
    [Fact]
    public void FlateIndexedImageWithPngPredictor_SurvivesASaveAndReload_AllKeysAndBytesUnchanged()
    {
        var lookup = new byte[] { 0, 0, 0, 255, 255, 255, 255, 0, 0 };
        var data = new byte[] { 0x78, 0x9C, 0x01, 0x02, 0x03, 0xAB, 0xCD, 0xEF };

        using var doc = PdfDocument.CreateNew();
        var dict = new PdfDictionary();
        dict.SetInt("Width", 4); dict.SetInt("Height", 4);
        dict.SetInt("BitsPerComponent", 4);
        dict["ColorSpace"] = new PdfArray(new PdfObject[] { new PdfName("Indexed"), new PdfName("DeviceRGB"), new PdfInteger(2), new PdfString(System.Text.Encoding.Latin1.GetString(lookup)) });
        dict["Decode"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(15) });
        dict.SetBool("Interpolate", true);
        dict.SetName("Filter", "FlateDecode");
        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 15); parms.SetInt("Colors", 1); parms.SetInt("BitsPerComponent", 4); parms.SetInt("Columns", 4);
        dict["DecodeParms"] = parms;

        var imageRef = AddImageObject(doc, dict, data);
        PlaceOnNewPage(doc, imageRef);

        var image = ReopenAndGetImage(doc, out var reopened);
        using var _ = reopened;

        image.EncodedData.Should().Equal(data, "the writer must not re-decode/re-encode a Flate stream on save");
        image.GetInt("BitsPerComponent").Should().Be(4);
        image.GetArray("Decode").Select(v => ((PdfInteger)v).Value).Should().Equal(0, 15);
        image.GetBool("Interpolate", false).Should().BeTrue();
        image.GetName("Filter").Should().Be("FlateDecode");
        var reopenedParms = image.GetDictionary("DecodeParms");
        reopenedParms.GetInt("Predictor").Should().Be(15);
        reopenedParms.GetInt("Colors").Should().Be(1);
        reopenedParms.GetInt("Columns").Should().Be(4);
        var cs = image.GetArray("ColorSpace");
        cs[0].Should().BeOfType<PdfName>().Which.Value.Should().Be("Indexed");
        cs[1].Should().BeOfType<PdfName>().Which.Value.Should().Be("DeviceRGB");

        var content = reopened.GetPage(1).GetContentStreamBytes();
        System.Text.Encoding.ASCII.GetString(content).Should().Contain("2 0 0 3 5 7 cm",
            "the CTM placing the image must survive unchanged (image-placement:CTM)");
    }

    /// <summary>
    /// Covers stream-filter:ASCII85Decode, LZWDecode(.early-change,.predictors),
    /// stream-filter-arrays, image-color:DeviceGray, and image-dictionary:SMask
    /// preserve -- a filter-array-encoded image referencing a second stream as
    /// its soft mask.
    /// </summary>
    [Fact]
    public void Ascii85LzwFilterArrayImageWithSoftMask_SurvivesASaveAndReload_Unchanged()
    {
        using var doc = PdfDocument.CreateNew();

        var maskDict = new PdfDictionary();
        maskDict.SetInt("Width", 2); maskDict.SetInt("Height", 2);
        maskDict.SetName("ColorSpace", "DeviceGray"); maskDict.SetInt("BitsPerComponent", 8);
        var maskData = new byte[] { 253, 255, 128, 64 };
        var maskRef = AddImageObject(doc, maskDict, maskData);

        var dict = new PdfDictionary();
        dict.SetInt("Width", 2); dict.SetInt("Height", 2);
        dict.SetName("ColorSpace", "DeviceGray"); dict.SetInt("BitsPerComponent", 8);
        dict["Filter"] = new PdfArray(new PdfObject[] { new PdfName("ASCII85Decode"), new PdfName("LZWDecode") });
        var lzwParms = new PdfDictionary();
        lzwParms.SetInt("EarlyChange", 0); lzwParms.SetInt("Predictor", 2);
        lzwParms.SetInt("Colors", 1); lzwParms.SetInt("BitsPerComponent", 8); lzwParms.SetInt("Columns", 2);
        dict["DecodeParms"] = new PdfArray(new PdfObject[] { PdfNull.Instance, lzwParms });
        dict["SMask"] = maskRef;
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var imageRef = AddImageObject(doc, dict, data);
        PlaceOnNewPage(doc, imageRef);

        var image = ReopenAndGetImage(doc, out var reopened);
        using var _ = reopened;

        image.EncodedData.Should().Equal(data);
        image.GetName("ColorSpace").Should().Be("DeviceGray");
        var filters = image.GetArray("Filter");
        filters.Select(f => ((PdfName)f).Value).Should().Equal("ASCII85Decode", "LZWDecode");
        var parmsArray = image.GetArray("DecodeParms");
        parmsArray[0].Should().BeOfType<PdfNull>();
        var reopenedLzwParms = (PdfDictionary)parmsArray[1];
        reopenedLzwParms.GetInt("EarlyChange").Should().Be(0);
        reopenedLzwParms.GetInt("Predictor").Should().Be(2);

        var smask = reopened.Resolve(image.GetReference("SMask")).Should().BeOfType<PdfStream>().Subject;
        smask.EncodedData.Should().Equal(maskData);
    }

    /// <summary>
    /// Covers image-filter:CCITTFaxDecode(.decodeparms), image-dictionary:
    /// Mask.image, and image-dictionary:ImageMask preserve -- a CCITT image
    /// with an explicit stencil-mask reference (not a soft mask).
    /// </summary>
    [Fact]
    public void CcittImageWithExplicitStencilMask_SurvivesASaveAndReload_Unchanged()
    {
        using var doc = PdfDocument.CreateNew();

        var stencilDict = new PdfDictionary();
        stencilDict.SetInt("Width", 8); stencilDict.SetInt("Height", 1);
        stencilDict.SetInt("BitsPerComponent", 1); stencilDict.SetBool("ImageMask", true);
        var stencilData = new byte[] { 0xF0 };
        var stencilRef = AddImageObject(doc, stencilDict, stencilData);

        var dict = new PdfDictionary();
        dict.SetInt("Width", 8); dict.SetInt("Height", 8);
        dict.SetName("ColorSpace", "DeviceGray"); dict.SetInt("BitsPerComponent", 1);
        dict.SetName("Filter", "CCITTFaxDecode");
        var parms = new PdfDictionary();
        parms.SetInt("K", -1); parms.SetInt("Columns", 1728); parms.SetBool("BlackIs1", true);
        dict["DecodeParms"] = parms;
        dict["Mask"] = stencilRef;
        var data = new byte[] { 0x00, 0x11, 0x22, 0x33 };
        var imageRef = AddImageObject(doc, dict, data);
        PlaceOnNewPage(doc, imageRef);

        var image = ReopenAndGetImage(doc, out var reopened);
        using var _ = reopened;

        image.EncodedData.Should().Equal(data);
        image.GetName("Filter").Should().Be("CCITTFaxDecode");
        var reopenedParms = image.GetDictionary("DecodeParms");
        reopenedParms.GetInt("K").Should().Be(-1);
        reopenedParms.GetInt("Columns").Should().Be(1728);
        reopenedParms.GetBool("BlackIs1", false).Should().BeTrue();

        var mask = reopened.Resolve(image.GetReference("Mask")).Should().BeOfType<PdfStream>().Subject;
        mask.EncodedData.Should().Equal(stencilData);
        mask.GetBool("ImageMask", false).Should().BeTrue();
    }

    /// <summary>
    /// Covers image-filter:DCTDecode(.color-transform), image-color:DeviceCMYK,
    /// and image-dictionary:Mask.color-key preserve.
    /// </summary>
    [Fact]
    public void DctCmykImageWithColorKeyMask_SurvivesASaveAndReload_Unchanged()
    {
        using var doc = PdfDocument.CreateNew();
        var dict = new PdfDictionary();
        dict.SetInt("Width", 4); dict.SetInt("Height", 4);
        dict.SetName("ColorSpace", "DeviceCMYK"); dict.SetInt("BitsPerComponent", 8);
        dict.SetName("Filter", "DCTDecode");
        var parms = new PdfDictionary();
        parms.SetInt("ColorTransform", 0);
        dict["DecodeParms"] = parms;
        dict["Mask"] = new PdfArray(new PdfObject[]
        {
            new PdfInteger(0), new PdfInteger(10), new PdfInteger(0), new PdfInteger(10),
            new PdfInteger(0), new PdfInteger(10), new PdfInteger(0), new PdfInteger(10),
        });
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var imageRef = AddImageObject(doc, dict, data);
        PlaceOnNewPage(doc, imageRef);

        var image = ReopenAndGetImage(doc, out var reopened);
        using var _ = reopened;

        image.EncodedData.Should().Equal(data);
        image.GetName("ColorSpace").Should().Be("DeviceCMYK");
        image.GetName("Filter").Should().Be("DCTDecode");
        image.GetDictionary("DecodeParms").GetInt("ColorTransform").Should().Be(0);
        image.GetArray("Mask").Select(v => ((PdfInteger)v).Value).Should().Equal(0, 10, 0, 10, 0, 10, 0, 10);
    }

    /// <summary>
    /// Covers image-filter:JBIG2Decode(.globals) and stream-filter:Crypt
    /// preserve -- a JBIG2 image referencing a /JBIG2Globals stream, and a
    /// separate image whose filter chain includes /Crypt.
    /// </summary>
    [Fact]
    public void Jbig2ImageWithGlobalsReference_AndCryptFilteredImage_SurviveASaveAndReload_Unchanged()
    {
        using var doc = PdfDocument.CreateNew();

        var globalsData = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        var globalsRef = AddImageObject(doc, new PdfDictionary(), globalsData);

        var jbig2Dict = new PdfDictionary();
        jbig2Dict.SetInt("Width", 8); jbig2Dict.SetInt("Height", 8);
        jbig2Dict.SetName("ColorSpace", "DeviceGray"); jbig2Dict.SetInt("BitsPerComponent", 1);
        jbig2Dict.SetName("Filter", "JBIG2Decode");
        var jbig2Parms = new PdfDictionary();
        jbig2Parms["JBIG2Globals"] = globalsRef;
        jbig2Dict["DecodeParms"] = jbig2Parms;
        var jbig2Data = new byte[] { 0xAA, 0xBB, 0xCC };
        var jbig2Ref = AddImageObject(doc, jbig2Dict, jbig2Data);
        PlaceOnNewPage(doc, jbig2Ref, "Im0");

        var cryptDict = new PdfDictionary();
        cryptDict.SetInt("Width", 1); cryptDict.SetInt("Height", 1);
        cryptDict.SetName("ColorSpace", "DeviceGray"); cryptDict.SetInt("BitsPerComponent", 8);
        cryptDict["Filter"] = new PdfArray(new PdfObject[] { new PdfName("Crypt"), new PdfName("FlateDecode") });
        var cryptParms = new PdfDictionary();
        cryptParms.SetName("Name", "Identity");
        cryptDict["DecodeParms"] = new PdfArray(new PdfObject[] { cryptParms, PdfNull.Instance });
        var cryptData = new byte[] { 0x42 };
        var cryptRef = AddImageObject(doc, cryptDict, cryptData);
        var page1 = doc.GetPage(1);
        var xobjects = page1.Resources!.GetDictionary("XObject");
        xobjects["Im1"] = cryptRef;

        var reopened = SaveAndReopen(doc);
        using var _ = reopened;
        var jbig2 = reopened.GetPage(1).GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;
        jbig2.EncodedData.Should().Equal(jbig2Data);
        // The parser eagerly resolves /JBIG2Globals in place for the decoder's
        // convenience (PdfDocumentObjectStore.ResolveJbig2GlobalsReferences),
        // so on reopen the value is the resolved stream itself rather than a
        // PdfReference -- the preserve claim is that the globals DATA is still
        // correctly reachable, not that the reference literal survives.
        var globalsValue = jbig2.GetDictionary("DecodeParms").GetOptional("JBIG2Globals");
        var globals = (globalsValue is PdfReference r ? reopened.Resolve(r) : globalsValue)
            .Should().BeOfType<PdfStream>().Subject;
        globals.EncodedData.Should().Equal(globalsData);

        var crypt = reopened.GetPage(1).GetXObject("Im1").Should().BeOfType<PdfStream>().Subject;
        crypt.EncodedData.Should().Equal(cryptData);
        crypt.GetArray("Filter").Select(f => ((PdfName)f).Value).Should().Equal("Crypt", "FlateDecode");
    }

    /// <summary>
    /// Covers image-filter:JPXDecode and image-color:ICCBased preserve.
    /// </summary>
    [Fact]
    public void JpxImageWithIccBasedColorSpace_SurvivesASaveAndReload_Unchanged()
    {
        using var doc = PdfDocument.CreateNew();
        var iccDict = new PdfDictionary();
        iccDict.SetInt("N", 3);
        var iccData = new byte[] { 1, 2, 3, 4, 5 };
        var iccRef = AddImageObject(doc, iccDict, iccData); // not an Image XObject, but a stream is a stream

        var dict = new PdfDictionary();
        dict.SetInt("Width", 4); dict.SetInt("Height", 4);
        dict["ColorSpace"] = new PdfArray(new PdfObject[] { new PdfName("ICCBased"), iccRef });
        dict.SetInt("BitsPerComponent", 8);
        dict.SetName("Filter", "JPXDecode");
        var data = new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50 };
        var imageRef = AddImageObject(doc, dict, data);
        PlaceOnNewPage(doc, imageRef);

        var image = ReopenAndGetImage(doc, out var reopened);
        using var _ = reopened;

        image.EncodedData.Should().Equal(data);
        image.GetName("Filter").Should().Be("JPXDecode");
        var cs = image.GetArray("ColorSpace");
        cs[0].Should().BeOfType<PdfName>().Which.Value.Should().Be("ICCBased");
        var icc = reopened.Resolve(cs[1]).Should().BeOfType<PdfStream>().Subject;
        icc.EncodedData.Should().Equal(iccData);
        icc.GetInt("N").Should().Be(3);
    }

    /// <summary>
    /// Covers image-color:Lab, image-color:Separation, and image-color:DeviceN
    /// preserve -- three distinct /ColorSpace array shapes on otherwise-minimal
    /// images.
    /// </summary>
    [Fact]
    public void LabSeparationAndDeviceNColorSpaces_SurviveASaveAndReload_Unchanged()
    {
        using var doc = PdfDocument.CreateNew();

        var labDict = new PdfDictionary();
        var labParams = new PdfDictionary();
        labParams["Range"] = new PdfArray(new PdfObject[] { new PdfInteger(-100), new PdfInteger(100), new PdfInteger(-100), new PdfInteger(100) });
        labDict.SetInt("Width", 1); labDict.SetInt("Height", 1); labDict.SetInt("BitsPerComponent", 8);
        labDict["ColorSpace"] = new PdfArray(new PdfObject[] { new PdfName("Lab"), labParams });
        var labRef = AddImageObject(doc, labDict, new byte[] { 1, 2, 3 });

        var sepDict = new PdfDictionary();
        sepDict.SetInt("Width", 1); sepDict.SetInt("Height", 1); sepDict.SetInt("BitsPerComponent", 8);
        sepDict["ColorSpace"] = new PdfArray(new PdfObject[] { new PdfName("Separation"), new PdfName("Spot1"), new PdfName("DeviceCMYK") });
        var sepRef = AddImageObject(doc, sepDict, new byte[] { 4 });

        var devNDict = new PdfDictionary();
        devNDict.SetInt("Width", 1); devNDict.SetInt("Height", 1); devNDict.SetInt("BitsPerComponent", 8);
        devNDict["ColorSpace"] = new PdfArray(new PdfObject[]
        {
            new PdfName("DeviceN"),
            new PdfArray(new PdfObject[] { new PdfName("Cyan"), new PdfName("Magenta") }),
            new PdfName("DeviceCMYK"),
        });
        var devNRef = AddImageObject(doc, devNDict, new byte[] { 5, 6 });

        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Lab"] = labRef; xobjects["Sep"] = sepRef; xobjects["DevN"] = devNRef;
        var resources = new PdfDictionary(); resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(System.Text.Encoding.ASCII.GetBytes("q /Lab Do /Sep Do /DevN Do Q"));

        var reopened = SaveAndReopen(doc);
        using var _ = reopened;
        var reopenedPage = reopened.GetPage(1);

        var lab = reopenedPage.GetXObject("Lab").Should().BeOfType<PdfStream>().Subject;
        lab.GetArray("ColorSpace")[0].Should().BeOfType<PdfName>().Which.Value.Should().Be("Lab");

        var sep = reopenedPage.GetXObject("Sep").Should().BeOfType<PdfStream>().Subject;
        var sepCs = sep.GetArray("ColorSpace");
        sepCs.Select(v => v is PdfName n ? n.Value : null).Should().Equal("Separation", "Spot1", "DeviceCMYK");

        var devN = reopenedPage.GetXObject("DevN").Should().BeOfType<PdfStream>().Subject;
        var devNCs = devN.GetArray("ColorSpace");
        ((PdfName)devNCs[0]).Value.Should().Be("DeviceN");
        ((PdfArray)devNCs[1]).Select(v => ((PdfName)v).Value).Should().Equal("Cyan", "Magenta");
    }

    /// <summary>
    /// Covers stream-filter:ASCIIHexDecode, stream-filter:RunLengthDecode,
    /// stream-filter:FlateDecode.predictor-tiff, and image-color:DeviceRGB
    /// preserve.
    /// </summary>
    [Fact]
    public void AsciiHexRgbImage_RunLengthImage_AndTiffPredictorImage_SurviveASaveAndReload_Unchanged()
    {
        using var doc = PdfDocument.CreateNew();

        var hexDict = new PdfDictionary();
        hexDict.SetInt("Width", 1); hexDict.SetInt("Height", 1);
        hexDict.SetName("ColorSpace", "DeviceRGB"); hexDict.SetInt("BitsPerComponent", 8);
        hexDict.SetName("Filter", "ASCIIHexDecode");
        var hexData = System.Text.Encoding.ASCII.GetBytes("FF0000>");
        var hexRef = AddImageObject(doc, hexDict, hexData);

        var rleDict = new PdfDictionary();
        rleDict.SetInt("Width", 2); rleDict.SetInt("Height", 2);
        rleDict.SetName("ColorSpace", "DeviceGray"); rleDict.SetInt("BitsPerComponent", 8);
        rleDict.SetName("Filter", "RunLengthDecode");
        var rleData = new byte[] { 253, 128 };
        var rleRef = AddImageObject(doc, rleDict, rleData);

        var tiffDict = new PdfDictionary();
        tiffDict.SetInt("Width", 4); tiffDict.SetInt("Height", 4);
        tiffDict.SetName("ColorSpace", "DeviceGray"); tiffDict.SetInt("BitsPerComponent", 8);
        tiffDict.SetName("Filter", "FlateDecode");
        var tiffParms = new PdfDictionary();
        tiffParms.SetInt("Predictor", 2); tiffParms.SetInt("Colors", 1);
        tiffParms.SetInt("BitsPerComponent", 8); tiffParms.SetInt("Columns", 4);
        tiffDict["DecodeParms"] = tiffParms;
        var tiffData = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var tiffRef = AddImageObject(doc, tiffDict, tiffData);

        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Hex"] = hexRef; xobjects["Rle"] = rleRef; xobjects["Tiff"] = tiffRef;
        var resources = new PdfDictionary(); resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(System.Text.Encoding.ASCII.GetBytes("q /Hex Do /Rle Do /Tiff Do Q"));

        var reopened = SaveAndReopen(doc);
        using var _ = reopened;
        var reopenedPage = reopened.GetPage(1);

        var hex = reopenedPage.GetXObject("Hex").Should().BeOfType<PdfStream>().Subject;
        hex.EncodedData.Should().Equal(hexData);
        hex.GetName("Filter").Should().Be("ASCIIHexDecode");
        hex.GetName("ColorSpace").Should().Be("DeviceRGB");

        var rle = reopenedPage.GetXObject("Rle").Should().BeOfType<PdfStream>().Subject;
        rle.EncodedData.Should().Equal(rleData);
        rle.GetName("Filter").Should().Be("RunLengthDecode");

        var tiff = reopenedPage.GetXObject("Tiff").Should().BeOfType<PdfStream>().Subject;
        tiff.EncodedData.Should().Equal(tiffData);
        tiff.GetDictionary("DecodeParms").GetInt("Predictor").Should().Be(2);
    }
}

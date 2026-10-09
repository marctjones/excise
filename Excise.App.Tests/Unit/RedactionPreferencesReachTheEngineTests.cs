using System.Text;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1640 step 2: a redaction preference changes what a redaction does. The checks here run the
/// REAL redaction workflow (<see cref="TermRedactionRunner"/>, the one the GUI and the CLI share)
/// with <see cref="RedactionPreferences.ToOptions"/> and read the saved bytes, so a preference that
/// reached the options but not the output fails. Profile, whole-word, keep-attachments and a
/// carrier policy each get one.
/// </summary>
public sealed class RedactionPreferencesReachTheEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-prefs-engine-{Guid.NewGuid():N}");

    public RedactionPreferencesReachTheEngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Pdf(string pageContent, string catalogExtra = "")
    {
        var objects = new[]
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            $"<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {pageContent.Length} >>\nstream\n{pageContent}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private byte[] Redact(byte[] input, string term, RedactionPreferences preferences)
    {
        var inputPath = Path.Combine(_dir, $"in-{Guid.NewGuid():N}.pdf");
        var outputPath = Path.Combine(_dir, $"out-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(inputPath, input);
        TermRedactionRunner.Execute(new TermRedactionRequest(inputPath, outputPath, term, preferences.ToOptions()));
        return File.ReadAllBytes(outputPath);
    }

    [Fact]
    public void Profile_Maximum_RemovesTheWholePageLabel_Standard_LeavesTheTextAroundTheTerm()
    {
        var input = Pdf(
            "BT /F1 12 Tf 72 700 Td (the visible SECRETNAME line) Tj ET\n",
            catalogExtra: "/PageLabels << /Nums [0 << /S /D /P (ACCT-SECRETNAME-) >>] >>");

        var standard = Redact(input, "SECRETNAME", new RedactionPreferences { Profile = RedactionProfile.Standard });
        var maximum = Redact(input, "SECRETNAME", new RedactionPreferences { Profile = RedactionProfile.Maximum });

        SavedPdfLeakScanner.FindTerm(standard, "SECRETNAME").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(standard, "ACCT").Should().NotBeEmpty("the control: Standard strips the term only");
        SavedPdfLeakScanner.FindTerm(maximum, "SECRETNAME").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(maximum, "ACCT").Should().BeEmpty("Maximum removes the whole label");
    }

    [Fact]
    public void CarrierPolicy_RemoveWhole_ForLinkTargets_DropsTheWholeUri_NotJustTheTerm()
    {
        var withLink = Link();

        var strip = Redact(withLink, "SECRETNAME", new RedactionPreferences { LinkUriPolicy = CarrierScrubMode.Strip });
        var removeWhole = Redact(withLink, "SECRETNAME", new RedactionPreferences { LinkUriPolicy = CarrierScrubMode.RemoveWhole });

        SavedPdfLeakScanner.FindTerm(strip, "SECRETNAME").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(strip, "example.org").Should().NotBeEmpty("the control: Strip keeps the rest of the address");
        SavedPdfLeakScanner.FindTerm(removeWhole, "SECRETNAME").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(removeWhole, "example.org").Should().BeEmpty("RemoveWhole drops the address");
    }

    private static byte[] Link()
    {
        // A page with one Link annotation (object 6) whose address holds the term.
        const string content = "BT /F1 12 Tf 72 700 Td (the visible SECRETNAME line) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 500 300 520] /Border [0 0 0] /A << /S /URI /URI (https://example.org/SECRETNAME/profile) >> >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void KeepAttachments_Off_RemovesTheEmbeddedFile_On_KeepsItWithTheTermCutOut()
    {
        const string attachment = "Notes about SECRETNAME and nothing else";
        const string content = "BT /F1 12 Tf 72 700 Td (the visible SECRETNAME line) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(notes.txt) 6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Filespec /F (notes.txt) /UF (notes.txt) /EF << /F 7 0 R >> >>",
            $"<< /Type /EmbeddedFile /Subtype /text#2Fplain /Length {attachment.Length} >>\nstream\n{attachment}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        var input = Encoding.Latin1.GetBytes(sb.ToString());

        SavedPdfLeakScanner.FindTerm(input, "nothing else").Should().NotBeEmpty("the control: the fixture carries the attachment");
        var removed = Redact(input, "SECRETNAME", new RedactionPreferences { KeepAttachments = false });
        var kept = Redact(input, "SECRETNAME", new RedactionPreferences { KeepAttachments = true });

        SavedPdfLeakScanner.FindTerm(removed, "nothing else").Should().BeEmpty("the default removes every attachment");
        SavedPdfLeakScanner.FindTerm(kept, "SECRETNAME").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(kept, "nothing else").Should().NotBeEmpty(
            "Keep Attachments keeps the file, with only the term cut out of it");
    }

    [Fact]
    public void WholeWord_On_SparesALongerWord_Off_RemovesTheSubstring()
    {
        var input = Pdf("BT /F1 12 Tf 72 700 Td (Lee and Sleeman) Tj ET\n");

        var substring = Redact(input, "Lee", new RedactionPreferences { WholeWord = false });
        var wholeWord = Redact(input, "Lee", new RedactionPreferences { WholeWord = true });

        SavedPdfLeakScanner.FindTerm(substring, "Lee").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(substring, "Sleeman").Should().BeEmpty("the substring rule removes the Lee inside Sleeman too");
        SavedPdfLeakScanner.FindTerm(wholeWord, "Lee").Should().BeEmpty("the standalone word is removed");
        SavedPdfLeakScanner.FindTerm(wholeWord, "Sleeman").Should().NotBeEmpty("whole-word matching spares the longer word");
    }
}

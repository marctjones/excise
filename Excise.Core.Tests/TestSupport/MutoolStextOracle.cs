using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace Excise.TestSupport;

/// <summary>
/// Independent GLYPH POSITIONS from MuPDF's structured text output
/// (<c>mutool draw -F stext</c>).
///
/// <para>Separate from <see cref="MutoolTextOracle"/> because the questions are
/// different: that one answers "is this string in the file", this one answers
/// "and WHERE". A recovery report claims both, and excise must not be the only
/// witness to either (the no-self-oracle rule). A location excise derived from
/// its own glyph model, checked against excise's own glyph model, proves only
/// that the model is self-consistent.</para>
///
/// <para>stext coordinates are TOP-LEFT origin, PDF space is bottom-left, so
/// every box is flipped through the page height on the way out. Getting that
/// backwards silently halves the agreement on a centred box and reverses it on
/// a header, so the conversion happens here, once.</para>
/// </summary>
internal static class MutoolStextOracle
{
    private static readonly string? Executable = FindOnPath("mutool");

    public static bool IsAvailable => Executable is not null;

    /// <summary>A word mutool read, in PDF page space (bottom-left origin).</summary>
    internal readonly record struct Word(
        string Text, double Left, double Bottom, double Right, double Top);

    /// <summary>
    /// Words on <paramref name="pageNumber"/> (1-based), converted to PDF space
    /// using <paramref name="pageHeight"/>.
    /// </summary>
    public static IReadOnlyList<Word> Words(byte[] pdf, int pageNumber, double pageHeight)
    {
        if (Executable is null)
            throw new InvalidOperationException("mutool is not available on PATH");

        var path = Path.Combine(Path.GetTempPath(), $"excise-stext-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var start = new ProcessStartInfo(Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
                     { "draw", "-F", "stext", "-o", "-", path, pageNumber.ToString(CultureInfo.InvariantCulture) })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("mutool did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("mutool stext exceeded 30 seconds");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"mutool stext exited {process.ExitCode}: {stderr}");

            return Parse(stdout, pageHeight);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IReadOnlyList<Word> Parse(string xml, double pageHeight)
    {
        var words = new List<Word>();
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch { return words; }

        // stext nests page > block > line > font/char. Words are assembled from
        // the chars, splitting on whitespace, because MuPDF's own <line> text
        // does not carry per-word boxes.
        foreach (var line in document.Descendants("line"))
        {
            var text = new System.Text.StringBuilder();
            double left = double.MaxValue, top = double.MaxValue;
            double right = double.MinValue, bottom = double.MinValue;

            void Flush()
            {
                if (text.Length == 0) return;
                words.Add(new Word(
                    text.ToString(), left,
                    pageHeight - bottom, right, pageHeight - top));
                text.Clear();
                left = top = double.MaxValue;
                right = bottom = double.MinValue;
            }

            foreach (var ch in line.Descendants("char"))
            {
                var value = (string?)ch.Attribute("c") ?? "";
                if (string.IsNullOrWhiteSpace(value)) { Flush(); continue; }
                if (!TryQuad(ch, out var q)) continue;
                text.Append(value);
                left = Math.Min(left, q.X0);
                top = Math.Min(top, q.Y0);
                right = Math.Max(right, q.X1);
                bottom = Math.Max(bottom, q.Y1);
            }
            Flush();
        }
        return words;
    }

    private static bool TryQuad(XElement ch, out (double X0, double Y0, double X1, double Y1) quad)
    {
        quad = default;
        // MuPDF writes either a bbox="x0 y0 x1 y1" or a quad="ul_x ul_y ur_x
        // ur_y ll_x ll_y lr_x lr_y"; which one depends on the build, so both
        // are read rather than pinning one and skipping everywhere else.
        var bbox = (string?)ch.Attribute("bbox");
        if (bbox != null && TryNumbers(bbox, 4, out var b))
        {
            quad = (b[0], b[1], b[2], b[3]);
            return true;
        }
        var q = (string?)ch.Attribute("quad");
        if (q != null && TryNumbers(q, 8, out var n))
        {
            quad = (Math.Min(n[0], n[4]), Math.Min(n[1], n[3]),
                    Math.Max(n[2], n[6]), Math.Max(n[5], n[7]));
            return true;
        }
        return false;
    }

    private static bool TryNumbers(string text, int expected, out double[] values)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        values = new double[expected];
        if (parts.Length < expected) return false;
        for (var i = 0; i < expected; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        return true;
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Excise.Rendering.Differential;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Structured text from mutool (`draw -F stext`): every character with its
/// page and quad, parsed from mutool's own XML. This is the position oracle
/// for #944 — classifying a destroyed character as boundary-adjacent or
/// remote needs to know WHERE it was, and excise cannot be the one to say,
/// because a broken redaction corrupts excise's own geometry too.
/// </summary>
internal static class MutoolStext
{
    internal readonly record struct Char(int Page, string C, double X0, double Y0, double X1, double Y1);

    internal static List<Char>? ExtractChars(string pdfPath, int timeoutMs = 120_000)
    {
        if (!MutoolReferenceRenderer.IsAvailable) return null;
        var psi = new ProcessStartInfo("mutool")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "draw", "-F", "stext", "-o", "-", pdfPath })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc == null) return null;
        // #925: drain both pipes concurrently.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            return null;
        }
        var xml = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(xml)) return null;
        return Parse(xml);
    }

    /// <summary>
    /// mutool emits the PDF's character values as XML numeric character
    /// references. A BEL (U+0007) is a legal PDF string byte and an illegal
    /// XML 1.0 character — <c>cdc-vis-covid-19.pdf</c> carries one, and
    /// <see cref="XDocument.Parse(string)"/> rejects the whole page. The
    /// oracle has to keep those glyphs or a fixture silently drops out of
    /// the remote/boundary count.
    /// </summary>
    internal static List<Char> Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            CheckCharacters = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader);
        var chars = new List<Char>();
        var pageNo = 0;
        foreach (var page in doc.Descendants("page"))
        {
            pageNo++;
            foreach (var ch in page.Descendants("char"))
            {
                var c = ch.Attribute("c")?.Value;
                var quad = ch.Attribute("quad")?.Value;
                if (c == null || quad == null) continue;
                var q = quad.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (q.Length < 8) continue;
                double P(int i) => double.Parse(q[i], CultureInfo.InvariantCulture);
                var xs = new[] { P(0), P(2), P(4), P(6) };
                var ys = new[] { P(1), P(3), P(5), P(7) };
                chars.Add(new Char(pageNo, c, xs.Min(), ys.Min(), xs.Max(), ys.Max()));
            }
        }
        return chars;
    }
}

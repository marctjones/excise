using System;
using AwesomeAssertions;
using Pdfe.Core.Fonts;
using Pdfe.Core.Tests.Fixtures;
using Xunit;

namespace Pdfe.Core.Tests.Fonts;

/// <summary>
/// Property-based / fuzz coverage for the embedded font-program parsers
/// (#648) — the classic attack surface named in the issue. Same shape as
/// <see cref="Pdfe.Core.Tests.Parsing.ParserFuzzTests"/> (#352): on hostile
/// or malformed bytes the parser must fail gracefully (a typed exception,
/// or — for <see cref="CffParser"/>, whose Parse is documented "malformed
/// inputs return null" and wraps its entire body in a catch-all — simply
/// returning null) and never crash with a raw, unguarded CLR exception or
/// hang. Seeds are fixed for reproducibility.
/// </summary>
public class FontParserFuzzTests
{
    private static bool IsGracefulTrueType(Exception ex) =>
        ex is NotSupportedException; // TrueTypeFontFile.Parse's documented failure mode (missing required table).

    private static void AssertGraceful(Exception ex, byte[] input, string parser, int seed, int iter, Func<Exception, bool> isGraceful)
    {
        if (isGraceful(ex)) return;
        throw new Xunit.Sdk.XunitException(
            $"[{parser}] seed={seed} iter={iter} len={input.Length}: parser threw a raw " +
            $"{ex.GetType().Name} (\"{ex.Message}\") on malformed input instead of a typed/documented " +
            $"failure. This is a missing guard — fix the parser. First bytes: " +
            BitConverter.ToString(input, 0, Math.Min(32, input.Length)) +
            "\nSTACK:\n" + ex.StackTrace);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CffParser_Parse_RandomBytes_NeverThrows(int seed)
    {
        var rng = new Random(seed);
        for (int iter = 0; iter < 400; iter++)
        {
            var bytes = new byte[rng.Next(0, 2000)];
            rng.NextBytes(bytes);
            try
            {
                _ = CffParser.Parse(bytes);
            }
            catch (Exception ex)
            {
                // CffParser.Parse's contract is "malformed inputs return null" —
                // any exception escaping it at all is the bug, not just an
                // "ungraceful" one.
                throw new Xunit.Sdk.XunitException(
                    $"[CffParser] seed={seed} iter={iter} len={bytes.Length}: Parse threw " +
                    $"{ex.GetType().Name} (\"{ex.Message}\") — its documented contract is " +
                    "'malformed inputs return null', never throw. First bytes: " +
                    BitConverter.ToString(bytes, 0, Math.Min(32, bytes.Length)) + "\nSTACK:\n" + ex.StackTrace);
            }
        }
    }

    [Theory]
    [InlineData(11)] [InlineData(22)] [InlineData(33)]
    public void CffParser_Parse_MutatedValidCff_NeverThrows(int seed)
    {
        var valid = LoadInconsolataCff();
        var rng = new Random(seed);
        for (int iter = 0; iter < 400; iter++)
        {
            var bytes = (byte[])valid.Clone();
            int muts = rng.Next(1, 12);
            for (int m = 0; m < muts; m++)
                bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            try
            {
                _ = CffParser.Parse(bytes);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"[CffParser] seed={seed} iter={iter} len={bytes.Length}: Parse threw " +
                    $"{ex.GetType().Name} (\"{ex.Message}\") on a mutated-but-mostly-valid CFF. " +
                    "STACK:\n" + ex.StackTrace);
            }
        }
    }

    [Theory]
    [InlineData(101)] [InlineData(202)] [InlineData(303)]
    public void TrueTypeFontFile_Parse_RandomBytes_FailsGracefullyOrParses(int seed)
    {
        var rng = new Random(seed);
        for (int iter = 0; iter < 400; iter++)
        {
            var bytes = new byte[rng.Next(0, 2000)];
            rng.NextBytes(bytes);
            try
            {
                var ttf = TrueTypeFontFile.Parse(bytes);
                _ = ttf.GlyphCount; // touch the parsed structure
            }
            catch (Exception ex) { AssertGraceful(ex, bytes, "TrueTypeFontFile", seed, iter, IsGracefulTrueType); }
        }
    }

    [Theory]
    [InlineData(111)] [InlineData(222)] [InlineData(333)]
    public void TrueTypeFontFile_Parse_MutatedValidTrueType_FailsGracefullyOrParses(int seed)
    {
        var valid = TestFontFixtures.LoadDejaVuSansBytes();
        var rng = new Random(seed);
        for (int iter = 0; iter < 150; iter++)
        {
            var bytes = (byte[])valid.Clone();
            int muts = rng.Next(1, 12);
            for (int m = 0; m < muts; m++)
                bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            try
            {
                var ttf = TrueTypeFontFile.Parse(bytes);
                _ = ttf.GlyphCount;
                _ = ttf.GidForCodepoint('A');
            }
            catch (Exception ex) { AssertGraceful(ex, bytes, "TrueTypeFontFile", seed, iter, IsGracefulTrueType); }
        }
    }

    [Theory]
    [InlineData(444)] [InlineData(555)] [InlineData(666)]
    public void TrueTypeFontFile_Parse_MutatedValidCffOpenType_FailsGracefullyOrParses(int seed)
    {
        var valid = TestFontFixtures.LoadLibertinusSerifCffBytes();
        var rng = new Random(seed);
        for (int iter = 0; iter < 150; iter++)
        {
            var bytes = (byte[])valid.Clone();
            int muts = rng.Next(1, 12);
            for (int m = 0; m < muts; m++)
                bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            try
            {
                var ttf = TrueTypeFontFile.Parse(bytes);
                _ = ttf.GlyphCount;
            }
            catch (Exception ex) { AssertGraceful(ex, bytes, "TrueTypeFontFile(CFF)", seed, iter, IsGracefulTrueType); }
        }
    }

    /// <summary>
    /// Regression for the specific hang this fuzzer found (#648): a cmap
    /// format-12 group whose declared codepoint range is unbounded — up to
    /// and including <c>endC = 0xFFFFFFFF</c>, where a naive <c>for (uint c
    /// = startC; c &lt;= endC; c++)</c> never terminates at all, since
    /// incrementing a uint past its max wraps back to 0. Found by
    /// <see cref="TrueTypeFontFile_Parse_MutatedValidTrueType_FailsGracefullyOrParses"/>
    /// (seed 333, iteration 119, on a real mutated DejaVu Sans — reproduced
    /// there for ~6 minutes of wall clock with near-zero CPU, consistent
    /// with a runaway Dictionary allocation rather than a hot spin loop,
    /// before being bisected to this root cause and hand-minimized here so
    /// the regression doesn't depend on a specific RNG sequence). Wrapped in
    /// a hard wall-clock budget so a reintroduced regression fails fast
    /// instead of hanging the suite again.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task TrueTypeFontFile_Parse_CmapFormat12UnboundedGroupRange_CompletesQuickly()
    {
        var data = BuildMinimalSfntWithMaliciousCmapFormat12Group(startC: 0, endC: 0xFFFFFFFF);

        var parseTask = System.Threading.Tasks.Task.Run(() => TrueTypeFontFile.Parse(data));
        var winner = await System.Threading.Tasks.Task.WhenAny(parseTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10)));
        winner.Should().Be(parseTask, "a malformed cmap group must not hang the parser — #648's threat model exactly");
    }

    private static byte[] BuildMinimalSfntWithMaliciousCmapFormat12Group(uint startC, uint endC)
    {
        const int numTables = 5;
        const int headOff = 12 + numTables * 16;
        const int headLen = 54;
        const int maxpOff = headOff + headLen;
        const int maxpLen = 6;
        const int hheaOff = maxpOff + maxpLen;
        const int hheaLen = 36;
        const int hmtxOff = hheaOff + hheaLen;
        const int hmtxLen = 4;
        const int cmapOff = hmtxOff + hmtxLen;
        const int cmapSubOff = cmapOff + 12; // header(4) + 1 encoding record(8)
        const int cmapLen = 12 + 8 + 16 + 12; // header + record + subtable header + 1 group

        var data = new byte[cmapOff + cmapLen];
        void PutU32(int o, uint v) { data[o] = (byte)(v >> 24); data[o + 1] = (byte)(v >> 16); data[o + 2] = (byte)(v >> 8); data[o + 3] = (byte)v; }
        void PutU16(int o, ushort v) { data[o] = (byte)(v >> 8); data[o + 1] = (byte)v; }

        PutU32(0, 0x00010000); // sfnt version: TrueType
        PutU16(4, numTables);

        void Table(int entry, string tag, int off, int len)
        {
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(data, entry);
            PutU32(entry + 8, (uint)off);
            PutU32(entry + 12, (uint)len);
        }
        Table(12 + 0 * 16, "head", headOff, headLen);
        Table(12 + 1 * 16, "maxp", maxpOff, maxpLen);
        Table(12 + 2 * 16, "hhea", hheaOff, hheaLen);
        Table(12 + 3 * 16, "hmtx", hmtxOff, hmtxLen);
        Table(12 + 4 * 16, "cmap", cmapOff, cmapLen);

        PutU16(headOff + 18, 1000); // unitsPerEm

        PutU16(maxpOff + 4, 1); // numGlyphs = 1

        PutU16(hheaOff + 34, 1); // numberOfHMetrics = 1

        // hmtx: one advance width entry.
        PutU16(hmtxOff, 500);

        // cmap header: version=0, numTables=1.
        PutU16(cmapOff + 2, 1);
        // Encoding record: platform 3 (Windows), encoding 10 (full Unicode) -> format 12, highest score.
        PutU16(cmapOff + 4, 3);
        PutU16(cmapOff + 6, 10);
        PutU32(cmapOff + 8, (uint)(cmapSubOff - cmapOff));

        // Format 12 subtable: format=12, nGroups=1, one malicious group.
        PutU16(cmapSubOff, 12);
        PutU32(cmapSubOff + 12, 1); // nGroups
        PutU32(cmapSubOff + 16, startC);
        PutU32(cmapSubOff + 20, endC);
        PutU32(cmapSubOff + 24, 0); // startGlyphID

        return data;
    }

    private static byte[] LoadInconsolataCff()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = System.Linq.Enumerable.Single(asm.GetManifestResourceNames(),
            n => n.EndsWith("Inconsolata.cff", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new System.IO.MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

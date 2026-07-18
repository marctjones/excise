using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Excise.Rendering.Differential;

/// <summary>
/// Text-comparison primitives shared by every place in the repo that
/// measures how much excise's own extraction agrees with an independent
/// oracle (mutool, OCR): the release-gate extraction-parity tests and the
/// runtime redaction-confidence checker (#650). One formula, not three
/// copies silently drifting apart.
/// </summary>
public static class TextSimilarity
{
    /// <summary>
    /// <paramref name="exciseText"/>'s letter/digit count as a fraction of
    /// <paramref name="oracleText"/>'s. Unicode-aware (every script counts,
    /// not just ASCII) — whitespace/punctuation is excluded since the two
    /// extractors reflow it differently and it isn't signal. 1.0 when the
    /// oracle found no letters/digits at all (nothing to be blind to).
    /// </summary>
    public static double CoverageRatio(string exciseText, string oracleText)
    {
        var oracleChars = CountLetterOrDigit(oracleText);
        if (oracleChars == 0) return 1.0;
        return (double)CountLetterOrDigit(exciseText) / oracleChars;
    }

    /// <summary>
    /// Bigram Jaccard similarity between the two texts, each normalized via
    /// <see cref="Normalize"/> first (ASCII alnum, lowercased — punctuation
    /// and whitespace reflow differently between extractors and aren't
    /// signal). 1.0 for a pair of near-empty strings (nothing to disagree
    /// about).
    /// </summary>
    public static double BigramJaccard(string exciseText, string oracleText)
    {
        var a = Normalize(exciseText);
        var b = Normalize(oracleText);
        if (a.Length < 2 && b.Length < 2) return 1.0;

        var setA = Bigrams(a);
        var setB = Bigrams(b);
        if (setA.Count == 0 && setB.Count == 0) return 1.0;

        var intersection = new HashSet<string>(setA);
        intersection.IntersectWith(setB);
        var union = new HashSet<string>(setA);
        union.UnionWith(setB);
        return union.Count == 0 ? 1.0 : (double)intersection.Count / union.Count;
    }

    private static int CountLetterOrDigit(string s) => s.Count(char.IsLetterOrDigit);

    /// <summary>ASCII alnum only, lowercased — drops everything else (punctuation, whitespace, non-ASCII).</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                sb.Append(ch);
            else if (ch >= 'A' && ch <= 'Z')
                sb.Append((char)(ch + 32));
        }
        return sb.ToString();
    }

    private static HashSet<string> Bigrams(string s)
    {
        var result = new HashSet<string>();
        for (int i = 0; i + 1 < s.Length; i++)
            result.Add(s.Substring(i, 2));
        return result;
    }
}

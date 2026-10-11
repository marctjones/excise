namespace Excise.Core.Text;

/// <summary>
/// How a separator INSIDE a search or redaction term matches the text. Search,
/// annotation search and term redaction read the same value through
/// <see cref="TermMatch"/>, so what search shows is what redaction removes.
/// </summary>
/// <remarks>
/// <para>A separator is a space or a hyphen (after
/// <see cref="MatchingNormalization.Fold"/>: en dash, em dash and minus read as a
/// hyphen, every whitespace run as one space; U+2010 and U+2011 count as
/// hyphens here too). Only a run of separators BETWEEN two other characters of
/// the term is affected: a term with no separator (<c>opensource</c>) and a
/// leading or trailing hyphen (<c>co-</c>) match literally under every setting.</para>
/// <para><see cref="HyphenAsSpace"/>: the run matches any run of separators in the
/// text, so <c>open source</c> and <c>open-source</c> each find <c>open source</c>,
/// <c>open-source</c>, <c>open–source</c>, <c>open—source</c> and a hyphenated wrap
/// (<c>open-</c> ending one line, <c>source</c> the next).</para>
/// <para><see cref="AllowNoSeparator"/>: the run may also match nothing, so both
/// also find the closed compound <c>opensource</c>. Off by default: it lets
/// <c>pen nest</c> match inside <c>pennest</c>.</para>
/// </remarks>
/// <param name="HyphenAsSpace">A hyphen and a space in the term match each other (default on).</param>
/// <param name="AllowNoSeparator">A separator in the term also matches no separator (default off).</param>
internal readonly record struct SeparatorMatching(bool HyphenAsSpace, bool AllowNoSeparator)
{
    /// <summary>Separators match only themselves: the behaviour before these options.</summary>
    public static SeparatorMatching Exact => default;

    /// <summary>The default everywhere: a hyphen and a space match each other; no closed compounds.</summary>
    public static SeparatorMatching Default => new(HyphenAsSpace: true, AllowNoSeparator: false);

    /// <summary>True when a separator in the term matches only itself.</summary>
    public bool IsExact => !HyphenAsSpace && !AllowNoSeparator;
}

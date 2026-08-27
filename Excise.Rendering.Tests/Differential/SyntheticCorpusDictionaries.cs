using System.Collections.Generic;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1181 — the closed candidate sets the #1134 synthetic corpus draws its answers
/// from, shared so residue recall is always scored against the SAME set the
/// answer came from. Scoring against a different dictionary measures coverage,
/// not width discrimination, and defeats the negative controls. Used by
/// <see cref="ResidueRecoveryRecallTests"/> and the consolidated scorecard's
/// residue driver (<see cref="UnredactionScorecardTests"/>).
/// </summary>
internal static class SyntheticCorpusDictionaries
{
    public static readonly string[] Names =
        ("James John Robert Michael William David Richard Joseph Thomas Charles " +
         "Christopher Daniel Matthew Anthony Donald Mark Paul Steven Andrew Kenneth " +
         "Mary Patricia Jennifer Linda Elizabeth Barbara Susan Jessica Sarah Karen " +
         "Nancy Lisa Betty Margaret Sandra Ashley Kimberly Emily Donna Michelle " +
         "Louise Farrar Anne Dorothy Carol Amanda Melissa Deborah Stephanie").Split(' ');

    public static readonly string[] Dates =
        { "01/15/1987","12/03/1992","07/22/1975","09/30/2001","03/11/1968","11/08/1954","06/19/1983","02/27/1990" };

    public static readonly string[] Digits =
        { "4012884012","5555341220","6011000990","3782822463","8842019375","1029384756","9998887776","4444333322" };

    /// <summary>
    /// The candidate set for a manifest <c>dictionary</c> field. NOT Random for
    /// "random": a proper "answer not in the dictionary" control searches the
    /// realistic attacker dictionary (names), where the random secret is
    /// structurally absent — scoring it against its own set made recovery
    /// trivially guaranteed and defeated the control.
    /// </summary>
    public static IReadOnlyList<string> For(string kind) => kind switch
    {
        "dict" or "dict-long" => Names,
        "date" => Dates,
        "digits" => Digits,
        "random" => Names,
        _ => Names,
    };
}

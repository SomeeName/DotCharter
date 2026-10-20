namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// The six FIXED string identity colours, as a total index → token-key mapping.
///
/// <para>A string is its colour on the highway, in the string gutter, <b>in the
/// tuner</b> and on a chord diagram — so the colour is an IDENTITY, not a
/// palette entry, and it is never reassigned. The values live once, in
/// <c>FireTokens.axaml</c>; this type only says which key belongs to which
/// string, the same shape as <see cref="FireUnitState.TokenKeyFor" /> for gear
/// categories. Handing a colour around instead of a key is what lets two
/// surfaces disagree.</para>
///
/// <para><b>Index 0 is the LOW string</b> — the convention every producer in the
/// app already uses (<c>TuningCheckTarget.StringIndex</c>,
/// <c>TuningStringRow.Index</c>, <c>ChartTheme.BaseStringColors</c>). So a
/// guitar's low E and a bass's low E are index 0 and share one colour, which is
/// what makes the two instruments read the same way.</para>
///
/// <para>Fire declares SIX identities. A seventh string (a 7-string guitar's low
/// B) has no identity here and gets <c>null</c> — the caller falls back to a
/// neutral, so it renders visibly un-identified rather than wearing some other
/// string's colour.</para>
/// </summary>
public static class FireStringIdentity
{
    /// <summary>How many strings carry a fixed identity.</summary>
    public const int Count = 6;

    /// <summary>Token key for a string, or <c>null</c> when the index is outside
    /// the six fixed identities.</summary>
    public static string? TokenKeyFor(int stringIndex) => stringIndex switch
    {
        0 => "FireString6E",
        1 => "FireString5A",
        2 => "FireString4D",
        3 => "FireString3G",
        4 => "FireString2B",
        5 => "FireString1e",
        _ => null
    };

    /// <summary>What an index outside the six resolves to. Deliberately a
    /// TEXT token, not a string colour: it must not look like an identity.</summary>
    public const string NeutralTokenKey = "FireTextFaint";
}

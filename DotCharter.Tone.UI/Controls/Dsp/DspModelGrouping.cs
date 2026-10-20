namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
/// The amp/cab model-family groupings for the model pickers (ABI v24 value
/// labels). PURE + deterministic — an index table over the native gear roster
/// order (index <c>i</c> = param value <c>i</c>), so the amp popup can present the
/// 27 voicings in named groups (American / British / Hi-Gain / Boutique / Bass /
/// DI) and the cab picker can split guitar vs bass. The LABEL strings come from
/// the ABI (never duplicated); only the GROUPING lives here, keyed by index.
/// <c>DspModelGroupingTests</c> pins the whole table (roster size + a few
/// signature members). If native ever reorders the roster the grouping is stale
/// but never crashes (an unmapped index falls back to <see cref="AmpModelGroup.Other"/>).
/// </summary>
public static class DspModelGrouping
{
    /// <summary>The amp voicing model families, in display order.</summary>
    public enum AmpModelGroup
    {
        American,
        British,
        HiGain,
        Boutique,
        Bass,
        Di,
        Other
    }

    /// <summary>Display order for the amp popup's group headers.</summary>
    public static readonly IReadOnlyList<AmpModelGroup> AmpGroupOrder = new[]
    {
        AmpModelGroup.American, AmpModelGroup.British, AmpModelGroup.HiGain,
        AmpModelGroup.Boutique, AmpModelGroup.Bass, AmpModelGroup.Di, AmpModelGroup.Other
    };

    // The v24 amp "voicing" roster (0..26). American Fender-voiced cleans/tweeds,
    // British Marshall/Vox/Orange, modern Hi-Gain, boutique, bass heads, DI/studio.
    private static readonly AmpModelGroup[] AmpVoicing =
    [
        AmpModelGroup.American, // 0  Blackface Twin 85
        AmpModelGroup.British,  // 1  Plexi 50
        AmpModelGroup.British,  // 2  Five-Oh-One Lead
        AmpModelGroup.British,  // 3  AC-Thirty
        AmpModelGroup.HiGain,   // 4  Recto Dual
        AmpModelGroup.American, // 5  Jazz Solid 120 (clean combo)
        AmpModelGroup.American, // 6  Brown Variac (brownface / brown sound)
        AmpModelGroup.American, // 7  Tweed Champ 5W
        AmpModelGroup.American, // 8  Tweed Twin 40
        AmpModelGroup.American, // 9  Blackface Deluxe 22
        AmpModelGroup.British,  // 10 Plexi 100
        AmpModelGroup.British,  // 11 JMP-Eight-Hundred
        AmpModelGroup.British,  // 12 Two-Oh-Three Crunch (Orange)
        AmpModelGroup.British,  // 13 AC-Fifteen
        AmpModelGroup.HiGain,   // 14 Recto Triple
        AmpModelGroup.HiGain,   // 15 Modern Djent 8-String
        AmpModelGroup.HiGain,   // 16 PowerBall Lead
        AmpModelGroup.Boutique, // 17 Overdrive Special (Dumble-style)
        AmpModelGroup.Boutique, // 18 Class-A Boutique 30
        AmpModelGroup.Bass,     // 19 Bass Three-Sixty
        AmpModelGroup.Bass,     // 20 Bass Flip-Top
        AmpModelGroup.Bass,     // 21 SVT Bass 300W
        AmpModelGroup.Di,       // 22 Acoustic Pre
        AmpModelGroup.Di,       // 23 Studio Direct
        // The CIRCUIT voicings, a family row through its
        // documented passive tone stack (dcdsp_amp.h).
        AmpModelGroup.American, // 24 Tweed Bass 45 (5F6-A)
        AmpModelGroup.British,  // 25 Plexi 50 Circuit (Marshall 1987 stack)
        AmpModelGroup.British   // 26 Plexi 100 Circuit
    ];

    /// <summary>The count the index table covers — the roster is expected to be
    /// exactly this many voicings (guards a stale table if native grows it).</summary>
    public const int AmpVoicingCount = 27;

    /// <summary>The model family for an amp voicing index; <see cref="AmpModelGroup.Other"/>
    /// for any index outside the known roster (never throws).</summary>
    public static AmpModelGroup AmpVoicingGroup(int index)
        => index >= 0 && index < AmpVoicing.Length ? AmpVoicing[index] : AmpModelGroup.Other;

    /// <summary>The localization KEY for a group's header.</summary>
    public static string AmpGroupResourceKey(AmpModelGroup group) => group switch
    {
        AmpModelGroup.American => "Pedalboard_AmpGroup_American",
        AmpModelGroup.British => "Pedalboard_AmpGroup_British",
        AmpModelGroup.HiGain => "Pedalboard_AmpGroup_HiGain",
        AmpModelGroup.Boutique => "Pedalboard_AmpGroup_Boutique",
        AmpModelGroup.Bass => "Pedalboard_AmpGroup_Bass",
        AmpModelGroup.Di => "Pedalboard_AmpGroup_Di",
        _ => "Pedalboard_AmpGroup_Other"
    };

    /// <summary>The cab picker's groups.</summary>
    public enum CabModelGroup
    {
        Guitar,
        Bass,
        FullRange
    }

    /// <summary>The native cab roster (`cab.model`, 0..25).</summary>
    public const int CabModelCount = 26;

    /// <summary>
    ///     Which group a cab model is picked from: 9..11 are the original bass boxes
    ///     and 18..25 the bass layouts; 15..17 are the full-range boxes
    ///     (PA 15 + Horn, PA Column, Hi-Fi Tower); every other model — 0..8 and the
    ///     10- and 15-inch guitar cabs 12..14 — is a guitar cab.
    /// </summary>
    public static CabModelGroup CabGroup(int index) => index switch
    {
        >= 9 and <= 11 => CabModelGroup.Bass,
        >= 15 and <= 17 => CabModelGroup.FullRange,
        >= 18 => CabModelGroup.Bass,
        _ => CabModelGroup.Guitar
    };

    public static bool IsBassCab(int index) => CabGroup(index) == CabModelGroup.Bass;

    public static string CabGroupResourceKey(CabModelGroup group) => group switch
    {
        CabModelGroup.Bass => "Pedalboard_CabGroup_Bass",
        CabModelGroup.FullRange => "Pedalboard_CabGroup_FullRange",
        _ => "Pedalboard_CabGroup_Guitar"
    };
}

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     How a 7-string guitar's chord that reaches the 7th string is written to a
/// Rocksmith package. The SNG's chord records have six string
///     slots; the two modes are the ones the <c>rs7chart</c> generator offers, and BOTH
///     write the RS7X block (an arrangement with more than six strings always has one).
/// </summary>
public enum Rs7ChordMode
{
    /// <summary>The chord stays one chord: its six-string columns are the SNG chord
    /// template, the 7th string's column and chord-note techniques go into the SNG's
    /// RS7X block, which the <c>rs7string</c> mod reads (the default).</summary>
    Projection,

    /// <summary>The six-string columns stay an SNG chord (a single note when only one is
    /// left) and every extra string becomes a simultaneous single note — playable with
    /// an <c>rs7string</c> build from before the block, without a chord frame or name on
    /// those strings.</summary>
    Split
}

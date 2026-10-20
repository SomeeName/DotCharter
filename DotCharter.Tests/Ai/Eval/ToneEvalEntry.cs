using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     One ground-truth row of the tone eval: a real Rocksmith tone, the song it
///     belongs to, and the <see cref="DcRigPreset" /> DotCharter's own
///     <see cref="RsToneConverter" /> builds from it.
///     <para>
///         The truth is the CONVERTER's output, not the RS tone — that is the
///         thing the app actually plays, so it is the only chain a designed tone
///         can be compared against on equal terms (same catalogue, same param
///         ranges, same module ids).
///     </para>
/// </summary>
/// <param name="SongTitle">Display title, as the package carries it.</param>
/// <param name="Artist">Display artist.</param>
/// <param name="ToneName">The tone's in-game name ("Lead", "Overdrive", "Clean2").</param>
/// <param name="ToneKey">The tone's key; falls back to the name when absent.</param>
/// <param name="ToneJson">The verbatim RS tone JSON body.</param>
/// <param name="Truth">The converted rig — the ground truth the scorer grades against.</param>
/// <param name="Source">Where the row came from, for the report ("rs2dlc", "psarc", "fixture").</param>
public sealed record ToneEvalEntry(
    string SongTitle,
    string Artist,
    string ToneName,
    string ToneKey,
    string ToneJson,
    DcRigPreset Truth,
    string Source)
{
    /// <summary>A stable per-song identity, used to de-duplicate the corpus.</summary>
    public string SongId => MakeSongId(Artist, SongTitle);

    /// <summary>
    ///     The one definition of a song's identity, so a consumer that holds a
    ///     package's artist and title — but no <see cref="ToneEvalEntry" /> — resolves
    ///     it to the same string this corpus keys on.
    ///     <para>
    ///         <b>It exists because the audio eval used to resolve a row to its audio
    ///         folder by the row's TONE JSON instead.</b> A tone definition reused
    ///         across songs — the normal case for one author — made every such row
    ///         resolve to one folder and score one song's audio N times, which
    ///         pseudo-replicated every aggregate that lane published. Title + artist is
    ///         the identity; a tone body is not.
    ///     </para>
    /// </summary>
    /// <param name="artist">Display artist, as the package carries it.</param>
    /// <param name="title">Display title, as the package carries it.</param>
    /// <returns>A lowercase <c>artist|title</c> key, both halves trimmed.</returns>
    public static string MakeSongId(string artist, string title) =>
        ((artist ?? string.Empty).Trim() + "|" + (title ?? string.Empty).Trim()).ToLowerInvariant();

    /// <summary>A stable per-row identity, used as the results.json key.</summary>
    public string Id => SongId + "|" + ToneName.ToLowerInvariant();

    /// <summary>
    ///     The FULL request the eval sends: everything a human would type, tone
    ///     name and song included.
    /// </summary>
    public string Prompt => $"{ToneName} tone for \"{SongTitle}\" by {Artist}";

    /// <summary>
    ///     The SPARSE request: the song and a coarse role only. It exists to
    ///     measure how much of a score comes from the model recognizing the song
    ///     versus from the tone name doing the work — a model that scores the same
    ///     on both has learned nothing from "Overdrive".
    /// </summary>
    public string ShortPrompt => $"{SongTitle} {Role} tone";

    /// <summary>
    ///     The coarse role folded out of the tone name: <c>bass</c>, <c>clean</c>,
    ///     <c>rhythm</c> or <c>lead</c>. Ordered so the most specific token wins —
    ///     "CleanLead" is a clean tone, "Octave_Solo" is a lead.
    /// </summary>
    public string Role
    {
        get
        {
            var n = ToneName.ToLowerInvariant();
            if (n.Contains("bass")) return "bass";
            if (n.Contains("clean")) return "clean";
            if (n.Contains("lead") || n.Contains("solo")) return "lead";
            if (n.Contains("rhythm") || n.Contains("rhyhtm") ||
                n.Contains("overdrive") || n.Contains("distortion") ||
                n.Contains("crunch") || n.Contains("od"))
            {
                return "rhythm";
            }

            return "lead";
        }
    }
}

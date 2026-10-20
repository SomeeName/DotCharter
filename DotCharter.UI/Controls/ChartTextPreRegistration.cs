using DotCharter.Rendering.Text;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.UI.Controls;

/// <summary>
///     Registers every character the 2D timeline can draw for a chart into its
///     glyph atlas in ONE pass — the 2D twin of
///     <c>Fretboard3DEngine.PreRegisterChartText</c>.
///     <para>
///         <b>Why.</b> An atlas rebuild is not cheap: a 2048² rasterize of every
///         registered glyph, then <c>DeviceWaitIdle</c>, a fresh 16 MB device
///         image, a 16 MB staging copy and a <c>QueueWaitIdle</c> — on the UI
///         thread, inside <c>BeginFrame</c>. The seed set has no apostrophe, no
///         <c>!</c>, no accented or CJK characters, so a timeline that
///         registered only the strings inside the current WINDOW would make the first
///         <c>don't</c> or <c>Gtr_Lead!</c> to scroll into view during playback
///         pay that rebuild mid-frame. Registering the whole chart up front moves
///         the one rebuild to the first frame after a load or a text edit — where
///         the transport is parked and a load is already in progress — and the
///         per-frame windowed scan then finds nothing new.
///     </para>
///     <para>
///         Mirrors what the layers draw: lyric words, phrase and section names,
///         the UPPERCASED section (EventLayer draws sections through
///         <see cref="RenderStringCache.Upper" />, so Ä/É-style uppercase glyphs
///         are distinct characters) and tone badge names. Pinned by
///         <c>ChartTextPreRegistrationTests</c>: after this pass, registering any
///         windowed string of the same chart adds no character.
///     </para>
/// </summary>
internal static class ChartTextPreRegistration
{
    public static void Register(ChartFontAtlas atlas,
        IList<LyricPhrase> lyricPhrases, IList<EventPoint> eventPoints, IList<ToneChange> tones)
    {
        for (var i = 0; i < lyricPhrases.Count; i++)
        {
            var lyrics = lyricPhrases[i].Lyrics;
            for (var j = 0; j < lyrics.Count; j++) atlas.RegisterCharacters(lyrics[j].Text);
        }

        for (var i = 0; i < eventPoints.Count; i++)
        {
            var ep = eventPoints[i];
            atlas.RegisterCharacters(ep.Phrase);
            atlas.RegisterCharacters(ep.Section);
            atlas.RegisterCharacters(RenderStringCache.Upper(ep.Section));
        }

        for (var i = 0; i < tones.Count; i++)
            atlas.RegisterCharacters(tones[i].ToneName);
    }
}

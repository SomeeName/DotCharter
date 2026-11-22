using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

/// <summary>
///     Plans a change of <see cref="LyricEvent.IsWordPart" />. A word part continues into the next
///     syllable, so joining one that ends its line also joins the next line in the same undo step
///     (<see cref="SetLyricPhraseEndAction" />): a syllable carrying both flags would be one line in
///     the editor and lose its line break in a Rocksmith file. With no next line to join, the line
///     end stays and the word part is dropped (<see cref="LyricLineRule" />).
/// </summary>
public static class LyricWordPartEdit
{
    /// <summary>The one action for the edit; on the track's last line the phrase end stays and the word part goes.</summary>
    public static IChartAction Create(IEnumerable<ObservableCollection<LyricPhrase>> trackPhrases, LyricEvent lyric,
        bool isWordPart) => Create(trackPhrases, lyric, lyric.Text, isWordPart);

    /// <summary>The same, with a new text for the syllable (the inline lyric editor).</summary>
    public static IChartAction Create(IEnumerable<ObservableCollection<LyricPhrase>> trackPhrases, LyricEvent lyric,
        string text, bool isWordPart)
    {
        var join = isWordPart && !lyric.IsWordPart && lyric.IsPhraseEnd ? JoinNextLine(trackPhrases, lyric) : null;
        var isPhraseEnd = join == null && lyric.IsPhraseEnd;
        var rename = new RenameLyricAction(lyric, lyric.Text, text, lyric.IsWordPart,
            LyricLineRule.WordPart(isWordPart, isPhraseEnd), lyric.IsPhraseEnd, isPhraseEnd);
        return join == null ? rename : new CompositeAction([join, rename]) { LabelKey = "History_RenameLyric" };
    }

    private static SetLyricPhraseEndAction? JoinNextLine(IEnumerable<ObservableCollection<LyricPhrase>> trackPhrases,
        LyricEvent lyric)
    {
        foreach (var phrases in trackPhrases)
        {
            var phrase = phrases.FirstOrDefault(p => p.Lyrics.Contains(lyric));
            if (phrase == null) continue;
            var join = new SetLyricPhraseEndAction(phrases, phrase, lyric, false);
            return join.HasEffect ? join : null;
        }

        return null;
    }
}

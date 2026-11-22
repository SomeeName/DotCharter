using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

/// <summary>
///     Joins the line that <paramref name="lyric" /> ends to the next line of the track
///     (<see cref="SetLyricPhraseEndAction" />), resolved when it first runs: inside a
///     <see cref="CompositeAction" /> the syllable or the line it joins is only added by an earlier
///     child. Redo and undo reuse that first join. The track must have a next line when it runs.
/// </summary>
public sealed class JoinNextLyricLineAction(ObservableCollection<LyricPhrase> trackPhrases, LyricEvent lyric)
    : IChartAction, ILinkPolicyAction
{
    private SetLyricPhraseEndAction? _join;

    /// <summary>Lyrics are outside the linked measure content.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public void Execute()
    {
        if (_join == null)
        {
            var phrase = trackPhrases.First(p => p.Lyrics.Contains(lyric));
            _join = new SetLyricPhraseEndAction(trackPhrases, phrase, lyric, false);
        }

        _join.Execute();
    }

    public void Undo() => _join?.Undo();
}

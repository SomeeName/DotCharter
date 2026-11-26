namespace DotCharter.Services.State;

/// <summary>
/// Rewrites one syllable's text, the lyric findings' text fix (<c>LyricStyleFix</c>). Undo restores
/// the text exactly as it was; nothing else about the syllable changes.
/// </summary>
public sealed class SetLyricTextAction : IChartAction, ILinkPolicyAction
{
    private readonly LyricEvent _lyric;
    private readonly string _oldText;
    private readonly string _newText;

    public SetLyricTextAction(LyricEvent lyric, string newText)
    {
        _lyric = lyric;
        _oldText = lyric.Text;
        _newText = newText;
    }

    /// <summary>Lyrics are outside the linked measure content.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public bool HasEffect => !string.Equals(_oldText, _newText, StringComparison.Ordinal);

    public void Execute() => _lyric.Text = _newText;

    public void Undo() => _lyric.Text = _oldText;
}

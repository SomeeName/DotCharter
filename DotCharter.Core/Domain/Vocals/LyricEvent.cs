using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Vocals;

public class LyricEvent
{
    public FractionalPosition EndPosition { get; set; } = FractionalPosition.Zero;
    public bool IsWordPart { get; set; }

    /// <summary>
    /// Marks this lyric as the last syllable of its phrase. Maps to the
    /// RsCharter <c>PHRASE_END</c> vocal flag. The Rscp writer will emit
    /// <c>PHRASE_END</c> for any lyric where this is true OR where the lyric
    /// is the last entry of its <see cref="LyricPhrase"/> for backwards
    /// compatibility.
    /// </summary>
    public bool IsPhraseEnd { get; set; }
    public int Note { get; set; } = 255;
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;
    public string Text { get; set; } = "";
}

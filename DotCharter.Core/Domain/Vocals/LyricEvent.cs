using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Vocals;

public class LyricEvent
{
    /// <summary>The auto-sync model's score; null for a syllable authored without alignment.</summary>
    public double? AlignmentConfidence { get; set; }

    /// <summary>Advisory alignment findings, retained until the chart is reviewed.</summary>
    public LyricReviewReason ReviewReasons { get; set; }

    public FractionalPosition EndPosition { get; set; } = FractionalPosition.Zero;
    /// <summary>The wheel deliberately shortened this sustain below the in-phrase end limit.</summary>
    public bool IsSustainShortened { get; set; }
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

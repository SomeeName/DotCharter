using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Project;

/// <summary>
/// A single occurrence of a phrase at a position in the song. Mirrors
/// rscharter's <c>ArrangementPhraseIteration</c> (RS-XML
/// <c>phraseIteration</c> element).
/// </summary>
public class PhraseIteration
{
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;

    /// <summary>Index into <see cref="Arrangement.Phrases"/>.</summary>
    public int PhraseId { get; set; }

    public PhraseIteration() { }

    public PhraseIteration(FractionalPosition position, int phraseId)
    {
        Position = position;
        PhraseId = phraseId;
    }
}

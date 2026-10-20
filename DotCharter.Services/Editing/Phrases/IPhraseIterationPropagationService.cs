namespace DotCharter.Services.Editing.Phrases;

public interface IPhraseIterationPropagationService
{
    /// <summary>
    /// Replicates the sound content (notes, chords, anchors, hand shapes)
    /// of <paramref name="source"/>'s span into the span of every other
    /// iteration of the same phrase, replacing the targets' existing content.
    /// Routed through the history service as a single undoable action.
    /// Returns the number of target iterations that were rewritten (0 when
    /// the phrase has no other iterations or nothing changed).
    /// See <see cref="PhraseIterationPropagationService"/> for the exact
    /// span/clipping rules.
    /// </summary>
    int PropagateToAllIterations(PhraseIteration source);
}

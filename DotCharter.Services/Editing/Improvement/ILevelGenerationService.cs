namespace DotCharter.Services.Editing.Improvement;

public interface ILevelGenerationService
{
    /// <summary>
    /// Replaces the arrangement's levels with <paramref name="levelCount"/>
    /// generated difficulty levels derived from its hardest existing level.
    /// See <see cref="LevelGenerationService"/> for the exact rules.
    /// Returns false (and leaves the arrangement untouched) when
    /// <paramref name="levelCount"/> &lt; 2 or the arrangement has no levels.
    /// </summary>
    /// <param name="arrangement">The arrangement to level.</param>
    /// <param name="levelCount">How many levels to generate (top level = the source chart).</param>
    /// <param name="beats">Optional beat grid for detecting measure downbeats;
    /// without it only on-beat/off-beat importance is distinguished.</param>
    /// <param name="levelProgress">Optional per-level report — the number of
    /// levels BUILT so far, raised after each one. Twenty levels over a dense
    /// chart is the slow case, and it is the only part of level generation
    /// whose cost scales with the request, so it is the only part worth
    /// reporting. Called on the calling thread; nothing is reported when the
    /// method refuses the request.</param>
    bool TryGenerate(Arrangement arrangement, int levelCount, IList<BeatMarker>? beats = null,
        IProgress<int>? levelProgress = null);
}

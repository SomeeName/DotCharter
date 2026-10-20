using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     Where a keyboard technique verb's chord half goes when the "Apply to" ladder reaches past
/// the selection: the rung's <paramref name="Chords" />, on the arrangement's
///     <paramref name="Levels" />, each level with its own spacing pass. Written through
///     <see cref="ChordTechniqueEdit" />, the inspector's own builder.
/// </summary>
internal sealed record ChordLadderReach(
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<ChartLevel> Levels,
    Func<ChartLevel, Action?> LevelPostPass);

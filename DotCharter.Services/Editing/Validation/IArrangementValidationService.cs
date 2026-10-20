namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     Public surface for the arrangement validation engine. The service is
///     responsible for inspecting an <see cref="Arrangement" /> and surfacing
///     issues that would either prevent it from working in-game or hint at
///     authoring mistakes (mirrors DLC Builder / Rocksmith2014.NET checkers).
///     The implementation is deliberately allocation-conscious — validation
///     can be triggered repeatedly (after every history step) on large levels.
/// </summary>
public interface IArrangementValidationService
{
    /// <summary>
    ///     Returns the list of issues detected for the given arrangement. The
    ///     list is sorted by time (general/non-positional issues first).
    /// </summary>
    /// <param name="arrangement">Arrangement to inspect; may be null (returns empty).</param>
    /// <param name="beats">Project beats — required to compute issue times in milliseconds.</param>
    /// <param name="totalAudioLengthMs">Optional song length for flagging notes after the song ends.</param>
    /// <param name="project">
    ///     Optional owning project, for rules that need a PROJECT-level fact the
    ///     arrangement cannot carry — the authored tone bodies
    ///     (<see cref="ChartProject.ToneDefinitions" />) above all. Null means
    ///     UNKNOWN, not empty: omitting it yields no project-level findings rather
    ///     than reporting every tone in the chart as missing.
    /// </param>
    IReadOnlyList<ArrangementIssue> Validate(
        Arrangement? arrangement,
        IList<BeatMarker> beats,
        double? totalAudioLengthMs = null,
        ChartProject? project = null);
}

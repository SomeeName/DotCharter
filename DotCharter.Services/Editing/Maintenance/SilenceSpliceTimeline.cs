namespace DotCharter.Services.Editing.Maintenance;

/// <summary>
///     The fields of a project that are absolute milliseconds into the song, and so move when silence
/// is spliced in front of it. Everything else is placed by beat and follows the beats.
///     <c>SilenceSpliceTimelineTests</c> fails when the domain gains a time field this does not name.
/// </summary>
public static class SilenceSpliceTimeline
{
    /// <summary>Moves the project <paramref name="leadingMs" /> later, the way the music moved.</summary>
    public static void ShiftForLeadingSilence(ChartProject project, double leadingMs)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (leadingMs <= 0) return;
        foreach (var beat in project.Beats) beat.Time += leadingMs;
        foreach (var bookmark in project.Bookmarks) bookmark.Time += leadingMs;
        foreach (var annotation in project.Annotations) annotation.TimeMs += leadingMs;
        project.Chart.Time += leadingMs;
        // The silence added to the front of the main audio, which is what a re-sync subtracts.
        project.Chart.MainAudioOffsetAdded += leadingMs;
    }
}

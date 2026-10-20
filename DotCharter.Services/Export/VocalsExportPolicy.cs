using DotCharter.Services.Export.DlcBuilder;

namespace DotCharter.Services.Export;

/// <summary>
///     The ONE decision behind "does this export carry vocals".
/// </summary>
/// <remarks>
///     <para>
///         <c>AppConfig.ExportIncludeVocals</c> is read by every writer, not only the psarc/SNG path (through
///         <c>RocksmithBuildOptions.IncludeVocals</c>). The companion artifacts
///         a save also produces (the <c>.rs2dlc</c>, the DTRS XML bundle and the
///         <c>.eof</c>) would each rebuild the "which vocal tracks count" test for
///         themselves without consulting the flag, so a charter who
///         turned vocals OFF would still get a lyric-bearing bundle and a
///         <c>.rs2dlc</c> naming Vocals arrangements.
///     </para>
///     <para>
///         Every writer now asks this type, so the flag has one meaning across
///         the whole save. It also decides how many Vocals arrangements a
///         PACKAGE carries: one, the first lyric-bearing track
///         (<see cref="PackageTrack" />), because Rocksmith plays a single Vocals
///         arrangement. The psarc and the <c>.eof</c> always took the first; the
/// <c>.rs2dlc</c> named every lyric-bearing track. The DTRS XML
///         bundle still writes a file per track: it is the charter's source
///         folder, not a package.
///     </para>
/// </remarks>
public static class VocalsExportPolicy
{
    /// <summary>Whether this configuration wants vocals in exported artifacts.
    /// A null config (no settings yet) keeps <c>AppConfig</c>'s own default.</summary>
    public static bool IncludeVocals(AppConfig? config) => config?.ExportIncludeVocals ?? true;

    /// <summary>A track carries lyrics — the pre-existing per-writer filter,
    /// named once so the two halves of the test never drift apart.</summary>
    public static bool HasLyrics(VocalTrack track) => DlcBuilderBundle.HasLyrics(track);

    /// <summary>The complete test a writer applies to one track: the flag AND
    /// the track actually having something to write.</summary>
    public static bool ShouldWrite(VocalTrack track, bool includeVocals) =>
        includeVocals && HasLyrics(track);

    /// <summary>The ONE vocal track a package carries (the psarc, the <c>.rs2dlc</c>,
    /// the <c>.eof</c>), with its index in <c>ChartProject.VocalTracks</c> (the bundle
    /// names its file by it); null when there is none to write.</summary>
    public static (VocalTrack Track, int Index)? PackageTrack(ChartProject project, bool includeVocals)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!includeVocals) return null;
        for (var i = 0; i < project.VocalTracks.Count; i++)
            if (HasLyrics(project.VocalTracks[i])) return (project.VocalTracks[i], i);
        return null;
    }

    /// <summary>The tracks an export should write, in project order.</summary>
    public static IReadOnlyList<VocalTrack> TracksToWrite(
        ChartProject project, bool includeVocals)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!includeVocals) return [];
        return [.. project.VocalTracks.Where(HasLyrics)];
    }
}

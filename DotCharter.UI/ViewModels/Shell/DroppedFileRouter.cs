// The pure extension -> subsystem table behind ProjectMenuViewModel.LoadFile
// (File ▸ Open Recent and the window's file DROP both funnel through it).
using DotCharter.Services.Import.Audio;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     Which subsystem owns a file handed to <see cref="ProjectMenuViewModel.LoadFile" />.
/// </summary>
public enum DroppedFileRoute
{
    /// <summary>An <c>.rscp</c> project — the project loader REPLACES the session.</summary>
    Project,

    /// <summary>Song audio (<see cref="AudioFileTypes" />) — attached or auditioned.</summary>
    Audio,

    /// <summary>Editor on Fire project — the EOF import dialog.</summary>
    EofImport,

    /// <summary>Guitar Pro tab (<c>.gp</c>/<c>.gpx</c>/<c>.gp3</c>-<c>.gp7</c>/<c>.gpa</c>).</summary>
    GuitarProImport,

    /// <summary>Standard MIDI file — the MIDI import dialog.</summary>
    MidiImport,

    /// <summary>
    ///     Rocksmith XML (song / vocals / showlights). MERGES into the open project;
    ///     with NO project open there is nothing to merge into, so the caller falls
    ///     back to the project loader, which reads an RS-XML song as a new project.
    /// </summary>
    RocksmithXmlImport,

    /// <summary>
    ///     A Rocksmith <c>.psarc</c> package. There is no single-file load seam —
    ///     <c>SongBrowserViewModel.LoadSongItem</c> takes a scanned entry, not a path
    ///     — so this route only says so instead of failing as a corrupt project.
    /// </summary>
    RocksmithPackage,

    /// <summary>
    ///     Nothing in the app reads this extension. Today it reached
    ///     <c>ProjectDocumentService.ResolveFormat</c>, threw
    ///     <see cref="NotSupportedException" /> and surfaced as "Error loading
    ///     project" — a corrupt-file message for a file that was never a project.
    /// </summary>
    Unsupported
}

/// <summary>
///     The single source of truth for "what does dropping / opening THIS file do?".
///     <para>
///     Pure (extension only, no I/O, never throws) so the whole table is unit
///     testable; <see cref="ProjectMenuViewModel.LoadFile" /> is the only consumer
///     and may not re-decide any extension itself — pinned by
///     <c>DotCharter.Tests/UI/DroppedFileRoutingTests</c>.
///     </para>
///     <para>
///     Before this existed only <c>.eof</c> and the audio set were routed, so a
///     dropped <c>.gp5</c>/<c>.mid</c>/<c>.psarc</c> fell into the PROJECT loader
///     and reported "Error loading project" — the same class of bug the audio list
///     fixed one release earlier (see <see cref="AudioFileTypes" />).
///     </para>
/// </summary>
public static class DroppedFileRouter
{
    /// <summary>The atomic-save temp suffix (<c>project.rscp.saving</c>) —
    /// <c>ProjectDocumentService</c> loads through it, so the router must too or a
    /// leftover temp becomes "unsupported".</summary>
    private const string AtomicTempSuffix = ".saving";

    /// <summary>Lower-case, dot-prefixed. Mirrors <c>FilePickerFilters.GuitarPro</c>.</summary>
    public static IReadOnlyList<string> GuitarProExtensions { get; } =
        new[] { ".gp", ".gpx", ".gp7", ".gpa", ".gp5", ".gp4", ".gp3" };

    /// <summary>Lower-case, dot-prefixed.</summary>
    public static IReadOnlyList<string> MidiExtensions { get; } = new[] { ".mid", ".midi" };

    /// <summary>Every extension this table names, in route order. Test surface.</summary>
    public static IReadOnlyList<string> KnownExtensions { get; } =
        new[] { ".rscp", ".dcsong", ".eof", ".xml", ".psarc" }
            .Concat(MidiExtensions)
            .Concat(GuitarProExtensions)
            .Concat(AudioFileTypes.Extensions)
            .ToArray();

    /// <summary>
    ///     Extension → owning subsystem. Case-insensitive; a null/blank/extension-less
    ///     path is <see cref="DroppedFileRoute.Unsupported" />. A <c>.psarc</c> has its
    /// own route only when <paramref name="readsGamePackages" />; otherwise it
    ///     is just another unsupported type.
    /// </summary>
    public static DroppedFileRoute Route(string? path, bool readsGamePackages)
    {
        var extension = ExtensionOf(path);
        if (extension.Length == 0) return DroppedFileRoute.Unsupported;

        // Audio first: the engine's decode list is owned by AudioFileTypes and must
        // not be duplicated here.
        if (AudioFileTypes.Extensions.Contains(extension)) return DroppedFileRoute.Audio;

        if (GuitarProExtensions.Contains(extension)) return DroppedFileRoute.GuitarProImport;
        if (MidiExtensions.Contains(extension)) return DroppedFileRoute.MidiImport;

        return extension switch
        {
            ".rscp" => DroppedFileRoute.Project,
            // A song package unpacks into a new project folder (ProjectSessionService.LoadFile).
            ".dcsong" => DroppedFileRoute.Project,
            ".eof" => DroppedFileRoute.EofImport,
            ".xml" => DroppedFileRoute.RocksmithXmlImport,
            ".psarc" when readsGamePackages => DroppedFileRoute.RocksmithPackage,
            _ => DroppedFileRoute.Unsupported
        };
    }

    /// <summary>
    ///     The lower-cased, dot-prefixed extension used for routing and for the
    ///     "&lt;type&gt; files aren't supported" message — <c>""</c> when there is none.
    ///     Strips the atomic-save temp suffix. Never throws.
    /// </summary>
    public static string ExtensionOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        try
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(AtomicTempSuffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^AtomicTempSuffix.Length];

            return Path.GetExtension(name).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Invalid path characters — not something any route could open.
            return string.Empty;
        }
    }
}

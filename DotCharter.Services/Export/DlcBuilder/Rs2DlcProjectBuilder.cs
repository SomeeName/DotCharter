using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>
///     Builds the DLC Builder project (<c>.rs2dlc</c>) that ties the RS-XML bundle,
///     the audio and the album art together.
/// </summary>
/// <remarks>
///     PURE — it takes what it needs and returns a DTO; <see cref="Rs2DlcWriter" />
///     does the I/O. Paths are emitted RELATIVE to the folder the <c>.rs2dlc</c>
///     will live in, which is what upstream's <c>DLCProject.toRelativePaths</c> does
///     on save and <c>toAbsolutePaths</c> undoes on load.
///     <para>
///         The project file sits in the PROJECT ROOT, beside the <c>.rscp</c> and
///         the audio — so the audio and album art are bare file names and the
///         arrangements are reached through <c>DTRS XML/</c>. It deliberately does
///         NOT live inside that folder: <c>SaveDlcBuilderXmlBundle</c> swaps the
///         bundle in by RENAMING the old one to <c>DTRS XML.old</c> and renaming
///         staging into place, so anything written inside it is moved aside on the
///         next save and deleted on the one after.
///     </para>
/// </remarks>
public static class Rs2DlcProjectBuilder
{
    /// <summary>Rocksmith stores every tuning in six slots whatever the
    /// instrument, and DLC Builder's DTO is a six-element array.</summary>
    private const int TuningSlots = 6;

    /// <summary>
    ///     Everything the builder needs that is not on the <see cref="ChartProject" />.
    /// </summary>
    /// <param name="AudioFileName">
    ///     The audio file as it should be REFERENCED, relative to the project folder
    ///     — normally <c>ChartProject.AudioFileName</c>. Empty when the project has
    ///     no resolvable audio.
    /// </param>
    /// <param name="CoverArtFileName">Same, for the album art. Empty when unset.</param>
    /// <param name="AudioLengthMs">Decoded audio length, or null when unknown.</param>
    /// <param name="FallbackCharter">
    ///     Used for the DLC-key prefix and the Author field when the chart carries no
    ///     charter name (<c>AppConfig.DefaultCharterName</c>).
    /// </param>
    /// <param name="IncludeVocals">
    ///     Whether lyric-bearing vocal tracks become Vocals arrangements — the
    ///     shared <see cref="VocalsExportPolicy" /> decision, so this file agrees
    ///     with the psarc, the DTRS XML bundle and the <c>.eof</c> beside it.
    ///     Independent of the one-arrangement-per-track shape below, which is
    ///     deliberate.
    /// </param>
    public readonly record struct Rs2DlcBuildOptions(
        string AudioFileName,
        string CoverArtFileName,
        double? AudioLengthMs,
        string FallbackCharter,
        bool IncludeVocals = true);

    /// <summary>
    ///     Projects a saved chart into the <c>.rs2dlc</c> DTO.
    /// </summary>
    /// <param name="project">
    ///     The SAVED snapshot. Its <c>ArrangementFiles</c> manifest must already be
    ///     refreshed — those are the names the bundle wrote and the ones this file
    ///     points at.
    /// </param>
    /// <param name="options">Paths and metadata that are not on the project.</param>
    public static Rs2DlcProjectDto Build(ChartProject project, Rs2DlcBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);

        var chart = project.Chart;
        var artist = chart.Artist?.Trim() ?? "";
        var title = chart.SongName?.Trim() ?? "";
        var album = chart.AlbumName?.Trim() ?? "";

        var charter = !string.IsNullOrWhiteSpace(chart.Charter)
            ? chart.Charter.Trim()
            : options.FallbackCharter?.Trim() ?? "";

        // Deterministic, so re-saving an unchanged project rewrites an identical
        // file: a random key would churn the folder on every Ctrl+S and make DLC
        // Builder treat each import as a different DLC.
        var dlcKey = Rs2DlcStrings.CreateDlcKey(
            charter, artist, title, StableLetter(charter, artist, title));

        return new Rs2DlcProjectDto
        {
            Version = "1",
            Author = string.IsNullOrWhiteSpace(charter) ? null : charter,
            DlcKey = dlcKey,
            ArtistName = new Rs2DlcSortableString
            {
                Value = artist,
                SortValue = !string.IsNullOrWhiteSpace(chart.ArtistSort)
                    ? Rs2DlcStrings.SortField(chart.ArtistSort.Trim())
                    : Rs2DlcStrings.ArtistSortValue(artist)
            },
            Title = new Rs2DlcSortableString
            {
                Value = title,
                SortValue = Rs2DlcStrings.TitleSortValue(title)
            },
            AlbumName = new Rs2DlcSortableString
            {
                Value = album,
                SortValue = Rs2DlcStrings.AlbumSortValue(album)
            },
            // Both fields have existed on the model since it was written and
            // nothing ever filled them, so every .rs2dlc this app emitted
            // dropped the original-script names DLC Builder would have shown.
            // Null, not "", when absent: the schema treats them as optional.
            JapaneseTitle = string.IsNullOrWhiteSpace(chart.JapaneseTitle) ? null : chart.JapaneseTitle.Trim(),
            JapaneseArtistName = string.IsNullOrWhiteSpace(chart.JapaneseArtist) ? null : chart.JapaneseArtist.Trim(),
            Year = ParseYear(chart.AlbumYear),
            AlbumArtFile = options.CoverArtFileName,
            AudioFile = new Rs2DlcAudioFile { Path = options.AudioFileName },
            AudioFileLength = options.AudioLengthMs is > 0 ? options.AudioLengthMs / 1000.0 : null,
            // DotCharter has no separate preview clip. Upstream requires the field,
            // and DLC Builder generates the preview itself from the main audio when
            // this file is missing — so it names the conventional sibling rather
            // than leaving a path that resolves to the project folder.
            AudioPreviewFile = new Rs2DlcAudioFile { Path = PreviewPathFor(options.AudioFileName) },
            IgnoredIssues = [],
            Arrangements = BuildArrangements(project, dlcKey, options.IncludeVocals),
            Tones = BuildTones(project)
        };
    }

    /// <summary>
    ///     The project's tone BODIES, in DLC Builder's own shape.
    ///     <para>
    ///         <c>ToneDefinition.Json</c> already IS that shape:
    ///         <c>RsToneJsonWriter</c> emits the manifest tone object DLC Builder
    ///         reads and writes back (<c>GearList</c> keyed by slot name, per-gear
    ///         <c>Type</c>/<c>KnobValues</c>/<c>Key</c>/<c>Category</c>, knob keys
    ///         <c>&lt;GearKey&gt;_&lt;Pot&gt;</c>, <c>Volume</c> as a 3-decimal
    ///         invariant STRING). So the body is parsed and handed to the
    ///         serializer VERBATIM rather than re-modelled: there is no second
    ///         writer to keep in step and nothing is lost in translation.
    ///     </para>
    ///     <para>
    ///         This was empty until the tone generator existed, because the app had
    ///         no tone bodies to put here — not because the field is optional. A
    ///         project naming a <c>BaseTone</c> with an empty <c>Tones</c> list is
    ///         one DLC Builder cannot build.
    ///     </para>
    ///     A body that is not well-formed JSON is SKIPPED, not repaired and not
    ///     thrown on: a save must never fail because one tone is junk.
    /// </summary>
    private static object[] BuildTones(ChartProject project)
    {
        if (project.ToneDefinitions.Count == 0) return [];

        var tones = new List<object>(project.ToneDefinitions.Count);
        foreach (var definition in project.ToneDefinitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Json)) continue;

            try
            {
                using var document = JsonDocument.Parse(definition.Json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                tones.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                // Not a tone body. Nothing here can make it one.
            }
        }

        return tones.ToArray();
    }

    /// <summary>
    ///     The arrangement's tone slots and base tone — <b>the same answer the RS
    ///     XML writer gives</b>, because it is the same call
    ///     (<see cref="RsToneSlots.Resolve" />).
    ///     <para>
    ///         DLC Builder does not take a project's word for which tones an
    ///         arrangement uses: it reads <c>&lt;tonebase&gt;</c> and
    ///         <c>&lt;tonea&gt;</c>–<c>&lt;toned&gt;</c> out of the arrangement XML
    ///         and marks anything the XML never names <b>"(Unused)"</b>. So these
    ///         two files have to agree, and the only way to guarantee that is to
    ///         derive both from one function rather than to re-state the rule here.
    ///     </para>
    ///     <para>
    ///         <b>The slot list is NOT "every tone except the base".</b> An
    ///         arrangement that switches back to its base tone needs that name in a
    ///         slot too — Rocksmith's tone CHANGES address slots, not the base — so
    ///         the base legitimately appears in both fields. The owner's reference
    ///         project has a <c>BaseTone</c> and an empty <c>Tones</c> because its
    ///         arrangement has no tone changes at all, which is the same rule
    ///         arriving at a different answer.
    ///     </para>
    /// </summary>
    private static (string BaseTone, string[] Slots) ToneSlots(Arrangement arrangement)
        => RsToneSlots.Resolve(arrangement);

    /// <summary>The file name the project should be written as, e.g.
    /// <c>Polkadot-Stingray_Sakasama.rs2dlc</c>.</summary>
    public static string FileNameFor(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Rs2DlcStrings.ProjectFileName(project.Chart.Artist, project.Chart.SongName);
    }

    private static Rs2DlcArrangementDto[] BuildArrangements(ChartProject project, string dlcKey,
        bool includeVocals)
    {
        var entries = new List<Rs2DlcArrangementDto>();

        for (var i = 0; i < project.Arrangements.Count; i++)
        {
            var arrangement = project.Arrangements[i];

            // A Vocal arrangement has no instrumental XML in the bundle — the
            // vocals come from ChartProject.VocalTracks below.
            if (arrangement.ArrangementType == ArrangementType.Vocal) continue;

            var xml = i < project.ArrangementFiles.Count
                ? project.ArrangementFiles[i]
                : DlcBuilderBundle.ArrangementFileName(i + 1, arrangement);

            var tones = ToneSlots(arrangement);

            entries.Add(new Rs2DlcArrangementDto
            {
                Case = "Instrumental",
                Fields =
                [
                    new Rs2DlcInstrumentalDto
                    {
                        Id = StableGuid(dlcKey, "arr", xml),
                        Xml = InBundle(xml),
                        Name = MapName(arrangement.ArrangementType),
                        RouteMask = MapRouteMask(arrangement.ArrangementType),
                        Priority = MapPriority(arrangement.Subtype),
                        // Upstream's own default. A null ScrollSpeed here means
                        // "no preference", which is what DLC Builder shows too.
                        ScrollSpeed = arrangement.ScrollSpeed ?? 1.3,
                        BassPicked = arrangement.PickedBass,
                        Tuning = TuningVector(arrangement),
                        TuningPitch = PitchFromCents(arrangement.CentOffset),
                        BaseTone = tones.BaseTone,
                        Tones = tones.Slots,
                        MasterId = StableMasterId(dlcKey, xml),
                        PersistentId = StableGuid(dlcKey, "persist", xml)
                    }
                ]
            });
        }

        // One Vocals arrangement, as in the psarc: Rocksmith plays a single one.
        if (VocalsExportPolicy.PackageTrack(project, includeVocals) is var (track, index))
        {
            var xml = DlcBuilderBundle.VocalTrackFileName(index + 1, track);
            entries.Add(new Rs2DlcArrangementDto
            {
                Case = "Vocals",
                Fields =
                [
                    new Rs2DlcVocalsDto
                    {
                        Id = StableGuid(dlcKey, "arr", xml),
                        Xml = InBundle(xml),
                        Japanese = false,
                        MasterId = StableMasterId(dlcKey, xml),
                        PersistentId = StableGuid(dlcKey, "persist", xml)
                    }
                ]
            });
        }

        if (project.Showlights.Count > 0)
        {
            entries.Add(new Rs2DlcArrangementDto
            {
                Case = "Showlights",
                Fields =
                [
                    new Rs2DlcShowlightsDto
                    {
                        Id = StableGuid(dlcKey, "arr", DlcBuilderBundle.ShowlightsFileName),
                        Xml = InBundle(DlcBuilderBundle.ShowlightsFileName)
                    }
                ]
            });
        }

        return [.. entries];
    }

    /// <summary>
    ///     Six semitone offsets, low → high. DotCharter stores one
    ///     <see cref="TuningOffset" /> per string and a chart may have 4-9 of them;
    ///     the RS slot count is fixed at six, so a shorter vector is zero-padded and
    ///     a longer one is truncated to what the format can spell.
    /// </summary>
    public static short[] TuningVector(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        var offsets = TuningOffsetResolution.Resolve(arrangement);
        var tuning = new short[TuningSlots];
        // A 7-/8-string guitar's six slots are the six STOCK strings of the six-string
        // projection the bundle's XML carries: package order, not the chart's
        // lowest-first order, whose first six would be the wrong strings.
        var extended = Rs7StringLayout.IsExtendedGuitar(arrangement);
        for (var i = 0; i < TuningSlots; i++)
        {
            var chartString = extended ? Rs7StringLayout.ToDomain(i, arrangement.StringCount) : i;
            if (chartString < offsets.Count)
                tuning[i] = (short)Math.Clamp(offsets[chartString], short.MinValue, short.MaxValue);
        }

        return tuning;
    }

    /// <summary>A440 shifted by the arrangement's cent offset, which is how
    /// Rocksmith spells "tuned down 10 cents".</summary>
    private static double PitchFromCents(double centOffset) =>
        Math.Round(440.0 * Math.Pow(2.0, centOffset / 1200.0), 3);

    private static Rs2DlcArrangementName MapName(ArrangementType type) => type switch
    {
        ArrangementType.Lead => Rs2DlcArrangementName.Lead,
        ArrangementType.Rhythm => Rs2DlcArrangementName.Rhythm,
        ArrangementType.Bass => Rs2DlcArrangementName.Bass,
        _ => Rs2DlcArrangementName.Combo
    };

    private static Rs2DlcRouteMask MapRouteMask(ArrangementType type) => type switch
    {
        ArrangementType.Lead => Rs2DlcRouteMask.Lead,
        ArrangementType.Rhythm => Rs2DlcRouteMask.Rhythm,
        ArrangementType.Bass => Rs2DlcRouteMask.Bass,
        // A Combo arrangement is playable on either guitar path.
        _ => Rs2DlcRouteMask.Any
    };

    private static Rs2DlcArrangementPriority MapPriority(ArrangementSubtype subtype) => subtype switch
    {
        ArrangementSubtype.Bonus => Rs2DlcArrangementPriority.Bonus,
        ArrangementSubtype.Alternate => Rs2DlcArrangementPriority.Alternative,
        _ => Rs2DlcArrangementPriority.Main
    };

    private static int ParseYear(string? albumYear) =>
        int.TryParse(albumYear, CultureInfo.InvariantCulture, out var year) && year > 0 ? year : 0;

    /// <summary>An arrangement XML lives in the bundle folder, which is a sibling of
    /// the project file — so it is reached through the folder name.</summary>
    private static string InBundle(string fileName) =>
        Path.Combine(DlcBuilderBundle.DirectoryName, fileName);

    private static string PreviewPathFor(string audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName)) return "";

        var preview = Path.GetFileNameWithoutExtension(audioFileName) + "_preview" +
                      Path.GetExtension(audioFileName);

        // An absolute audio path keeps its folder; a bare file name stays bare,
        // because the project file now sits in the project root beside it.
        return Path.IsPathRooted(audioFileName)
            ? Path.Combine(Path.GetDirectoryName(audioFileName) ?? "", preview)
            : preview;
    }

    /// <summary>
    ///     A deterministic stand-in for upstream's random alphabet generator, so
    ///     the key for one song is the same on every save AND on every machine.
    /// </summary>
    private static Func<char> StableLetter(string charter, string artist, string title)
    {
        var seed = Hash($"{charter} {artist} {title}");
        var index = 0;
        return () => (char)('a' + seed[index++ % seed.Length] % 26);
    }

    private static int StableMasterId(string dlcKey, string discriminator)
    {
        var hash = Hash($"{dlcKey} master {discriminator}");
        // Positive and non-zero: RS treats 0 as unset.
        var value = BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
        return value == 0 ? 1 : value;
    }

    private static Guid StableGuid(string dlcKey, string kind, string discriminator) =>
        new(Hash($"{dlcKey} {kind} {discriminator}").AsSpan(0, 16));

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

using System.Text.Json.Serialization;

namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>
///     The on-disk shape of a DLC Builder <c>.rs2dlc</c> project file.
/// </summary>
/// <remarks>
///     Transcribed from Rocksmith2014.NET's <c>DLCProject.Dto</c> /
///     <c>InstrumentalDto</c> / <c>VocalsDto</c> / <c>ShowlightsDto</c>
///     (<c>src/Rocksmith2014.DLCProject/DLCProject.fs</c>) and checked against the
///     real file <c>tests/Rocksmith2014.IntegrationTests/project/Integration_Test.rs2dlc</c>
///     in the reference checkout.
///     <para>
///         Three encoding facts are load-bearing and are why this is hand-modelled
///         rather than mirrored from our own types:
///         <list type="number">
///             <item>
///                 The arrangement list is an F# discriminated union serialized by
///                 <c>JsonFSharpConverter</c> in its default AdjacentTag form —
///                 <c>{ "Case": "Instrumental", "Fields": [ { … } ] }</c>. A flat
///                 object there does not deserialize.
///             </item>
///             <item>
///                 Enums are written as NUMBERS (<c>"Name": 0</c>,
///                 <c>"RouteMask": 1</c>), which is System.Text.Json's default and
///                 the reason no <c>JsonStringEnumConverter</c> may be added to
///                 <see cref="Rs2DlcWriter" />'s options.
///             </item>
///             <item>
///                 Upstream writes with <c>ignoreNull = true</c>, so a null field is
///                 ABSENT rather than <c>null</c> — hence the nullable properties
///                 here and <c>JsonIgnoreCondition.WhenWritingNull</c> on the writer.
///                 An empty ARRAY still writes as <c>[]</c>.
///             </item>
///         </list>
///     </para>
/// </remarks>
public sealed class Rs2DlcProjectDto
{
    /// <summary>Upstream's <c>DLCProject.Empty</c> writes "1"; there has never been
    /// a second value.</summary>
    public string Version { get; set; } = "1";

    public string? Author { get; set; }

    [JsonPropertyName("DLCKey")]
    public string DlcKey { get; set; } = "";

    public Rs2DlcSortableString ArtistName { get; set; } = new();
    public string? JapaneseArtistName { get; set; }
    public string? JapaneseTitle { get; set; }
    public Rs2DlcSortableString Title { get; set; } = new();
    public Rs2DlcSortableString AlbumName { get; set; } = new();
    public int Year { get; set; }
    public string AlbumArtFile { get; set; } = "";
    public Rs2DlcAudioFile AudioFile { get; set; } = new();

    /// <summary>Seconds. Upstream is a <c>TimeSpan option</c> flattened to
    /// <c>Nullable&lt;float&gt;</c> of TOTAL SECONDS on the wire.</summary>
    public double? AudioFileLength { get; set; }

    public Rs2DlcAudioFile AudioPreviewFile { get; set; } = new();

    /// <summary>Seconds, same flattening as <see cref="AudioFileLength" />.</summary>
    public double? AudioPreviewStartTime { get; set; }

    public short? PitchShift { get; set; }
    public string[] IgnoredIssues { get; set; } = [];
    public Rs2DlcArrangementDto[] Arrangements { get; set; } = [];

    /// <summary>
    ///     Rocksmith tone BODIES, populated from <c>ChartProject.ToneDefinitions</c>
    ///     by <see cref="Rs2DlcProjectBuilder" /> — each entry is the manifest tone
    ///     object itself (<c>GearList</c>, <c>ToneDescriptors</c>, <c>Volume</c>,
    ///     <c>Key</c>, <c>Name</c>), so it is typed as <c>object</c> and written
    ///     through verbatim.
    ///     <para>
    ///         Empty ONLY when the project owns no tone definitions. It was always
    ///         empty before the tone generator, because the app had nothing to put
    ///         here — and an arrangement naming a <c>BaseTone</c> whose body is
    ///         absent is a project DLC Builder cannot build.
    ///     </para>
    /// </summary>
    public object[] Tones { get; set; } = [];
}

/// <summary>Upstream <c>SortableString</c>: the displayed value plus the value the
/// game sorts on.</summary>
public sealed class Rs2DlcSortableString
{
    public string Value { get; set; } = "";
    public string SortValue { get; set; } = "";
}

/// <summary>Upstream <c>AudioFile</c>. <c>Volume</c> is dB; upstream's own empty
/// value is <c>-7</c>, which is what an unmeasured file gets.</summary>
public sealed class Rs2DlcAudioFile
{
    public string Path { get; set; } = "";
    public double Volume { get; set; } = -7.0;
}

/// <summary>
///     One entry of the arrangement union. <see cref="Case" /> is
///     <c>Instrumental</c>, <c>Vocals</c> or <c>Showlights</c> and
///     <see cref="Fields" /> always holds exactly one payload object.
/// </summary>
public sealed class Rs2DlcArrangementDto
{
    public string Case { get; set; } = "";
    public object[] Fields { get; set; } = [];
}

/// <summary>Upstream <c>ArrangementName</c>. Written as a number.</summary>
public enum Rs2DlcArrangementName
{
    Lead = 0,
    Combo = 1,
    Rhythm = 2,
    Bass = 3
}

/// <summary>Upstream <c>RouteMask</c>. Written as a number.</summary>
public enum Rs2DlcRouteMask
{
    None = 0,
    Lead = 1,
    Rhythm = 2,
    Any = 3,
    Bass = 4
}

/// <summary>Upstream <c>ArrangementPriority</c>. Written as a number.</summary>
public enum Rs2DlcArrangementPriority
{
    Main = 0,
    Alternative = 1,
    Bonus = 2
}

/// <summary>Upstream <c>InstrumentalDto</c>.</summary>
public sealed class Rs2DlcInstrumentalDto
{
    [JsonPropertyName("ID")]
    public Guid Id { get; set; }

    [JsonPropertyName("XML")]
    public string Xml { get; set; } = "";

    public Rs2DlcArrangementName Name { get; set; } = Rs2DlcArrangementName.Lead;
    public Rs2DlcRouteMask RouteMask { get; set; } = Rs2DlcRouteMask.None;
    public Rs2DlcArrangementPriority Priority { get; set; } = Rs2DlcArrangementPriority.Main;
    public double ScrollSpeed { get; set; } = 1.3;
    public bool BassPicked { get; set; }

    /// <summary>Per-string semitone offsets from standard, low → high. Rocksmith
    /// stores SIX slots whatever the instrument, so this is padded to six.</summary>
    public short[] Tuning { get; set; } = [0, 0, 0, 0, 0, 0];

    public double TuningPitch { get; set; } = 440.0;
    public string BaseTone { get; set; } = "";
    public string[] Tones { get; set; } = [];
    public Rs2DlcAudioFile? CustomAudio { get; set; }

    [JsonPropertyName("MasterID")]
    public int MasterId { get; set; }

    [JsonPropertyName("PersistentID")]
    public Guid PersistentId { get; set; }
}

/// <summary>Upstream <c>VocalsDto</c>.</summary>
public sealed class Rs2DlcVocalsDto
{
    [JsonPropertyName("ID")]
    public Guid Id { get; set; }

    [JsonPropertyName("XML")]
    public string Xml { get; set; } = "";

    public bool Japanese { get; set; }
    public string? CustomFont { get; set; }

    [JsonPropertyName("MasterID")]
    public int MasterId { get; set; }

    [JsonPropertyName("PersistentID")]
    public Guid PersistentId { get; set; }
}

/// <summary>Upstream <c>ShowlightsDto</c>.</summary>
public sealed class Rs2DlcShowlightsDto
{
    [JsonPropertyName("ID")]
    public Guid Id { get; set; }

    [JsonPropertyName("XML")]
    public string Xml { get; set; } = "";
}

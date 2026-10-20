using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Services.Export.Package;

/// <summary>Song-level metadata for a Rocksmith package.</summary>
internal sealed class RsPackageMetadata
{
    /// <summary>The DLC key ("SongKey"), e.g. "DotcMySong". Alphanumeric.</summary>
    public required string DlcKey { get; init; }

    public required string Title { get; init; }
    public string TitleSort => SortValue(Title);
    public required string Artist { get; init; }

    /// <summary>The AUTHORED sort name, when the chart has one
    /// (<c>Arrangement.ArtistNameSort</c> / <c>ChartData.ArtistSort</c> — the
    /// RS-XML's <c>&lt;artistNameSort&gt;</c>). Empty falls back to
    /// <see cref="SortValue"/>, which only strips a leading article.</summary>
    public string ArtistSortOverride { get; init; } = "";

    public string ArtistSort => string.IsNullOrWhiteSpace(ArtistSortOverride)
        ? SortValue(Artist)
        : ArtistSortOverride.Trim();
    public string Album { get; init; } = "";
    public string AlbumSort => SortValue(Album);

    /// <summary>Title / artist in their original script. Emitted as the
    /// manifest's <c>JapaneseSongName</c> / <c>JapaneseArtistName</c> only when
    /// non-empty — the game treats the attributes as optional and official DLC
    /// omits them entirely for Latin-titled songs. There is no album
    /// counterpart in the manifest.</summary>
    public string JapaneseTitle { get; init; } = "";

    public string JapaneseArtist { get; init; } = "";
    public int? Year { get; init; }
    public float AverageTempo { get; init; } = 120f;

    /// <summary>Package author, written to toolkit.version ("Package Author"),
    /// which is what importers (including ours) read the charter back from.</summary>
    public string Charter { get; init; } = "DotCharter";

    internal static string SortValue(string value)
    {
        var trimmed = value.Trim();
        foreach (var article in new[] { "The ", "A ", "An " })
        {
            if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length > article.Length)
            {
                return trimmed[article.Length..];
            }
        }

        return trimmed;
    }
}

/// <summary>One arrangement prepared for packaging: the converted SNG plus
/// everything the manifests/xblock/graph need to describe it.</summary>
internal sealed class RsPackageArrangement
{
    /// <summary>"Lead", "Rhythm", "Bass", "Combo" or "Vocals" (generic name).</summary>
    public required string ArrangementName { get; init; }

    /// <summary>Partition file name: "lead", "lead2", "rhythm", "bass", "vocals"...</summary>
    public required string PartitionName { get; init; }

    /// <summary>1-based partition number within arrangements of the same name.</summary>
    public int SongPartition { get; init; } = 1;

    public required Guid PersistentId { get; init; }
    public int MasterId { get; init; }

    public required RocksmithSng Sng { get; init; }

    /// <summary>Null for vocals arrangements.</summary>
    public SngNoteCounts? NoteCounts { get; init; }

    /// <summary>The source domain arrangement; null for vocals.</summary>
    public Arrangement? Domain { get; init; }

    public string ToneBase { get; init; } = "";
    public string[] ToneSlots { get; init; } = [];

    /// <summary>Raw manifest "Tones" entry JSON bodies for the tone keys this
    /// arrangement uses. Kept as strings (not parsed nodes) because a
    /// <c>JsonNode</c> is single-parent and the same tone may be embedded in
    /// several arrangement manifests.</summary>
    public string[] ToneDefinitions { get; init; } = [];

    public bool IsVocals => Domain == null;

    /// <summary>ArrangementName enum value: Lead=0, Combo=1, Rhythm=2, Bass=3.</summary>
    public int ArrangementTypeValue => ArrangementName switch
    {
        "Combo" => 1,
        "Rhythm" => 2,
        "Bass" => 3,
        _ => 0
    };

    /// <summary>
    ///     The flags this arrangement ships, resolved once through
    ///     <see cref="ArrangementPropertiesDeriver" /> — content-derived technique
    ///     flags unless the charter pinned them, plus the authored path/priority/
    ///     picked-bass mirrors. Null for vocals.
    ///     <para>
    ///     Everything that describes techniques to the game must read THIS, never
    ///     <c>Domain.Properties</c>: the manifest writes the flags out and
    ///     <c>RsManifestGenerator.CalculateDifficulties</c> weighs them into
    ///     <c>SongDiffEasy/Med/Hard</c>, so the two have to be looking at the same
    ///     object.
    ///     </para>
    /// </summary>
    public ArrangementProperties? Properties =>
        _properties ??= Domain == null ? null : ArrangementPropertiesDeriver.Derive(Domain);

    private ArrangementProperties? _properties;

    /// <summary>
    ///     RouteMask: Lead=1, Rhythm=2, Any=3, Bass=4.
    ///     <para>
    ///     The AUTHORED route wins over the arrangement name, because the name does
    ///     not always carry it: the official base game calls both of Satch Boogie's
    ///     guitar arrangements "Combo" and only the path flags say which is which
    ///     (the same reason the package reader's arrangement-properties parse
    ///     refuses the name fallback while flags are present). The name is the
    ///     fallback for vocals, which have no domain arrangement at all.
    ///     </para>
    /// </summary>
    public int RouteMaskValue
    {
        get
        {
            if (Domain != null)
            {
                var route = ArrangementPropertiesDeriver.ResolveRouteMask(Domain);
                if (route != 0) return route;
            }

            return ArrangementName switch
            {
                "Combo" => 3,
                "Rhythm" => 2,
                "Bass" => 4,
                "Vocals" => 0,
                _ => 1
            };
        }
    }

    public bool IsRepresentative =>
        Domain is { Subtype: ArrangementSubtype.Main };
}

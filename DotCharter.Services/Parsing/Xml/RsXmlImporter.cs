using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Showlights;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Services.Parsing.Xml;

public enum RsXmlImportKind
{
    Unknown,
    Song,
    Vocals,
    Showlights
}

public sealed class RsXmlImportResult
{
    public RsXmlImportKind Kind { get; init; }
    public string? AddedArrangementName { get; init; }
    public int AddedVocalPhrases { get; init; }
    public int AddedShowlights { get; init; }
    public bool ReplacedHostBeats { get; init; }
}

/// <summary>
///     Everything <see cref="RsXmlImporter.Prepare" /> read off disk, ready to be
///     merged. Holding this between the two halves is what lets a caller do the
///     multi-megabyte XML read on a background thread and touch the live project
///     only on the thread that owns it.
/// </summary>
public sealed class RsXmlImportPlan
{
    public RsXmlImportKind Kind { get; init; }
    public string FilePath { get; init; } = "";

    internal Arrangement? Arrangement { get; init; }
    internal IReadOnlyList<BeatMarker> Beats { get; init; } = [];
    internal VocalTrack? VocalTrack { get; init; }
    internal IReadOnlyList<Showlight> Showlights { get; init; } = [];
}

/// <summary>
/// Merges an RS-XML document (<c>song</c>, <c>vocals</c> or <c>showlights</c>)
/// into an existing <see cref="ChartProject"/>. Unlike <see cref="XmlChartSerializer.Load"/>,
/// which produces a fresh project, this preserves the host project's audio,
/// beats, existing arrangements and vocal tracks.
/// <para>
/// Split into <see cref="Prepare" /> (the READ — safe on a background thread,
/// takes only a snapshot of the host beats) and <see cref="Apply" /> (the MERGE
/// — must run on whichever thread owns the project). <see cref="ImportInto" />
/// is the two of them back to back, for callers that do not care.
/// </para>
/// </summary>
public sealed class RsXmlImporter
{
    public RsXmlImportResult ImportInto(ChartProject host, string filePath)
    {
        if (host == null) throw new ArgumentNullException(nameof(host));
        return Apply(host, Prepare(filePath, host.Beats));
    }

    /// <summary>
    ///     Reads and parses the document. Nothing here touches the host project:
    ///     <paramref name="hostBeats" /> is a read-only snapshot the vocals and
    ///     showlights readers need to resolve their times, and the caller passes
    ///     a copy when the live list can change underneath.
    /// </summary>
    public static RsXmlImportPlan Prepare(string filePath, IList<BeatMarker> hostBeats)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            // ReSharper disable once LocalizableElement -- developer-facing argument validation.
            throw new ArgumentException("File path required.", nameof(filePath));

        var doc = SafeXml.LoadFromFile(filePath);
        var rootName = doc.Root?.Name.LocalName ?? "";

        return rootName switch
        {
            "song" => PrepareSong(doc, filePath),
            "vocals" => PrepareVocals(doc, filePath, hostBeats),
            "showlights" => PrepareShowlights(doc, filePath, hostBeats),
            _ => throw new InvalidDataException(
                $"Unknown RS-XML root element '{rootName}' in '{filePath}'. " +
                "Expected 'song', 'vocals', or 'showlights'.")
        };
    }

    /// <summary>Merges a prepared plan into the live project. Cheap — list adds
    /// and one name disambiguation.</summary>
    public RsXmlImportResult Apply(ChartProject host, RsXmlImportPlan plan)
    {
        if (host == null) throw new ArgumentNullException(nameof(host));
        if (plan == null) throw new ArgumentNullException(nameof(plan));

        return plan.Kind switch
        {
            RsXmlImportKind.Song => ApplySong(host, plan),
            RsXmlImportKind.Vocals => ApplyVocals(host, plan),
            RsXmlImportKind.Showlights => ApplyShowlights(host, plan),
            _ => new RsXmlImportResult { Kind = plan.Kind }
        };
    }

    private static RsXmlImportPlan PrepareSong(XDocument doc, string filePath)
    {
        var loaded = new RsXmlSongReader { MergeSimultaneousNotes = true }.ReadDocument(doc, filePath);
        return new RsXmlImportPlan
        {
            Kind = RsXmlImportKind.Song,
            FilePath = filePath,
            Arrangement = loaded.Arrangements.FirstOrDefault(),
            Beats = loaded.Beats.ToList()
        };
    }

    private static RsXmlImportPlan PrepareVocals(XDocument doc, string filePath,
        IList<BeatMarker> hostBeats)
    {
        // Read positions against the host beats so events land at the correct
        // musical positions (vocals XML has no beat map of its own).
        var loaded = new RsXmlVocalsReader().ReadDocument(doc, filePath, hostBeats);
        return new RsXmlImportPlan
        {
            Kind = RsXmlImportKind.Vocals,
            FilePath = filePath,
            VocalTrack = loaded.VocalTracks.FirstOrDefault()
        };
    }

    private static RsXmlImportPlan PrepareShowlights(XDocument doc, string filePath,
        IList<BeatMarker> hostBeats)
    {
        var loaded = new RsXmlShowlightsReader().ReadDocument(doc, filePath, hostBeats);
        return new RsXmlImportPlan
        {
            Kind = RsXmlImportKind.Showlights,
            FilePath = filePath,
            Showlights = loaded.Showlights.ToList()
        };
    }

    private static RsXmlImportResult ApplySong(ChartProject host, RsXmlImportPlan plan)
    {
        var arr = plan.Arrangement;
        if (arr == null) return new RsXmlImportResult { Kind = RsXmlImportKind.Song };

        // If the host project has no beats yet, adopt the imported beat map so
        // every position in the new arrangement lines up. Otherwise keep the
        // host beats untouched and trust they match (typical when importing
        // RS-XML companions of an existing project — they share the same beats).
        // Decided HERE, not in Prepare: the answer is a fact about the live
        // project at merge time.
        var replacedBeats = false;
        if (host.Beats.Count == 0 && plan.Beats.Count > 0)
        {
            foreach (var b in plan.Beats) host.Beats.Add(b);
            replacedBeats = true;
        }

        // Disambiguate name collisions so users see both old + new arrangements.
        arr.Name = MakeUniqueArrangementName(host, arr.Name);
        host.Arrangements.Add(arr);

        // Carry over showlights / tone names if host had none — the song XML
        // doesn't contain them, but the reader may have populated empties.
        return new RsXmlImportResult
        {
            Kind = RsXmlImportKind.Song,
            AddedArrangementName = arr.Name,
            ReplacedHostBeats = replacedBeats
        };
    }

    private static RsXmlImportResult ApplyVocals(ChartProject host, RsXmlImportPlan plan)
    {
        var track = plan.VocalTrack;
        if (track == null) return new RsXmlImportResult { Kind = RsXmlImportKind.Vocals };

        track.Name = MakeUniqueVocalTrackName(host, track.Name);
        host.VocalTracks.Add(track);

        return new RsXmlImportResult
        {
            Kind = RsXmlImportKind.Vocals,
            AddedVocalPhrases = track.Phrases.Count
        };
    }

    private static RsXmlImportResult ApplyShowlights(ChartProject host, RsXmlImportPlan plan)
    {
        var added = 0;
        foreach (var s in plan.Showlights)
        {
            host.Showlights.Add(s);
            added++;
        }

        return new RsXmlImportResult
        {
            Kind = RsXmlImportKind.Showlights,
            AddedShowlights = added
        };
    }

    // Both through the shared rule (ArrangementNames.MakeUnique) — the psarc
    // import needed the same one and a third copy is a third thing to keep right.
    private static string MakeUniqueArrangementName(ChartProject host, string desired)
        => ArrangementNames.MakeUnique(host.Arrangements.Select(a => a.Name), desired);

    private static string MakeUniqueVocalTrackName(ChartProject host, string desired)
        => ArrangementNames.MakeUnique(host.VocalTracks.Select(t => t.Name), desired, "Vocals");
}

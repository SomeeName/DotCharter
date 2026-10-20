using System.Globalization;
using System.Xml.Linq;
using Bookmark = DotCharter.Core.Domain.Events.Bookmark;
using ChartData = DotCharter.Core.Domain.Project.ChartData;
using ChartProject = DotCharter.Core.Domain.Project.ChartProject;
using ProjectIdentity = DotCharter.Core.Domain.Project.ProjectIdentity;
using Showlight = DotCharter.Core.Domain.Events.Showlight;
using Stem = DotCharter.Core.Domain.Project.Stem;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Parses the header section of RSCP chart files including metadata, stems, bookmarks, and showlights
/// </summary>
public class RscpHeaderParser
{
    /// <summary>
    ///     Every element name this parser looks up, and the ONLY names
    ///     <see cref="RscpFirstElementIndex" /> will answer — a name added to a
    ///     <c>header.First(...)</c> call below and not to this list silently reads
    ///     as absent, so <c>RscpHeaderIndexCoverageTests</c> derives the list from
    ///     this file's source and fails the gate on a mismatch.
    /// </summary>
    internal static readonly string[] HeaderElementNames =
    [
        "chartFormatVersion", "editMode", "arrangement", "level", "time", "title",
        "artistName", "artistNameSort", "albumName", "dcJapaneseTitle", "dcJapaneseArtist",
        "albumYear", "dcCharter", "dcGenre", "dcReleaseDate", "dcCoverArt",
        "dcKeyAccidentals", "dcKeyIsMinor", "barsAdded", "selectedStem",
        "mainAudioOffsetAdded", "musicFileName", "dcBeatMapLocked", "text",
        "stems", "arrangementFiles", "showlights", "bookmarks", "dcAnnotations",
        "dcToneDefinitions", "dcSynthPlayStyle", "dcProjectId", "dcProvenance", "dcVersion"
    ];

    public void ParseChartHeader(XDocument doc, string filePath, ChartProject project)
    {
        // ONE pre-order walk answers all thirty lookups below. Each would otherwise be
        // its own doc.Descendants(name).FirstOrDefault() — a full-tree walk for
        // any name the file does not carry, and for the four the writer emits
        // after <arrangements>. See RscpFirstElementIndex.
        var header = RscpFirstElementIndex.Build(doc, HeaderElementNames);

        // No project-level tuning is stamped here (see the note on
        // ChartData). Reading the FIRST <tuning strings="N"> node in the
        // document — an ARRANGEMENT's node — to build N *standard-tuning* names
        // from it would discard the offsets that node carries: an Eb chart would load
        // back as E, and a project with a 6-string lead and a 4-string bass would tell
        // every reader "6" while the bass is being edited. Each arrangement's own
        // tuning is parsed by RscpArrangementParser.ParseTuning.
        project.Chart = new ChartData
        {
            // A missing/unparseable version reads as current — deliberately, and matching
            // RsCharter, whose own field default is 3 so a file predating the element also
            // deserializes as 3. The number is therefore NOT trustworthy on its own, which
            // is exactly why RscpMigrations detects an old project by its STRUCTURE
            // (arrangements named but not embedded) rather than by this value.
            ChartFormatVersion =
                int.TryParse(header.First("chartFormatVersion")?.Value, CultureInfo.InvariantCulture, out var cfv)
                    ? cfv
                    : RscpMigrations.CurrentVersion,
            EditMode = header.First("editMode")?.Value ?? "GUITAR",
            ArrangementId = int.TryParse(header.First("arrangement")?.Value, CultureInfo.InvariantCulture, out var arrId)
                ? arrId
                : 0,
            Level = int.TryParse(header.First("level")?.Value, CultureInfo.InvariantCulture, out var lvl) ? lvl : 0,
            Time = double.TryParse(header.First("time")?.Value, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var chartTime)
                ? chartTime
                : 0.0,
            SongName = header.First("title")?.Value ?? Path.GetFileNameWithoutExtension(filePath),
            // Missing metadata reads EMPTY, never a placeholder sentinel — see
            // the comment on ChartData. A "Unknown Artist" / whitespace default
            // here passes every downstream blank check and gets exported.
            Artist = header.First("artistName")?.Value ?? "",
            ArtistSort = header.First("artistNameSort")?.Value ?? "",
            AlbumName = header.First("albumName")?.Value ?? "",
            JapaneseTitle = header.First("dcJapaneseTitle")?.Value ?? "",
            JapaneseArtist = header.First("dcJapaneseArtist")?.Value ?? "",
            AlbumYear = header.First("albumYear")?.Value ?? "",
            Charter = header.First("dcCharter")?.Value ?? "",
            Genre = header.First("dcGenre")?.Value ?? "",
            ReleaseDate = header.First("dcReleaseDate")?.Value ?? "",
            // A file from before the field, or a blank one, is version 1 (DLC Builder's default).
            Version = header.First("dcVersion")?.Value.Trim() is { Length: > 0 } version ? version : "1",
            CoverArtPath = header.First("dcCoverArt")?.Value ?? "",
            // Key signature (DotCharter extension). Absent in every pre-0.35
            // file and in anything rscharter wrote, which reads as C major.
            KeyAccidentals = int.TryParse(header.First("dcKeyAccidentals")?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var keyAcc)
                ? keyAcc
                : 0,
            KeyIsMinor = header.First("dcKeyIsMinor")?.Value == "true",
            BarsAdded = int.TryParse(header.First("barsAdded")?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var ba) ? ba : 0,
            SelectedStem = int.TryParse(header.First("selectedStem")?.Value, CultureInfo.InvariantCulture, out var stemId)
                ? stemId
                : -1,
            MainAudioOffsetAdded = double.TryParse(header.First("mainAudioOffsetAdded")?.Value,
                NumberStyles.Any, CultureInfo.InvariantCulture, out var offset)
                ? offset
                : 0.0
        };

        // The stable project identity. Only a well-formed id is taken: the
        // chat history names a directory after it, so a hand-edited value is not
        // trusted, and a missing or malformed one is left to the serializer, which
        // derives one for the file (RscpChartSerializer.Load).
        var projectId = header.First("dcProjectId")?.Value.Trim();
        if (ProjectIdentity.IsWellFormed(projectId)) project.ProjectId = projectId!;

        project.AudioFileName = header.First("musicFileName")?.Value ?? "";
        // The beat-map lock (DotCharter extension); absent reads as unlocked,
        // which is both the pre-0.35 behaviour and the safe default.
        project.IsBeatMapLocked = header.First("dcBeatMapLocked")?.Value == "true";
        // Official provenance; absent reads as unrestricted.
        project.Provenance = RscpProvenance.Parse(header.First("dcProvenance")?.Value);
        ParseSynthPlayStyle(header, project);

        ParseStems(header, project);
        ParseArrangementFiles(header, project);
        ParseShowlights(header, project);
        ParseBookmarks(header, project);
        ParseAnnotations(header, project);
        ParseToneDefinitions(header, project);

        project.TextContent = header.First("text")?.Value ?? "";
    }

    private void ParseStems(RscpFirstElementIndex header, ChartProject project)
    {
        var stemsNode = header.First("stems");
        if (stemsNode == null) return;

        foreach (var stemNode in stemsNode.Elements("stem"))
        {
            project.Stems.Add(new Stem
            {
                Name = stemNode.Attribute("name")?.Value ?? "song",
                Path = stemNode.Attribute("path")?.Value ?? "song.wav",
                Local = (stemNode.Attribute("local")?.Value ?? "true") == "true",
                Volume = double.TryParse(stemNode.Attribute("volume")?.Value, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var v)
                    ? v
                    : 1.0,
                Offset = double.TryParse(stemNode.Attribute("offset")?.Value, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var o)
                    ? o
                    : 0.0,
                // Additive DotCharter attributes; absent reads as unmuted/unsoloed.
                Muted = stemNode.Attribute("muted")?.Value == "true",
                Solo = stemNode.Attribute("solo")?.Value == "true"
            });
        }

        // Absent means the file predates the attribute, and re-deriving it the way
        // a mixer default would — "muted, because stems exist" — is exactly the bug: a
        // project whose stems are ALL muted then opens with nothing audible at all.
        // "Muted iff any stem is audible" reads those files the way they were meant.
        var originalMuted = stemsNode.Attribute("originalMuted")?.Value;
        project.OriginalMixMuted = originalMuted is null
            ? project.Stems.Any(s => !s.Muted)
            : originalMuted == "true";
        project.GuitarRolesSplit = stemsNode.Attribute("guitarRoles")?.Value == "true";
    }

    private void ParseArrangementFiles(RscpFirstElementIndex header, ChartProject project)
    {
        var arrFilesNode = header.First("arrangementFiles");
        if (arrFilesNode == null) return;

        foreach (var af in arrFilesNode.Elements("string")) project.ArrangementFiles.Add(af.Value);
    }

    private void ParseShowlights(RscpFirstElementIndex header, ChartProject project)
    {
        var slNode = header.First("showlights");
        if (slNode == null) return;

        foreach (var sl in slNode.Elements("showlight"))
        {
            var position = RscpParsingUtilities.ParseFraction(sl.Attribute("p")?.Value);
            var rawType = sl.Attribute("t")?.Value ?? "";

            // RsCharter's Showlight.types is a List<ShowlightType> serialized as
            // a comma-separated string (ShowlightConverter.java:30-31). Each type
            // becomes its own Showlight entry sharing the same position so the
            // single-type domain model can represent stacked lights losslessly.
            var types = rawType.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (types.Length == 0) types = [""];

            foreach (var t in types)
            {
                var type = ShowlightCatalog.NormalizeType(t);
                project.Showlights.Add(new Showlight
                {
                    Position = position,
                    Type = type,
                    Note = ShowlightCatalog.NormalizeNote(type)
                });
            }
        }
    }

    private void ParseAnnotations(RscpFirstElementIndex header, ChartProject project)
    {
        var node = header.First("dcAnnotations");
        if (node == null) return;

        foreach (var el in node.Elements("annotation"))
        {
            if (!double.TryParse(el.Attribute("time")?.Value, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var timeMs))
            {
                continue;
            }

            Enum.TryParse<ChartAnnotationKind>(el.Attribute("kind")?.Value, out var kind);
            project.Annotations.Add(new ChartAnnotation
            {
                TimeMs = timeMs,
                Kind = kind,
                Resolved = string.Equals(el.Attribute("resolved")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                Text = el.Value
            });
        }
    }

    /// <summary>How the chart synth plays this song. Absent reads as the
    /// defaults; a value the rules do not offer reads as its default.</summary>
    private static void ParseSynthPlayStyle(RscpFirstElementIndex header, ChartProject project)
    {
        var node = header.First("dcSynthPlayStyle");
        if (node == null) return;
        ReadRules(node.Element("guitar"), project.SynthPlayStyle.Guitar);
        ReadRules(node.Element("bass"), project.SynthPlayStyle.Bass);

        static void ReadRules(XElement? el, DotCharter.Core.Domain.Project.ChartSynthPlayRules rules)
        {
            if (el == null) return;
            rules.HandShapesRing = el.Attribute("handShapesRing")?.Value != "false";
            rules.HoldSingleNoteGaps = el.Attribute("holdNotes")?.Value != "false";
            rules.HoldChordGaps = el.Attribute("holdChords")?.Value != "false";
            rules.SingleNoteGapDivisor = Divisor(el.Attribute("noteGap")?.Value);
            rules.ChordGapDivisor = Divisor(el.Attribute("chordGap")?.Value);
        }

        static int Divisor(string? raw) =>
            DotCharter.Core.Domain.Project.ChartSynthPlayRules.Sanitize(
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0);
    }

    private void ParseToneDefinitions(RscpFirstElementIndex header, ChartProject project)
    {
        // DotCharter extension node (dcToneDefinitions) — the project-owned
        // Rocksmith tone bodies. Schema-tolerant: entries without a key are
        // dropped, duplicate keys keep the first occurrence.
        var node = header.First("dcToneDefinitions");
        if (node == null) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in node.Elements("toneDefinition"))
        {
            var key = el.Attribute("key")?.Value?.Trim() ?? "";
            if (key.Length == 0 || !seen.Add(key)) continue;

            var name = el.Attribute("name")?.Value ?? "";
            project.ToneDefinitions.Add(new ToneDefinition
            {
                Key = key,
                Name = name.Length == 0 ? key : name,
                Json = el.Value
            });
        }
    }

    private void ParseBookmarks(RscpFirstElementIndex header, ChartProject project)
    {
        var bmNode = header.First("bookmarks");
        if (bmNode == null) return;

        foreach (var entry in bmNode.Elements("entry"))
        {
            int idx;
            double time;
            // Prefer attribute style (rscharter MapConverterIntDouble write format).
            var keyAttr = entry.Attribute("key")?.Value;
            var valueAttr = entry.Attribute("value")?.Value;
            if (keyAttr != null && valueAttr != null)
            {
                if (int.TryParse(keyAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) &&
                    double.TryParse(valueAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out time))
                {
                    project.Bookmarks.Add(new Bookmark { Index = idx, Time = time });
                }
            }
            else if (int.TryParse(entry.Element("int")?.Value, CultureInfo.InvariantCulture, out idx) &&
                     double.TryParse(entry.Element("double")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture,
                         out time))
            {
                project.Bookmarks.Add(new Bookmark { Index = idx, Time = time });
            }
        }
    }
}

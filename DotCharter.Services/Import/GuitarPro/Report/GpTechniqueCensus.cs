using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Services.Import.GuitarPro.Report;

/// <summary>
///     Counts what a Guitar Pro 6/7 score (<c>score.gpif</c>) AUTHORS per track — notes and
///     the techniques the chart model can carry — weighted by how often each written bar is
///     played after the repeats unfold. Compared with the same counts over the imported
///     arrangement, a shortfall is a technique the importer dropped. Reads the XML directly,
///     never through the importer, so it is an independent witness.
/// </summary>
public static class GpTechniqueCensus
{
    /// <summary>The technique classes both sides are counted in, in report order.</summary>
    public static readonly IReadOnlyList<string> Techniques =
        ["notes", "bend", "slide", "slide-in", "hopo", "tap", "palm-mute", "dead", "harmonic", "vibrato", "tremolo", "slap-pop"];

    /// <summary>Per track (in <c>&lt;Tracks&gt;</c> order), technique → count. Null when the file
    /// holds no gpif (GP3-5 binaries).</summary>
    public static IReadOnlyList<Dictionary<string, int>>? CountSource(string gpFilePath, IReadOnlyList<int> playsPerSourceBar)
    {
        XDocument? doc;
        try
        {
            doc = GpImporter.LoadScoreDocument(gpFilePath);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var root = doc?.Root;
        if (root == null) return null;
        var trackCount = root.Element("Tracks")?.Elements("Track").Count() ?? 0;
        var bars = IdMap(root.Element("Bars"), "Bar");
        var voices = IdMap(root.Element("Voices"), "Voice");
        var beats = IdMap(root.Element("Beats"), "Beat");
        var notes = IdMap(root.Element("Notes"), "Note");

        var result = Enumerable.Range(0, trackCount).Select(_ => NewCounts()).ToList();
        var masterBars = root.Element("MasterBars")?.Elements("MasterBar").ToList() ?? [];
        for (var mb = 0; mb < masterBars.Count; mb++)
        {
            var plays = mb < playsPerSourceBar.Count ? playsPerSourceBar[mb] : 0;
            if (plays == 0) continue;
            var barIds = Ids(masterBars[mb].Element("Bars")?.Value);
            for (var track = 0; track < barIds.Count && track < trackCount; track++)
            {
                if (!bars.TryGetValue(barIds[track], out var bar)) continue;
                foreach (var voiceId in Ids(bar.Element("Voices")?.Value))
                {
                    if (!voices.TryGetValue(voiceId, out var voice)) continue;
                    foreach (var beatId in Ids(voice.Element("Beats")?.Value))
                    {
                        if (!beats.TryGetValue(beatId, out var beat) || beat.Element("GraceNotes") != null) continue;
                        var tremolo = beat.Element("Tremolo") != null;
                        // Guitar Pro 7+ writes slap and pop on the BEAT; older files on the note.
                        var slapPop = beat.Element("Properties")?.Elements("Property")
                            .Any(p => p.Attribute("name")?.Value is "Slapped" or "Popped") == true;
                        foreach (var noteId in Ids(beat.Element("Notes")?.Value))
                        {
                            if (notes.TryGetValue(noteId, out var note))
                                CountNote(note, tremolo, slapPop, plays, result[track]);
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>The same classes over an imported (hardest) level.</summary>
    /// <param name="level">The imported level.</param>
    /// <param name="slideInsMaterialized">The importer's own tally of slide-ins it turned
    /// into a sliding origin sound (<see cref="GpImportResult.SlideInsMaterialized"/>). In the
    /// chart such an origin is an ordinary note that slides, so it is moved out of
    /// <c>notes</c> and <c>slide</c> (the score authored neither) and into <c>slide-in</c>.</param>
    public static Dictionary<string, int> CountImported(ChartLevel level, int slideInsMaterialized = 0)
    {
        var counts = NewCounts();
        foreach (var note in level.Notes) CountSound(note, counts);
        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes) CountSound(chordNote, counts);
        }

        counts["slide-in"] = slideInsMaterialized;
        counts["notes"] -= slideInsMaterialized;
        counts["slide"] -= slideInsMaterialized;
        return counts;
    }

    private static void CountSound(IGuitarSound sound, Dictionary<string, int> counts)
    {
        counts["notes"]++;
        if (sound.BendValues.Count > 0) counts["bend"]++;
        if (sound.SlideTo != null || sound.UnpitchedSlide) counts["slide"]++;
        if (sound.Hopo is HopoType.HammerOn or HopoType.PullOff) counts["hopo"]++;
        if (sound.Hopo == HopoType.Tap) counts["tap"]++;
        if (sound.Mute == MuteType.Palm) counts["palm-mute"]++;
        if (sound.Mute == MuteType.Full) counts["dead"]++;
        if (sound.Harmonic != HarmonicType.None) counts["harmonic"]++;
        if (sound.Vibrato) counts["vibrato"]++;
        if (sound.Tremolo) counts["tremolo"]++;
        if (sound.BassPicking != BassPickingType.None) counts["slap-pop"]++;
    }

    private static void CountNote(XElement note, bool tremolo, bool beatSlapPop, int plays, Dictionary<string, int> counts)
    {
        // A tie destination is merged into its origin by the importer: not a sound of its own.
        if (note.Element("Tie")?.Attribute("destination")?.Value == "true") return;
        var properties = note.Element("Properties")?.Elements("Property")
            .Select(p => (Name: p.Attribute("name")?.Value ?? "", Element: p))
            .ToList() ?? [];
        bool Has(string name) => properties.Any(p => p.Name == name);

        counts["notes"] += plays;
        if (Has("Bended")) counts["bend"] += plays;
        var slideFlags = properties.Where(p => p.Name == "Slide")
            .Select(p => int.TryParse(p.Element.Element("Flags")?.Value, CultureInfo.InvariantCulture, out var f) ? f : 0)
            .FirstOrDefault();
        if ((slideFlags & 0x0F) != 0) counts["slide"] += plays;
        if ((slideFlags & 0x30) != 0 && (slideFlags & 0x0F) == 0) counts["slide-in"] += plays;
        if (Has("LeftHandTapped") || (Has("HopoDestination") && !Has("Tapped"))) counts["hopo"] += plays;
        else if (Has("Tapped")) counts["tap"] += plays;
        if (Has("PalmMuted")) counts["palm-mute"] += plays;
        else if (Has("Muted")) counts["dead"] += plays;
        if (Has("HarmonicType") || Has("HarmonicFret")) counts["harmonic"] += plays;
        if (note.Element("Vibrato") != null || Has("Vibrato")) counts["vibrato"] += plays;
        if (tremolo) counts["tremolo"] += plays;
        if (beatSlapPop || Has("Slapped") || Has("Popped")) counts["slap-pop"] += plays;
    }

    private static Dictionary<string, int> NewCounts() => Techniques.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);

    private static Dictionary<int, XElement> IdMap(XElement? parent, string name)
    {
        var map = new Dictionary<int, XElement>();
        if (parent == null) return map;
        foreach (var element in parent.Elements(name))
        {
            if (int.TryParse(element.Attribute("id")?.Value, CultureInfo.InvariantCulture, out var id))
                map.TryAdd(id, element);
        }

        return map;
    }

    private static List<int> Ids(string? text)
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return ids;
        foreach (var part in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            ids.Add(int.TryParse(part, CultureInfo.InvariantCulture, out var id) ? id : -1);
        return ids;
    }
}

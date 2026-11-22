using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Parsing.Xml.Vocals;

/// <summary>
/// Reads an RS-XML <c>&lt;vocals&gt;</c> document into a <see cref="ChartProject"/>.
/// </summary>
internal sealed class RsXmlVocalsReader
{
    public ChartProject Read(string filePath, IList<BeatMarker>? externalBeats = null)
    {
        var doc = SafeXml.LoadFromFile(filePath);
        return ReadDocument(doc, filePath, externalBeats);
    }

    public ChartProject ReadDocument(XDocument doc, string filePath)
        => ReadDocument(doc, filePath, externalBeats: null);

    public ChartProject ReadDocument(XDocument doc, string filePath, IList<BeatMarker>? externalBeats)
    {
        var project = new ChartProject();
        project.Chart.SongName = Path.GetFileNameWithoutExtension(filePath);

        var root = doc.Root;
        if (root == null) return project;

        // A standalone vocals file has no tempo map. Give it a neutral seconds
        // grid so its absolute timestamps survive editing and saving. Imports
        // into an existing chart instead use that chart's grid.
        if (externalBeats == null || externalBeats.Count == 0)
        {
            project.Bpm = 60;
            project.Beats.Add(new BeatMarker { Time = 0, Bpm = 60, IsMeasure = true });
        }

        var beats = externalBeats is { Count: > 0 } ? externalBeats : project.Beats;

        var track = new VocalTrack { Name = "Vocals" };
        project.VocalTracks.Add(track);

        // The lyric attribute carries the structure (`-` joins the next syllable,
        // `+` ends the phrase); the shared builder strips it and splits the lines
        // exactly as the psarc path does.
        var syllables = root.Elements("vocal").Select(vEl =>
        {
            var timeMs = Fmt.ParseTime(vEl.Attribute("time")?.Value);
            var lengthMs = Fmt.ParseTime(vEl.Attribute("length")?.Value);
            return new RsVocalSyllable(timeMs, timeMs + lengthMs,
                vEl.Attribute("lyric")?.Value ?? "", Fmt.AttrInt(vEl, "note", 255));
        });

        var phrases = RsVocalPhraseBuilder.Build(syllables, ms => TimelineMath.PositionFromImportedMs(ms, beats));
        foreach (var phrase in phrases) track.Phrases.Add(phrase);

        return project;
    }
}

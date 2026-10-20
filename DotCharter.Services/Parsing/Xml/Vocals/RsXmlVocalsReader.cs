using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Parsing.Xml.Vocals;

/// <summary>
/// Reads an RS-XML <c>&lt;vocals&gt;</c> document into a <see cref="ChartProject"/>.
/// </summary>
internal sealed class RsXmlVocalsReader
{
    public ChartProject Read(string filePath)
    {
        var doc = SafeXml.LoadFromFile(filePath);
        return ReadDocument(doc, filePath);
    }

    public ChartProject ReadDocument(XDocument doc, string filePath)
        => ReadDocument(doc, filePath, externalBeats: null);

    public ChartProject ReadDocument(XDocument doc, string filePath, IList<BeatMarker>? externalBeats)
    {
        var project = new ChartProject();
        project.Chart.SongName = Path.GetFileNameWithoutExtension(filePath);

        var root = doc.Root;
        if (root == null) return project;

        // When external beats are provided, position vocals against them so the
        // result can be merged into a host project. Otherwise vocals collapse
        // to position-zero (legacy behaviour: vocals XML has no beat map).
        var beats = externalBeats ?? project.Beats;

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

        var phrases = RsVocalPhraseBuilder.Build(syllables, ms => beats.Count > 0
            ? TimelineMath.PositionFromImportedMs(ms, beats)
            : FractionalPosition.Zero);
        foreach (var phrase in phrases) track.Phrases.Add(phrase);

        return project;
    }
}

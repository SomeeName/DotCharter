using System.Globalization;
using System.Xml.Linq;
using ChartProject = DotCharter.Core.Domain.Project.ChartProject;
using LyricEvent = DotCharter.Core.Domain.Vocals.LyricEvent;
using LyricPhrase = DotCharter.Core.Domain.Vocals.LyricPhrase;
using VocalTrack = DotCharter.Core.Domain.Vocals.VocalTrack;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Parses vocal tracks and lyric data from RSCP chart files
/// </summary>
public class RscpVocalsParser
{
    public void ParseVocals(XDocument doc, ChartProject project)
    {
        var vocalPathsNodes = doc.Root?.Element("vocalPaths")?.Elements("vocals").ToList();

        if (vocalPathsNodes == null || !vocalPathsNodes.Any())
        {
            var allVocalNodes = doc.Descendants("vocal").Where(v => v.Attribute("p") != null).ToList();
            if (allVocalNodes.Any())
            {
                var groupedVocals = allVocalNodes.GroupBy(v => v.Parent);
                var count = 1;
                foreach (var group in groupedVocals)
                {
                    var parentName = group.Key?.Name.LocalName ?? "Vocals";
                    if (string.Equals(parentName, "vocals", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(parentName, "vocalpath", StringComparison.OrdinalIgnoreCase))
                    {
                        var nameNode = group.Key?.Parent?.Element("name") ?? group.Key?.Element("name");
                        if (nameNode != null)
                            parentName = nameNode.Value;
                        else
                            parentName = count == 1 ? "Vocals" : $"Vocals {count}";
                    }

                    var vt = new VocalTrack { Name = parentName };
                    ParseLyricEvents(group.ToList(), vt);
                    project.VocalTracks.Add(vt);
                    count++;
                }
            }

            return;
        }

        foreach (var vp in vocalPathsNodes)
        {
            var nameVal = vp.Element("name")?.Value;
            if (string.IsNullOrWhiteSpace(nameVal)) nameVal = $"Vocals {project.VocalTracks.Count + 1}";
            var vt = new VocalTrack { Name = nameVal };
            ParseVocalColor(vp, vt);

            var innerVocalsNode = vp.Element("vocals");
            if (innerVocalsNode != null)
            {
                var vocalNodes = innerVocalsNode.Elements("vocal").Where(v => v.Attribute("p") != null).ToList();
                ParseLyricEvents(vocalNodes, vt);
            }

            ApplyExplicitPhraseSpans(vp, vt);

            project.VocalTracks.Add(vt);
        }
    }

    private void ParseVocalColor(XElement vp, VocalTrack vt)
    {
        var colorNode = vp.Element("color");
        if (colorNode == null) return;

        if (byte.TryParse(colorNode.Element("red")?.Value, CultureInfo.InvariantCulture, out var r)) vt.ColorR = r;
        if (byte.TryParse(colorNode.Element("green")?.Value, CultureInfo.InvariantCulture, out var g)) vt.ColorG = g;
        if (byte.TryParse(colorNode.Element("blue")?.Value, CultureInfo.InvariantCulture, out var b)) vt.ColorB = b;
        if (byte.TryParse(colorNode.Element("alpha")?.Value, CultureInfo.InvariantCulture, out var a)) vt.ColorA = a;
    }

    /// <summary>
    ///     Re-applies the additive <c>dcPhrases</c> node written by
    ///     <c>RscpWriter.BuildVocalPhrasesNode</c>. The rscharter wire has no phrase
    ///     element, so <see cref="ParseLyricEvents" /> DERIVES each phrase's span from
    ///     its first/last lyric and cannot represent an empty phrase at all; when the
    ///     writer found a phrase the derivation would damage it recorded every phrase
    ///     of the track, in order, with its lyric COUNT so the empty ones can be put
    ///     back in the right place. Absent — every chart written before the node
    ///     existed, and every chart whose phrases derive exactly — the derivation
    ///     stands untouched.
    /// </summary>
    private void ApplyExplicitPhraseSpans(XElement vp, VocalTrack track)
    {
        var entries = vp.Element("dcPhrases")?.Elements("phrase").ToList();
        if (entries == null || entries.Count == 0) return;

        var derived = track.Phrases.ToList();
        track.Phrases.Clear();

        if (TryRegroupByLyricCounts(entries, derived, track)) return;

        var next = 0;
        foreach (var entry in entries)
        {
            var lyricCount = int.TryParse(entry.Attribute("n")?.Value, CultureInfo.InvariantCulture, out var n) ? n : -1;

            LyricPhrase phrase;
            if (lyricCount == 0)
            {
                phrase = new LyricPhrase();
            }
            else if (next < derived.Count)
            {
                phrase = derived[next++];
            }
            else
            {
                // The node names more lyric-bearing phrases than the vocal entries
                // produced (a truncated or foreign file). Keep what actually parsed
                // rather than invent phrases with no lyrics in them.
                break;
            }

            if (FractionalPosition.TryParse(entry.Attribute("p")?.Value, out var p)) phrase.Position = p;
            if (FractionalPosition.TryParse(entry.Attribute("ep")?.Value, out var ep)) phrase.EndPosition = ep;
            track.Phrases.Add(phrase);
        }

        // Anything the node did not account for still belongs to the track.
        for (; next < derived.Count; next++) track.Phrases.Add(derived[next]);
    }

    /// <summary>
    ///     Rebuilds the phrases from the node's lyric COUNTS rather than from the
    ///     PHRASE_END-derived ones. A lyric flagged <c>IsPhraseEnd</c> in the middle of
    ///     its phrase is written as PHRASE_END, so the derivation split that phrase in
    /// two and every later entry landed on the wrong phrase. The counts are
    ///     what the writer recorded per phrase; they are only trusted when every entry
    ///     has one and they add up to exactly the lyrics that parsed — otherwise (a
    ///     truncated or foreign file) the one-entry-per-derived-phrase mapping stands.
    /// </summary>
    private static bool TryRegroupByLyricCounts(List<XElement> entries, List<LyricPhrase> derived, VocalTrack track)
    {
        var counts = new List<int>(entries.Count);
        foreach (var entry in entries)
        {
            if (!int.TryParse(entry.Attribute("n")?.Value, CultureInfo.InvariantCulture, out var n) || n < 0)
                return false;
            counts.Add(n);
        }

        var lyrics = derived.SelectMany(p => p.Lyrics).ToList();
        if (counts.Sum() != lyrics.Count) return false;

        var next = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var phrase = new LyricPhrase();
            for (var k = 0; k < counts[i]; k++) phrase.Lyrics.Add(lyrics[next++]);
            if (phrase.Lyrics.Count > 0)
            {
                phrase.Position = phrase.Lyrics[0].Position;
                phrase.EndPosition = phrase.Lyrics[^1].EndPosition;
            }

            if (FractionalPosition.TryParse(entries[i].Attribute("p")?.Value, out var p)) phrase.Position = p;
            if (FractionalPosition.TryParse(entries[i].Attribute("ep")?.Value, out var ep)) phrase.EndPosition = ep;
            track.Phrases.Add(phrase);
        }

        return true;
    }

    private void ParseLyricEvents(List<XElement> vocalNodes, VocalTrack track)
    {
        LyricPhrase? currentPhrase = null;
        foreach (var vocalNode in vocalNodes)
        {
            var t = RscpParsingUtilities.ParseFraction(vocalNode.Attribute("p")?.Value);
            var ep = RscpParsingUtilities.ParseFraction(vocalNode.Attribute("ep")?.Value);
            if (ep <= t) ep = t + new Fraction(1, 8);

            var textAttr = vocalNode.Attribute("lyric") ?? vocalNode.Attribute("text");
            var text = textAttr?.Value ?? "";
            var flag = vocalNode.Attribute("flag")?.Value ?? "";

            var isWordPart = flag == "WORD_PART" || text.EndsWith('-');
            // The legacy `flag` attribute holds at most ONE of the two states, and
            // WORD_PART wins it; the additive dcPhraseEnd attribute is what a word-part
            // syllable that also ends its phrase writes (RscpWriter.BuildVocalLyricsNode).
            // Read alongside the legacy value so older files keep working.
            var isPhraseEnd = flag == "PHRASE_END" ||
                              string.Equals(vocalNode.Attribute("dcPhraseEnd")?.Value, "true",
                                  StringComparison.OrdinalIgnoreCase);

            if (text.EndsWith('-')) text = text[..^1];
            currentPhrase ??= new LyricPhrase { Position = t };

            // A node that CARRIES a text attribute is a syllable, even when that text
            // is empty — `RscpWriter.BuildVocalLyricsNode` emits one `<vocal>` per
            // `LyricEvent` unconditionally, and dropping the empty ones DELETED them on
            // every save: the syllable, its Position/EndPosition and its Note were
            // gone, and the phrase's lyric count changed, which then desynchronised the
            // `n` counts `ApplyExplicitPhraseSpans` reads to put the spans back. It is
            // reachable straight from the inspector — clearing a syllable's text is an
            // accepted, undoable edit (`RenameLyricAction` has effect for it). A node
            // with NO text attribute at all is a legacy spacer and is still skipped.
            if (textAttr != null || !string.IsNullOrEmpty(text))
            {
                double noteVal = int.TryParse(vocalNode.Attribute("note")?.Value, CultureInfo.InvariantCulture, out var nv) ? nv : 255;
                currentPhrase.Lyrics.Add(new LyricEvent
                { Position = t, EndPosition = ep, Text = text, IsWordPart = isWordPart, IsPhraseEnd = isPhraseEnd, Note = (int)noteVal });
            }

            if (isPhraseEnd)
            {
                currentPhrase.EndPosition = ep;
                track.Phrases.Add(currentPhrase);
                currentPhrase = null;
            }
        }

        if (currentPhrase != null && currentPhrase.Lyrics.Count > 0)
        {
            currentPhrase.EndPosition = currentPhrase.Lyrics.Last().EndPosition;
            track.Phrases.Add(currentPhrase);
        }
    }
}

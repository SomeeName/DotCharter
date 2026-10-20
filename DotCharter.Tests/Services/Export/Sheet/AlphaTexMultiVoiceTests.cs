using AlphaTab;
using AlphaTab.Importer;
using AlphaTab.Model;
using DotCharter.Services.Export.Sheet;
using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Multi-voice alphaTex emission: an arrangement carrying notation sidecar
/// voices (GP voices 2–4) renders every voice on the one staff. Contract with
/// the bundled AlphaTab 1.8 parser (probe-verified): in multi-voice output
/// EVERY voice — the first included — is prefixed with <c>\voice</c> and lists
/// all its bars in sequence; without the leading marker a single-bar first
/// voice makes the parser swallow voice 2 as a second bar. Single-voice charts
/// must emit no <c>\voice</c> token at all (output byte-identical to before).
/// </summary>
[Collection("AlphaTab")]
public class AlphaTexMultiVoiceTests
{
    private static (ChartProject project, Arrangement arr, ChartLevel level, List<BeatMarker> beats) Fixture(int bars)
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < bars * 4; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });

        return (project, arr, level, beats);
    }

    private static Score Parse(string tex)
    {
        var importer = new AlphaTexImporter();
        importer.InitFromString(tex, new Settings());
        return importer.ReadScore();
    }

    private static void AddQuarterRun(ChartLevel level, int bar, int stringIndex, int fret)
    {
        for (var i = 0; i < 4; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(bar * 4 + i),
                StringIndex = stringIndex,
                Fret = fret
            });
        }
    }

    [Fact]
    public void SidecarVoice_RendersAsSecondVoice_OnTheSameStaff()
    {
        var (project, arr, level, beats) = Fixture(2);
        AddQuarterRun(level, 0, 2, 5);
        AddQuarterRun(level, 1, 2, 7);

        var voice = new NotationVoice { VoiceIndex = 1 };
        // Bar 0: two authored halves on the low E; bar 1: empty (rests).
        voice.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 3,
            NotationDuration = new Fraction(2, 1)
        });
        voice.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 5,
            NotationDuration = new Fraction(2, 1)
        });
        arr.NotationVoices.Add(voice);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var staff = Parse(tex).Tracks[0].Staves[0];
        Assert.Equal(2, staff.Bars.Count);

        foreach (var bar in staff.Bars)
            Assert.Equal(2, bar.Voices.Count);

        // Voice 0 = the chart: 4 quarters per bar.
        Assert.Equal(4, staff.Bars[0].Voices[0].Beats.Count);
        Assert.All(staff.Bars[0].Voices[0].Beats, b => Assert.Equal(Duration.Quarter, b.Duration));

        // Voice 1 = the sidecar: two halves in bar 0 with the authored frets…
        var v1Bar0 = staff.Bars[0].Voices[1].Beats;
        Assert.Equal(2, v1Bar0.Count);
        Assert.All(v1Bar0, b => Assert.Equal(Duration.Half, b.Duration));
        Assert.Equal(3.0, v1Bar0[0].Notes[0].Fret);
        Assert.Equal(5.0, v1Bar0[1].Notes[0].Fret);
        Assert.Equal(1.0, v1Bar0[0].Notes[0].String); // model String 1 = LOWEST = domain 0 (low E)

        // …and bar 1 filled with rests, never truncated or spilled.
        Assert.All(staff.Bars[1].Voices[1].Beats, b => Assert.True(b.IsRest));
    }

    [Fact]
    public void ThreeVoices_AllRender()
    {
        var (project, arr, level, beats) = Fixture(1);
        AddQuarterRun(level, 0, 2, 5);
        foreach (var (vi, fret) in new[] { (1, 3), (2, 8) })
        {
            var v = new NotationVoice { VoiceIndex = vi };
            v.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(0), StringIndex = 0, Fret = fret,
                NotationDuration = new Fraction(4, 1)
            });
            arr.NotationVoices.Add(v);
        }

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var bar = Parse(tex).Tracks[0].Staves[0].Bars[0];
        Assert.Equal(3, bar.Voices.Count);
        Assert.Equal(Duration.Whole, bar.Voices[1].Beats[0].Duration);
        Assert.Equal(3.0, bar.Voices[1].Beats[0].Notes[0].Fret);
        Assert.Equal(8.0, bar.Voices[2].Beats[0].Notes[0].Fret);
    }

    [Fact]
    public void SidecarChord_RendersWithItsNotes_ButNoChordNameLabel()
    {
        var (project, arr, level, beats) = Fixture(1);
        AddQuarterRun(level, 0, 2, 5);

        var voice = new NotationVoice { VoiceIndex = 1 };
        var chord = new Chord { Position = new FractionalPosition(0), NotationDuration = new Fraction(4, 1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        voice.Chords.Add(chord);
        arr.NotationVoices.Add(voice);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        // Sidecar voices never print chord-name labels (harmony is annotated
        // once, by the chart voice).
        var voice2Part = tex[tex.IndexOf("\\voice", tex.IndexOf("\\voice", StringComparison.Ordinal) + 1)..];
        Assert.DoesNotContain("ch \"", voice2Part);

        var bar = Parse(tex).Tracks[0].Staves[0].Bars[0];
        Assert.Equal(2, bar.Voices[1].Beats[0].Notes.Count);
    }

    [Fact]
    public void SingleVoiceChart_EmitsNoVoiceToken()
    {
        var (project, arr, level, beats) = Fixture(1);
        AddQuarterRun(level, 0, 2, 5);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        // The single-voice shape is the pre-multi-voice output — no \voice
        // marker anywhere (old charts keep rendering byte-identically).
        Assert.DoesNotContain("\\voice", tex);
        Assert.Single(Parse(tex).Tracks[0].Staves[0].Bars[0].Voices, v => !v.IsEmpty);
    }

    [Fact]
    public void BeatMap_CoversOnlyTheChartVoice()
    {
        var (project, arr, level, beats) = Fixture(1);
        AddQuarterRun(level, 0, 2, 5);
        var voice = new NotationVoice { VoiceIndex = 1 };
        voice.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 3,
            NotationDuration = new Fraction(4, 1)
        });
        arr.NotationVoices.Add(voice);

        var export = AlphaTexExporter.Export(project, arr, level, beats);

        // 4 chart quarters — the sidecar whole note contributes NO entries
        // (it is not a click target; the renderer filters bounds to match).
        Assert.Equal(4, export.BeatMap.Count);
        Assert.All(export.BeatMap, b => Assert.All(b.Notes, n => Assert.Equal(2, n.StringIndex)));

        // Emission order per bar is exactly the parse order of voice 0.
        var parsedV0 = Parse(export.AlphaTex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;
        Assert.Equal(parsedV0.Count, export.BeatMap.Count);
    }

    [Fact]
    public void BarMetadata_StaysWithTheFirstVoiceOnly()
    {
        var (project, arr, level, beats) = Fixture(1);
        AddQuarterRun(level, 0, 2, 5);
        var voice = new NotationVoice { VoiceIndex = 1 };
        voice.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 3,
            NotationDuration = new Fraction(4, 1)
        });
        arr.NotationVoices.Add(voice);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        // \ts and \tempo are master-bar state: exactly one occurrence each —
        // re-emitting them per voice doubles every tempo marker on the sheet.
        Assert.Single(SplitOccurrences(tex, "\\ts"));
        Assert.Single(SplitOccurrences(tex, "\\tempo"));

        var master = Parse(tex).MasterBars[0];
        Assert.Equal(4.0, master.TimeSignatureNumerator);
        Assert.Equal(4.0, master.TimeSignatureDenominator);
    }

    private static IEnumerable<int> SplitOccurrences(string text, string token)
    {
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            yield return i;
        }
    }
}

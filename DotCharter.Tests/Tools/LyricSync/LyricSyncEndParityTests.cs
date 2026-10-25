using DotCharter.Analysis.Lyrics;
using DotCharter.LyricSync;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Tools.LyricSync;

/// <summary>
///     The CLI and the app end a sheet's syllables the same way: given the same aligned
///     spans and the same vocal envelope, <c>LyricSyncCommand.SpanEnds</c> and
///     <c>LyricAutoSyncService.BuildPhrases</c> put every end where the voice stops. Where
///     the voice decides, neither the CLI's fixed gap nor the app's beat-grid gap does.
/// </summary>
public sealed class LyricSyncEndParityTests
{
    private const double Hop = 0.010;

    /// <summary>120 BPM: one beat every 500 ms.</summary>
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static VocalEnvelope Voiced(double seconds, params (double From, double To)[] voiced)
    {
        var levels = new float[(int)Math.Round(seconds / Hop)];
        for (var k = 0; k < levels.Length; k++)
        {
            var t = k * Hop;
            if (voiced.Any(v => t >= v.From - 1e-9 && t < v.To - 1e-9)) levels[k] = 0.5f;
        }

        return new VocalEnvelope(levels, Hop);
    }

    private static readonly (double Start, double End)[] Spans = [(1.0, 1.05), (2.0, 2.1), (3.0, 3.1), (3.6, 3.65)];

    private static double[] AppEnds(VocalEnvelope? envelope)
    {
        var sheet = LyricSheetParser.Parse("i-tsu\nka-ze");
        var aligned = Spans.Select((t, i) => new AlignedSyllable(i, t.Start, t.End, 0.9, IsInterpolated: false)).ToList();
        var phrases = LyricAutoSyncService.BuildPhrases(sheet, aligned, Beats(), LyricAutoSyncOptions.Default, envelope);
        return [.. phrases.SelectMany(p => p.Lyrics).Select(l => l.EndPosition.GetPositionMs(Beats()) / 1000.0)];
    }

    private static double[] CliEnds(VocalEnvelope? envelope)
    {
        var sheet = LyricSheetParser.Parse("i-tsu\nka-ze");
        var syllables = sheet.Syllables
            .Select((s, i) => new LyricSyncSyllable(i, s.Text, s.ContinuesWord, s.EndsLine, s.LineIndex, Spans[i].Start, Spans[i].End, 0.9, false))
            .ToList();
        return [.. LyricSyncCommand.SpanEnds(syllables, spanWordGap: true, envelope).Select(s => s.EndSeconds)];
    }

    [Fact]
    public void WithAStem_TheCliAndTheAppEndEverySyllableWhereTheVoiceStops()
    {
        // "i" breathes before "tsu"; "tsu" holds to 2.6 s; "ka" stops at 3.3 s; "ze" fades at 4.2 s.
        var envelope = Voiced(6.0, (1.0, 1.4), (2.0, 2.6), (3.0, 3.3), (3.6, 4.2));

        var app = AppEnds(envelope);
        var cli = CliEnds(envelope);

        Assert.Equal([1.4, 2.6, 3.3, 4.2], cli.Select(e => Math.Round(e, 2)));
        Assert.Equal(app.Length, cli.Length);
        for (var i = 0; i < app.Length; i++)
            Assert.True(Math.Abs(app[i] - cli[i]) < 0.002, $"syllable {i}: app {app[i]:0.000} s, CLI {cli[i]:0.000} s");
    }

    [Fact]
    public void WithoutAStem_TheCliKeepsItsFixedGapSpans()
    {
        var cli = CliEnds(null);

        Assert.Equal(2.0 - LyricSyncCommand.MinGapSeconds, cli[0], 6);
        Assert.Equal(2.1, cli[1], 6);
        Assert.Equal(3.6 - LyricSyncCommand.MinGapSeconds, cli[2], 6);
        Assert.Equal(3.65, cli[3], 6);
    }
}

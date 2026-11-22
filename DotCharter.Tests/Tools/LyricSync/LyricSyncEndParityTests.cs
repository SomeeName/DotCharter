using DotCharter.Analysis.Lyrics;
using DotCharter.LyricSync;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Tools.LyricSync;

/// <summary>
///     The CLI runs the app's own fold (<see cref="LyricSyncSpacing" />): on the same beat
///     grid every end matches the app's, and with no grid only the gap differs (a fixed 30 ms).
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

    private static LyricSyncResult Result(VocalEnvelope? envelope)
    {
        var sheet = LyricSheetParser.Parse("i-tsu\nka-ze");
        var aligned = Spans.Select((t, i) => new AlignedSyllable(i, t.Start, t.End, 0.9, IsInterpolated: false)).ToList();
        var syllables = sheet.Syllables
            .Select((s, i) => new LyricSyncSyllable(i, s.Text, s.ContinuesWord, s.EndsLine, s.LineIndex, Spans[i].Start, Spans[i].End, 0.9, false))
            .ToList();
        return new LyricSyncResult(true, null, sheet, aligned, syllables, 0, 0, "vocal stem", "v.wav", "m", false, 0, 6.0, [], envelope);
    }

    private static double[] CliEnds(VocalEnvelope? envelope, LyricSyncBeatGrid grid) =>
    [
        .. LyricSyncSpacing.Apply(Result(envelope), grid,
            LyricSyncCommand.SpacingOptions(new LyricSyncArguments("a", "b") { SpanWordGap = true })).Select(s => s.EndSeconds)
    ];

    [Fact]
    public void OnTheSameGrid_TheAppAndCliEndEverySyllableAlike()
    {
        // "i" breathes before "tsu"; "tsu" holds to 2.6 s; "ka" stops at 3.3 s; "ze" fades at 4.2 s.
        var envelope = Voiced(6.0, (1.0, 1.4), (2.0, 2.6), (3.0, 3.3), (3.6, 4.2));

        var app = AppEnds(envelope);
        var cli = CliEnds(envelope, new LyricSyncBeatGrid(Beats(), LyricSyncGridSource.Tempo));

        Assert.Equal(app, cli);
        Assert.Equal(2.0 - 0.03125, app[0], 6);
        var beats = Beats();
        var nextAlignedStart = TimelineMath.PositionFromMs(3600, beats).GetPositionMs(beats) / 1000.0;
        Assert.Equal(nextAlignedStart - 0.03125, app[2], 6);
        Assert.Equal([2.6, 4.2], new[] { cli[1], cli[3] }.Select(e => Math.Round(e, 2)));
    }

    [Fact]
    public void WithoutAGrid_TheCliKeepsTheMeasuredPhraseEndsAndTheFixedGap()
    {
        var envelope = Voiced(6.0, (1.0, 1.4), (2.0, 2.6), (3.0, 3.3), (3.6, 4.2));
        var gap = LyricSyncSpacing.FallbackGapMilliseconds / 1000.0;

        var cli = CliEnds(envelope, LyricSyncBeatGrid.None);
        var plain = CliEnds(null, LyricSyncBeatGrid.None);

        Assert.Equal(2.0 - gap, cli[0], 4);
        Assert.Equal(2.6, cli[1], 2);
        Assert.Equal(3.6 - gap, cli[2], 4);
        Assert.Equal(4.2, cli[3], 2);
        Assert.Equal(2.1, plain[1], 4);
        Assert.Equal(3.65, plain[3], 4);
    }
}

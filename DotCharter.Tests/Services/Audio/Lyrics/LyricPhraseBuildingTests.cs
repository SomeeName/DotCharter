using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     The fold from aligned seconds up to chart phrases. Pure — no model, no
///     audio — so it pins the part of auto-sync that decides what actually lands
///     in the vocal track.
/// </summary>
public sealed class LyricPhraseBuildingTests
{
    /// <summary>120 BPM: one beat every 500 ms, 32 beats = 16 s.</summary>
    private static List<BeatMarker> Beats(int count = 32) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    /// <summary>The default minimum gap on the 120 BPM grid: measure-fraction
    /// mode, 4 beats over the 64 divisor = 1/16 beat = 31.25 ms.</summary>
    private const double MinGapMs = 31.25;

    private static List<LyricPhrase> Build(string text, params (double Start, double End)[] times)
        => Build(text, LyricAutoSyncOptions.Default, times);

    private static List<LyricPhrase> Build(
        string text, LyricAutoSyncOptions options, params (double Start, double End)[] times)
    {
        var sheet = LyricSheetParser.Parse(text);
        Assert.Equal(times.Length, sheet.Syllables.Count);

        var aligned = times
            .Select((t, i) => new AlignedSyllable(i, t.Start, t.End, 0.9, IsInterpolated: false))
            .ToList();

        return LyricAutoSyncService.BuildPhrases(sheet, aligned, Beats(), options);
    }

    [Fact]
    public void EachLineBecomesItsOwnPhrase()
    {
        var phrases = Build("a b\nc d",
            (1.0, 1.2), (1.5, 1.7), (3.0, 3.2), (3.5, 3.7));

        Assert.Equal(2, phrases.Count);
        Assert.Equal(2, phrases[0].Lyrics.Count);
        Assert.Equal(2, phrases[1].Lyrics.Count);
    }

    /// <summary>
    ///     <b>The hyphen is input syntax and must never reach the stored text.</b>
    ///     In this model a continuation is <c>IsWordPart</c>; the writers append
    ///     the <c>-</c> themselves at export (<c>RsXmlVocalsWriter</c>) or emit a
    ///     <c>WORD_PART</c> flag (<c>RscpWriter</c>), and the Ctrl+Space tap path
    ///     already strips it for exactly this reason. Storing it put a visible
    ///     <c>-</c> on every syllable in the editor — owner-reported on the first
    ///     real sync.
    /// </summary>
    [Fact]
    public void AContinuingSyllableCarriesTheFlagButNotAVisibleHyphen()
    {
        var phrases = Build("tsuk-kon-de", (1.0, 1.2), (1.3, 1.5), (1.6, 1.9));

        Assert.Equal(["tsuk", "kon", "de"], phrases[0].Lyrics.Select(l => l.Text).ToArray());
        Assert.Equal([true, true, false], phrases[0].Lyrics.Select(l => l.IsWordPart).ToArray());
        Assert.True(phrases[0].Lyrics[^1].IsPhraseEnd);
    }

    /// <summary>
    ///     …and no stored syllable anywhere may end in one, whatever the input
    ///     looked like — including Rocksmith's own export form.
    /// </summary>
    [Fact]
    public void NoStoredSyllableEndsInAHyphenOrPlus()
    {
        var phrases = Build("Mi- zu ta- ma- ri de+",
            (1.0, 1.1), (1.2, 1.3), (1.4, 1.5), (1.6, 1.7), (1.8, 1.9), (2.0, 2.1));

        Assert.All(phrases.SelectMany(p => p.Lyrics), l =>
        {
            Assert.DoesNotContain('-', l.Text);
            Assert.DoesNotContain('+', l.Text);
        });
    }

    /// <summary>
    ///     A syllable may not run into the next one. The aligner's frame spans
    ///     never overlap, but the onset bias moves onsets after the fact, so the
    ///     clamp has to exist here too.
    ///     <para>
    ///         Since the spanning rule this is ALSO the floor case: the span
    ///         target (next start − min gap) sits EARLIER than the truncated CTC
    ///         end, and spanning never shortens what was heard — so the end
    ///         stays at the next syllable's start, not at the gapped target.
    ///     </para>
    /// </summary>
    [Fact]
    public void ASyllableIsTruncatedWhereTheNextOneBegins()
    {
        var beats = Beats();
        var phrases = Build("a b", (1.0, 5.0), (2.0, 2.5));

        var first = phrases[0].Lyrics[0];
        Assert.Equal(2000.0, first.EndPosition.GetPositionMs(beats), 1.0);
    }

    /// <summary>
    ///     The CTC end is where the model stopped HEARING the syllable, which
    ///     leaves short pills with dead gaps — a syllable inside a word spans to
    ///     the next syllable's start minus the note-style minimum gap
    ///     (measure-fraction default: 1/16 beat = 31.25 ms at 120 BPM).
    /// </summary>
    [Fact]
    public void ASyllableInsideAWordSpansToTheNextSyllableMinusTheMinimumGap()
    {
        var beats = Beats();
        var phrases = Build("tsuk-kon-de", (1.0, 1.1), (2.0, 2.1), (3.0, 3.1));

        Assert.Equal(2000.0 - MinGapMs, phrases[0].Lyrics[0].EndPosition.GetPositionMs(beats), 1.0);
        Assert.Equal(3000.0 - MinGapMs, phrases[0].Lyrics[1].EndPosition.GetPositionMs(beats), 1.0);
    }

    /// <summary>Word boundary within a phrase: spanned by default
    /// (<see cref="LyricAutoSyncOptions.SpanWordEndToNextWord" /> is ON).</summary>
    [Fact]
    public void TheLastSyllableOfAWordSpansToTheNextWordByDefault()
    {
        var beats = Beats();
        var phrases = Build("a b", (1.0, 1.2), (3.0, 3.2));

        Assert.Equal(3000.0 - MinGapMs, phrases[0].Lyrics[0].EndPosition.GetPositionMs(beats), 1.0);
    }

    [Fact]
    public void WordSpanningOffKeepsTheCtcEndAtAWordBoundary()
    {
        var beats = Beats();
        var options = LyricAutoSyncOptions.Default with { SpanWordEndToNextWord = false };
        var phrases = Build("a b", options, (1.0, 1.2), (3.0, 3.2));

        Assert.Equal(1200.0, phrases[0].Lyrics[0].EndPosition.GetPositionMs(beats), 1.0);
    }

    /// <summary>…but a syllable INSIDE a word still spans with word-boundary
    /// spanning off — the word is sung through; only the boundary is optional.</summary>
    [Fact]
    public void WordSpanningOffStillSpansInsideAWord()
    {
        var beats = Beats();
        var options = LyricAutoSyncOptions.Default with { SpanWordEndToNextWord = false };
        var phrases = Build("tsuk-kon", options, (1.0, 1.1), (2.0, 2.1));

        Assert.Equal(2000.0 - MinGapMs, phrases[0].Lyrics[0].EndPosition.GetPositionMs(beats), 1.0);
    }

    /// <summary>Phrase boundary: NOT spanned by default
    /// (<see cref="LyricAutoSyncOptions.SpanPhraseEndToNextPhrase" /> is OFF) —
    /// a phrase break usually is a breath or an instrumental gap.</summary>
    [Fact]
    public void TheLastSyllableOfAPhraseKeepsTheCtcEndByDefault()
    {
        var beats = Beats();
        var phrases = Build("a\nb", (1.0, 1.2), (3.0, 3.2));

        Assert.Equal(1200.0, phrases[0].Lyrics[0].EndPosition.GetPositionMs(beats), 1.0);
        Assert.Equal(1200.0, phrases[0].EndPosition.GetPositionMs(beats), 1.0);
    }

    [Fact]
    public void PhraseSpanningOnSpansTheLastSyllableIntoTheGapBeforeTheNextPhrase()
    {
        var beats = Beats();
        var options = LyricAutoSyncOptions.Default with { SpanPhraseEndToNextPhrase = true };
        var phrases = Build("a\nb", options, (1.0, 1.2), (3.0, 3.2));

        Assert.Equal(3000.0 - MinGapMs, phrases[0].Lyrics[0].EndPosition.GetPositionMs(beats), 1.0);
        // The phrase's own end follows its spanned last syllable.
        Assert.Equal(3000.0 - MinGapMs, phrases[0].EndPosition.GetPositionMs(beats), 1.0);
    }

    /// <summary>The maximum-hold cap applies to a SPANNED end exactly as it does
    /// to a heard one — a 7 s word gap must not become a 7 s sustain.</summary>
    [Fact]
    public void ASpannedSyllableIsStillCappedAtTheMaximumHold()
    {
        var beats = Beats();
        var phrases = Build("a b", (1.0, 1.2), (8.0, 8.2));

        Assert.Equal(1000.0 + LyricAutoSyncOptions.Default.MaxSyllableSeconds * 1000.0,
            phrases[0].Lyrics[0].EndPosition.GetPositionMs(beats), 1.0);
    }

    /// <summary>The minimum gap follows <c>AppConfig</c>'s note-spacing rules —
    /// milliseconds mode uses the configured distance verbatim.</summary>
    [Fact]
    public void TheMinimumGapFollowsTheMillisecondsSpacingMode()
    {
        var beats = Beats();
        var options = LyricAutoSyncOptions.Default with
        {
            SpacingConfig = new AppConfig
            {
                MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds,
                MinNoteDistanceMs = 50.0
            }
        };
        var phrases = Build("a b", options, (1.0, 1.2), (3.0, 3.2));

        Assert.Equal(2950.0, phrases[0].Lyrics[0].EndPosition.GetPositionMs(beats), 1.0);
    }

    /// <summary>
    ///     A syllable parked over a long instrumental would otherwise be drawn as
    ///     a sustain running the length of the gap.
    /// </summary>
    [Fact]
    public void ASyllableIsCappedAtTheMaximumHoldEvenWithNoFollower()
    {
        var beats = Beats();
        var phrases = Build("a", (1.0, 30.0));

        var only = phrases[0].Lyrics[0];
        Assert.Equal(1000.0 + LyricAutoSyncOptions.Default.MaxSyllableSeconds * 1000.0,
            only.EndPosition.GetPositionMs(beats), 1.0);
    }

    [Fact]
    public void PhrasesSpanFromTheirFirstSyllableToTheirLast()
    {
        var beats = Beats();
        var phrases = Build("a b\nc", (1.0, 1.2), (1.5, 1.8), (3.0, 3.4));

        Assert.Equal(1000.0, phrases[0].Position.GetPositionMs(beats), 1.0);
        Assert.Equal(1800.0, phrases[0].EndPosition.GetPositionMs(beats), 1.0);
        Assert.Equal(3000.0, phrases[1].Position.GetPositionMs(beats), 1.0);
    }

    /// <summary>
    ///     An onset bias can push a syllable before the grid's zero; the position
    ///     conversion must not be handed a negative time.
    /// </summary>
    [Fact]
    public void ASyllablePushedBeforeTheStartOfTheSongIsClampedToZero()
    {
        var beats = Beats();
        var phrases = Build("a", (-5.0, -4.5));

        Assert.Equal(0.0, phrases[0].Lyrics[0].Position.GetPositionMs(beats), 1.0);
    }
}

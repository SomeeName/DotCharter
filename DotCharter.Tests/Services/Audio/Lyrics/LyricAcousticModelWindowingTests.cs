using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     The window stitch that turns a song into overlapping model passes and
///     back into one emission matrix.
///     <para>
///     It shipped reporting <c>FrameCount = samples / 320</c> unconditionally,
///     while wav2vec2's convolutional front end emits <b>n − 1</b> rows for
///     320·n samples — so in the last window <c>skip + k</c> reached
///     <c>produced</c> one row early and the song's FINAL row was never written.
///     The matrix is zero-initialised, and a zero row in a LOG-probability
///     matrix is probability 1.0 for all 31 symbols: strictly cheaper than every
///     real emission, so the CTC trellis takes it for free. That inflates
///     <c>CtcAlignment.TotalLogProb</c> and the last syllable's
///     <c>Confidence</c> whenever its span reaches the last frame — which feeds
///     the user-visible "worth checking" count the low-confidence threshold was
///     calibrated against.
///     </para>
///     <para>
///     Tested here rather than through the model because the model is a 340 MB
///     ONNX graph and cannot run in the default gate. The frame arithmetic is
///     derived from the standard conv config; the off-by-one itself does not
///     depend on it, which is why the cases sweep <c>produced</c> rather than
///     asserting one shape.
///     </para>
/// </summary>
public sealed class LyricAcousticModelWindowingTests
{
    private const int CoreFrames = 1000;
    private const int ContextFrames = 100;

    /// <summary>Songs that straddle every interesting boundary of a 1000-frame
    /// core: shorter than one window, one short of it, exactly it, one past it,
    /// and two and a half of them.</summary>
    public static TheoryData<int> SongLengths => [1, 999, 1000, 1001, 2500];

    /// <summary>What the conv front end really does: 320n samples in, n−1 rows
    /// out.</summary>
    private static int ShortByOne(int windowFrames) => windowFrames - 1;

    [Theory]
    [MemberData(nameof(SongLengths))]
    public void EveryFrameTheStitchReportsWasReallyWritten(int totalFrames)
    {
        var reported = LyricWindowStitch.FramesWritten(totalFrames, CoreFrames, ContextFrames, ShortByOne);

        // Replay the plan and mark what the copies really touch.
        var touched = new bool[totalFrames];
        foreach (var w in LyricWindowStitch.Plan(totalFrames, CoreFrames, ContextFrames))
        {
            var copied = LyricWindowStitch.CopyCount(w, ShortByOne(w.WindowFrames));
            for (var k = 0; k < copied; k++) touched[w.CoreStart + k] = true;
        }

        Assert.InRange(reported, 0, totalFrames);
        for (var f = 0; f < reported; f++)
            Assert.True(touched[f], $"frame {f} of {reported} reported is still all zeros.");
    }

    /// <summary>
    ///     The bug in one line: a pass that is one row short must lower the
    ///     reported count, not be reported as complete. On a 1000-frame song the
    ///     last row is the SONG's last row.
    /// </summary>
    [Theory]
    [MemberData(nameof(SongLengths))]
    public void APassThatComesUpShort_IsNotReportedAsComplete(int totalFrames)
    {
        var honest = LyricWindowStitch.FramesWritten(totalFrames, CoreFrames, ContextFrames, ShortByOne);
        var complete = LyricWindowStitch.FramesWritten(totalFrames, CoreFrames, ContextFrames, n => n);

        Assert.Equal(totalFrames, complete);
        Assert.Equal(totalFrames - 1, honest);
    }

    /// <summary>The stitch is contiguous — the plan may not skip a frame between
    /// two windows, which would leave a zero row in the MIDDLE of the song.</summary>
    [Theory]
    [MemberData(nameof(SongLengths))]
    public void ThePlanCoversTheSongWithNoGapAndNoOverlapOfCores(int totalFrames)
    {
        var next = 0;
        var seen = 0;
        foreach (var w in LyricWindowStitch.Plan(totalFrames, CoreFrames, ContextFrames))
        {
            Assert.Equal(next, w.CoreStart);
            Assert.True(w.Wanted > 0);
            Assert.Equal(w.CoreStart - w.WindowStart, w.Skip);
            Assert.True(w.Skip + w.Wanted <= w.WindowFrames,
                "the window is not long enough to contain the rows it is responsible for.");
            next += w.Wanted;
            seen++;
        }

        Assert.Equal(totalFrames, next);
        Assert.Equal((totalFrames + CoreFrames - 1) / CoreFrames, seen);
    }

    /// <summary>A pass that fails outright writes nothing rather than reporting
    /// a matrix of zeros as real, and one that stops mid-song is reported as the
    /// contiguous prefix — never as the whole song with a hole in it.</summary>
    [Fact]
    public void AWindowThatProducesNothing_ReportsNoFrames()
    {
        Assert.Equal(0, LyricWindowStitch.FramesWritten(2500, CoreFrames, ContextFrames, _ => 0));

        // The FIRST window has no leading context, so 500 rows really are 500
        // song frames — and the second window then contributes none, which must
        // stop the count rather than jump past the gap to the third.
        var stopsAfterOne = 0;
        Assert.Equal(500, LyricWindowStitch.FramesWritten(2500, CoreFrames, ContextFrames,
            _ => stopsAfterOne++ == 0 ? 500 : 0));
    }

    /// <summary>The first window has no leading context, so its skip is 0 — if
    /// it were not, frame 0 itself would go missing.</summary>
    [Fact]
    public void TheFirstWindowHasNoLeadingContext()
    {
        var first = LyricWindowStitch.Plan(2500, CoreFrames, ContextFrames).First();

        Assert.Equal(0, first.CoreStart);
        Assert.Equal(0, first.WindowStart);
        Assert.Equal(0, first.Skip);
        Assert.Equal(CoreFrames + ContextFrames, first.WindowFrames);
    }
}

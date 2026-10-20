namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     Which of a sliding window's own output rows belong to the song, and where
///     they land in the song-wide emission matrix.
///     <para>
///     A whole song does not fit through the model at once, so it is encoded in
///     overlapping windows: <c>CoreFrames</c> the window is responsible for, with
///     <c>ContextFrames</c> of neighbouring audio on each side so the frames at
///     the seams are conditioned on what surrounds them. The context rows are
///     then thrown away and the core rows are stitched together.
///     </para>
///     <para>
///     Pure and separate because that arithmetic is off-by-one-prone in exactly
///     the way that does not throw, and the thing it drives is a 340 MB ONNX
///     graph that cannot run in the default gate. The off-by-one it was
///     extracted for: wav2vec2's convolutional front end emits <b>n − 1</b>
///     frames for 320·n samples, so the last window produces one row fewer than
///     the plan asks for, and the song's final row was left as allocated — all
///     zeros. In a LOG-probability matrix that is probability 1.0 for every
///     symbol, i.e. the cheapest frame the CTC trellis can find.
///     </para>
/// </summary>
internal static class LyricWindowStitch
{
    /// <param name="CoreStart">First song frame this window is responsible for.</param>
    /// <param name="WindowStart">First song frame fed to the model (core start minus its context).</param>
    /// <param name="WindowFrames">Frames fed to the model: leading context + core + trailing context.</param>
    /// <param name="Skip">
    ///     Rows at the head of this window's OUTPUT that are leading context and
    ///     belong to the previous window.
    /// </param>
    /// <param name="Wanted">Rows this window is responsible for (its core, clipped at the song's end).</param>
    internal readonly record struct Window(
        int CoreStart, int WindowStart, int WindowFrames, int Skip, int Wanted);

    /// <summary>The window plan for a song of <paramref name="totalFrames" /> frames.</summary>
    internal static IEnumerable<Window> Plan(int totalFrames, int coreFrames, int contextFrames)
    {
        if (totalFrames <= 0 || coreFrames <= 0) yield break;

        for (var coreStart = 0; coreStart < totalFrames; coreStart += coreFrames)
        {
            var windowStart = Math.Max(0, coreStart - contextFrames);
            var windowEnd = Math.Min(totalFrames, coreStart + coreFrames + contextFrames);
            var windowFrames = windowEnd - windowStart;
            if (windowFrames < 1) yield break;

            yield return new Window(
                coreStart, windowStart, windowFrames,
                Skip: coreStart - windowStart,
                Wanted: Math.Min(coreFrames, totalFrames - coreStart));
        }
    }

    /// <summary>
    ///     How many of <paramref name="window" />'s wanted rows the forward pass
    ///     actually delivered. A pass that came up short simply writes fewer —
    ///     it must never be allowed to leave a target row untouched and then be
    ///     counted as if it had filled it.
    /// </summary>
    internal static int CopyCount(in Window window, int produced) =>
        Math.Clamp(produced - window.Skip, 0, window.Wanted);

    /// <summary>
    ///     The song's honest frame count: how many LEADING song frames the stitch
    ///     really writes, given how many rows a forward pass over a window of
    ///     <c>WindowFrames</c> produces. Stops at the first gap — a matrix with a
    ///     hole in it is not usable past the hole, so the count is the contiguous
    ///     prefix rather than the highest index touched.
    /// </summary>
    internal static int FramesWritten(
        int totalFrames, int coreFrames, int contextFrames, Func<int, int> producedForWindowFrames)
    {
        var written = 0;
        foreach (var window in Plan(totalFrames, coreFrames, contextFrames))
        {
            if (window.CoreStart > written) break;
            written = Math.Max(written, window.CoreStart + CopyCount(window, producedForWindowFrames(window.WindowFrames)));
        }

        return written;
    }
}

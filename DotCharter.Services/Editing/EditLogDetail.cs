namespace DotCharter.Services.Editing;

/// <summary>
///     How an editing log line spells out the sounds it acted on.
///     <para>
///         These lines were written for the gesture they read well on — one note,
///         a chord, a handful of dragged sounds — and they are built
///         UNCONDITIONALLY, because <c>IAppLogger.Info</c> takes an
///         already-interpolated <c>string</c>. On a 5,000-sound selection one
///         keystroke therefore formatted 5,000 sounds (each one a
///         <c>GetPositionMs</c> and half a dozen interpolations) before the logger
///         was even asked whether it wanted them: 9.16 MB of garbage per keypress,
///         measured, on the UI thread. Nothing reads past the first few anyway —
///         the COUNTS beside them are what a log reader uses.
///     </para>
///     <para>
///         The same fault also appears one line at a time — a
///         <c>foreach (…) _logger.Info(…)</c> over the selection — which trades one
///         megabyte-long line for thousands of short ones and is no cheaper. Cap
///         those loops at <see cref="Cap" /> as well and write a single
///         "…and N more" tail; <c>HighwayEditingService.LogMovedSounds</c> is the
///         worked example.
///     </para>
/// </summary>
public static class EditLogDetail
{
    /// <summary>
    ///     How many items an editing log line spells out before it summarises the
    ///     rest.
    /// </summary>
    public const int Cap = 20;

    /// <summary>
    ///     Up to <see cref="Cap" /> formatted items, then how many were left out.
    ///     Empty string for an empty list, which is what <c>string.Join</c>
    ///     produced.
    /// </summary>
    public static string Describe<T>(IReadOnlyList<T> items, Func<T, string> format)
    {
        if (items.Count == 0) return string.Empty;

        var shown = Math.Min(items.Count, Cap);
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < shown; i++)
        {
            if (i > 0) text.Append("; ");
            text.Append(format(items[i]));
        }

        if (items.Count > shown) text.Append($"; …and {items.Count - shown} more");
        return text.ToString();
    }
}

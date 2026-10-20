namespace DotCharter.UI.Input;

/// <summary>
///     Which way the string INDEX runs on screen.
///     <para>
///         The highway lays lane 0 at the top and
///         <c>ChartRenderContext.LaneTopFor</c> maps a string to it as
///         <c>flip ? count - 1 - index : index</c> — so unflipped, string 0 (the
///         LOWEST-pitched) is drawn at the TOP and a bigger index is further
///         down.
///     </para>
///     <para>
///         The arrow keys did not know that. Up ran
///         <c>MoveSelectedStringUp</c>, which INCREMENTS the index — so the note
///         went down the screen, and down went up. Reported as "arrow keys seem
///         inverted when I want to move note up string it moves it down and vice
///         versa". An arrow means the direction the charter can SEE, which is
///         why this is a function of the flip rather than a constant.
///     </para>
/// </summary>
public static class StringLaneDirection
{
    /// <summary>
    ///     Does one step toward the TOP of the highway INCREASE the string index?
    ///     True only when the lanes are flipped (lefty mode, or the invert-strings
    ///     setting — <c>IChartViewport.FlipStrings</c> is their XOR).
    /// </summary>
    public static bool TowardTopIncreasesIndex(bool flipStrings) => flipStrings;

    /// <summary>
    ///     The index step for one press: <paramref name="towardTop" /> is what the
    ///     arrow says, and this is what the model has to do about it.
    /// </summary>
    public static int IndexStep(bool flipStrings, bool towardTop) =>
        TowardTopIncreasesIndex(flipStrings) == towardTop ? +1 : -1;
}

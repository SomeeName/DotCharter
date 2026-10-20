namespace DotCharter.Services.Export.Sheet;

/// <summary>
///     Everything a sheet render depends on, folded into one comparable value.
///     <para>
///         <c>SheetMusicView.RebuildAsync</c> runs on every committed edit while
///         the strip is open (behind its 250 ms debounce), and a great many of
///         those edits change nothing the notation shows — a technique toggle
///         AlphaTex has no glyph for, a selection-adjacent edit, an undo of a
///         non-structural action. The AlphaTex export has to run either way (it
///         reads the live graph and is the only way to find out), but the
///         AlphaTab layout and the AlphaSkia raster behind it do not: when this
///         key is unchanged the existing chunk bitmaps are already the answer.
///     </para>
///     <para>
///         The geometry belongs in the key as much as the text does. The same
///         method is what a resize, a zoom, a staves change, a page/strip flip
///         and a DPI change all go through, so a key of the AlphaTex ALONE would
///         make the strip stop re-rendering when the pane is dragged wider.
///     </para>
///     <para>
///         Not in the key, deliberately: the app theme. The strip always renders
///         <c>SheetMusicRenderer.InkPalette.Screen</c> — the palette is a
///         parameter of the PDF path, not of this one — so no theme change can
///         alter these bitmaps. If the strip ever takes its ink from the theme,
///         that stamp becomes a field here.
///     </para>
/// </summary>
public readonly record struct SheetRenderKey(
    string AlphaTex,
    double Width,
    double Height,
    double Dpi,
    double Zoom,
    bool PageMode,
    int Staves)
{
    /// <summary>
    ///     Whether a fresh key needs the layout + raster, given what was last
    ///     rendered. Null means nothing has been rendered yet (or the last
    ///     attempt failed), which always renders.
    ///     <para>
    ///         Exact comparison on the doubles, deliberately: this may only ever
    ///         skip a render that would have produced identical pixels, so any
    ///         difference at all falls to the safe side.
    ///     </para>
    /// </summary>
    public static bool ShouldRerender(SheetRenderKey? lastRendered, SheetRenderKey next) =>
        lastRendered is not { } last || !last.Equals(next);
}

using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Application;

/// <summary>
///     The status rail's editing-state readouts (P1-11) — pure string
///     formatting over the grid divisor, the selection total, the timeline
///     zoom and the string-edit mask.
/// </summary>
public sealed class EditStateReadoutsTests
{
    // The English formats, read from the resx itself so this is not a copy of
    // them: sentence case, the figure in the middle or at the end.
    private static readonly string SnapFormat = English("StatusBar_Snap");
    private static readonly string SelectionFormat = English("StatusBar_Selection");
    private static readonly string ZoomFormat = English("StatusBar_Zoom");

    private static string English(string key)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
            dir = Path.GetDirectoryName(dir);
        var resx = File.ReadAllText(Path.Combine(dir!, "DotCharter.Localization", "Resources.resx"));
        var m = System.Text.RegularExpressions.Regex.Match(resx,
            $@"<data name=""{key}"" xml:space=""preserve""><value>([^<]*)</value>");
        Assert.True(m.Success, $"{key} is not in Resources.resx");
        return m.Groups[1].Value;
    }

    /// <summary>
    ///     The rail shows the picker's own label, so it follows it: the divisor,
    ///     spelled as EOF spells it. It read "Snap 1/64T" for divisor 96 until
    ///     0.61 — see <c>GridSnapOptionTests</c> for why that went.
    /// </summary>
    [Theory]
    [InlineData(4, "Snap 1/4")]
    [InlineData(12, "Snap 1/12")]
    [InlineData(24, "Snap 1/24")]
    [InlineData(96, "Snap 1/96")]
    public void Snap_UsesTheGridSnapLabel(int divisor, string expected)
        => Assert.Equal(expected, EditStateReadouts.Snap(divisor, SnapFormat));

    [Fact]
    public void Selection_CountsAllSixObjectClasses()
    {
        var selection = new SelectionService();
        selection.SelectedNotes.Add(new GuitarNote());
        selection.SelectedNotes.Add(new GuitarNote());
        selection.SelectedChords.Add(new Chord());
        selection.SelectedHandShapes.Add(new HandShape());
        selection.SelectedAnchors.Add(new FretAnchor());

        Assert.Equal(5, EditStateReadouts.TotalSelected(selection));
        Assert.Equal("5 selected", EditStateReadouts.Selection(EditStateReadouts.TotalSelected(selection), SelectionFormat));
    }

    [Theory]
    [InlineData(0.2, "Zoom ×1")]     // the UserSettingsState default is ×1
    [InlineData(0.4, "Zoom ×2")]
    [InlineData(0.25, "Zoom ×1.25")]
    public void Zoom_IsTheFactorOverTheDefaultPixelsPerMs(double pixelsPerMs, string expected)
        => Assert.Equal(expected, EditStateReadouts.Zoom(pixelsPerMs, ZoomFormat));

    [Fact]
    public void TheZoomBaseline_IsTheUserSettingsDefault()
    {
        // If UserSettingsState.PixelsPerMs's default ever moves, this readout
        // silently reports the wrong factor — pin the two together.
        Assert.Equal(EditStateReadouts.BaselinePixelsPerMs,
            new DotCharter.Services.State.UserSettingsState().PixelsPerMs);
    }

    private const string MaskFormat = "Mask {0}";
    private const string StringLabelFormat = "{0} ({1})";
    private static readonly string[] StandardTuning = ["E", "A", "D", "G", "B", "e"];

    [Fact]
    public void Mask_IsEmptyWhileNoMaskIsSet()
        => Assert.Equal(string.Empty,
            EditStateReadouts.Mask(StringEditMask.Empty, StandardTuning, MaskFormat, StringLabelFormat));

    [Fact]
    public void Mask_NamesTheStringsTheWayTheInspectorDoes()
        // Model indices 1 and 4 (set via Ctrl+2 / Ctrl+5) are the A and B
        // strings: the guitarist's 5 and 2, as the lane gutter and the
        // inspector's Sound tab print them (#125). It used to print "MASK 2·5".
        => Assert.Equal("Mask A (5), B (2)",
            EditStateReadouts.Mask(StringEditMask.FromStrings(1, 4), StandardTuning, MaskFormat, StringLabelFormat));

    [Fact]
    public void Mask_AgreesWithTheInspectorsReadout()
    {
        // The status bar and the Sound tab's queue strip read the SAME mask;
        // they must name its strings identically (#125).
        var mask = StringEditMask.FromStrings(0, 3);
        var localization = FakeLocalization.English();
        var inspector = GuitarStringLabel.MaskReadout(mask, StandardTuning.Length, StandardTuning, localization);
        var statusBar = EditStateReadouts.Mask(mask, StandardTuning, MaskFormat,
            localization["Inspector_StringLabelFmt"]);

        Assert.EndsWith("E (6), G (3)", inspector);
        Assert.EndsWith("E (6), G (3)", statusBar);
    }
}

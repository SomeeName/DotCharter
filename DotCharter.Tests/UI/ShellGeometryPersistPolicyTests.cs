using System.Text.RegularExpressions;
using DotCharter.UI.Layout;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     A gameplay presentation OVERRIDE may never be written back as the user's
///     editor layout — the rule <see cref="SheetStripHeightPolicy" /> already
///     states for the notation strip, applied to the writer it never covered:
///     the CLOSE-THE-APP path.
///
///     <para>
///         The run collapses the timeline row and the tool dock
///         (<c>MainWindowShellLayout.ApplyGameplayLayout</c>) and closes the
///         Inspector and the Sheet Music pane
///         (<c>MainWindowViewModel.OnIsGameplayPresentationChanged</c>), saving
///         the pre-run values in fields it restores when the run ends.
///         <c>MainWindow.OnClosing</c> never passes through that restore: it
///         calls <c>PersistDockLayout</c> and
///         <c>MainWindowLayoutController.PersistOnClose</c> directly. So quitting
///         from the pause menu measured the PRESENTATION and stored it — the
///         bottom dock at the whole window's height (clamped to half of it on the
///         next restore), the Inspector and notation panes recorded as "Closed",
///         and the notation strip's in-run 240 px cap saved as the user's score
///         height. Permanent, silent, no undo.
///     </para>
/// </summary>
public sealed class ShellGeometryPersistPolicyTests
{
    // ---- the dock row -------------------------------------------------------

    /// <summary>The bug: during a run the dock row owns the entire window (row 0
    /// is Auto and the splitter row is 0), and <c>IsBottomDockVisible</c> is
    /// forced TRUE because the presentation turns the 3D preview on — so the old
    /// "measured above zero and the dock is visible" guard passed and stored the
    /// highway's height as the user's charting split.</summary>
    [Fact]
    public void ADockHeightMeasuredDuringARun_IsNotAPreference()
    {
        Assert.Null(ShellGeometryPersistPolicy.DockHeightToPersistPx(
            980, bottomDockVisible: true, gameplayPresentation: true));
    }

    [Fact]
    public void ADockHeightMeasuredInTheEditor_IsPersistedAsMeasured()
    {
        Assert.Equal(340, ShellGeometryPersistPolicy.DockHeightToPersistPx(
            340, bottomDockVisible: true, gameplayPresentation: false));
    }

    /// <summary>A hidden dock reports 0 — saving that would lose the user's
    /// previous size, which is the rule that was already there.</summary>
    [Theory]
    [InlineData(0d, true)]
    [InlineData(-1d, true)]
    [InlineData(340d, false)]
    public void ACollapsedOrHiddenDock_PersistsNothing(double measured, bool visible)
    {
        Assert.Null(ShellGeometryPersistPolicy.DockHeightToPersistPx(
            measured, visible, gameplayPresentation: false));
    }

    // ---- the inspector column ----------------------------------------------

    [Fact]
    public void AnInspectorWidthMeasuredDuringARun_IsNotAPreference()
    {
        Assert.Null(ShellGeometryPersistPolicy.InspectorWidthToPersistPx(
            420, gameplayPresentation: true));
        Assert.Equal(420, ShellGeometryPersistPolicy.InspectorWidthToPersistPx(
            420, gameplayPresentation: false));
    }

    // ---- the pane snapshot --------------------------------------------------

    /// <summary>The sharpest half: the presentation closed the Inspector and the
    /// Sheet Music pane, so a capture taken during a run says the USER closed
    /// them, and the next launch honours it.</summary>
    [Fact]
    public void ThePaneSnapshotIsNotOverwrittenDuringARun()
    {
        Assert.False(ShellGeometryPersistPolicy.MayPersistDockPanes(gameplayPresentation: true));
        Assert.True(ShellGeometryPersistPolicy.MayPersistDockPanes(gameplayPresentation: false));
    }

    // ---- the round trip the bug destroyed -----------------------------------

    /// <summary>
    ///     Editor: dock at 340 px, Inspector open, notation strip at 480. Start a
    ///     run (dock row becomes the window, panes close, strip is capped at 240),
    ///     quit from the pause menu, and every one of those must survive.
    /// </summary>
    [Fact]
    public void QuittingMidRun_LeavesTheEditorLayoutExactlyAsItWas()
    {
        double? savedDock = 340;
        double? savedSheet = 480;
        var savedPanesAreEditorPanes = true;

        // ...the run's measurements, offered to the close path.
        const bool inRun = true;
        if (ShellGeometryPersistPolicy.DockHeightToPersistPx(980, true, inRun) is { } dockPx)
            savedDock = dockPx;
        if (SheetStripHeightPolicy.HeightToPersistPx(
                SheetStripHeightPolicy.GameplayMaxPx, inRun) is { } sheetPx)
        {
            savedSheet = sheetPx;
        }

        if (ShellGeometryPersistPolicy.MayPersistDockPanes(inRun))
            savedPanesAreEditorPanes = false; // the capture would record both panes Closed

        Assert.Equal(340, savedDock);
        Assert.Equal(480, savedSheet);
        Assert.True(savedPanesAreEditorPanes, "the run's closed panes were persisted as the editor's");
    }

    // ---- the two writers actually ASK ---------------------------------------

    /// <summary>
    ///     Both close-path writers must route through the policy. The bare
    ///     <c>ActualHeight</c> writes below are exactly the code this fix removed
    ///     — and the sheet-strip one is a VERBATIM copy of the line
    ///     <c>SheetStripHeightPolicy</c> was created to delete from
    ///     <c>MainWindowShellLayout</c>, which survived here because that fix
    ///     linted one file and this is the other.
    /// </summary>
    [Fact]
    public void TheCloseHandlers_AskThePolicyRatherThanMeasuringDirectly()
    {
        var layout = Code("DotCharter.UI", "Layout", "MainWindowLayoutController.cs");
        Assert.Contains("ShellGeometryPersistPolicy.DockHeightToPersistPx(", layout);
        Assert.Contains("ShellGeometryPersistPolicy.InspectorWidthToPersistPx(", layout);
        Assert.Contains("SheetStripHeightPolicy.HeightToPersistPx(", layout);
        Assert.DoesNotMatch(new Regex(@"px\s*>\s*0\s*&&\s*vm\.IsBottomDockVisible"), layout);
        Assert.DoesNotMatch(new Regex(@"px\s*>=\s*120\s*&&\s*vm\.IsSheetMusicVisible"), layout);

        var docking = Code("DotCharter.UI", "Views", "MainWindow.Docking.cs");
        var persist = docking.IndexOf("private void PersistDockLayout()", StringComparison.Ordinal);
        Assert.True(persist >= 0, "PersistDockLayout has moved — repoint this lint.");
        var body = docking[persist..];
        var guard = body.IndexOf("MayPersistDockPanes(", StringComparison.Ordinal);
        var capture = body.IndexOf("CaptureState(", StringComparison.Ordinal);
        Assert.True(guard >= 0, "PersistDockLayout never consults ShellGeometryPersistPolicy.");
        Assert.True(guard < capture,
            "the gameplay guard must come BEFORE the capture, or the run's panes are snapshotted anyway.");
    }

    private static string Code(params string[] parts)
    {
        var text = UiSourceTree.ReadAllText(Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray()));
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\r\n]*", "");
    }
}

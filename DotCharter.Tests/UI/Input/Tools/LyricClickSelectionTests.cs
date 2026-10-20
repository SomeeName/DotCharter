using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.Input.Tools;
using ToneChange = DotCharter.Core.Domain.Events.ToneChange;

namespace DotCharter.Tests.UI.Input.Tools;

/// <summary>
///     Clicking a lyric REPLACES the selection, like clicking anything else on the timeline.
///     <para>
///         Owner report, 2026-09-25: click a note, then click a lyric, then wheel to lengthen
///         the lyric — the NOTE's sustain grew. The lyric click only set
///         <see cref="ISelectionService.SelectedLyric" /> and left the note selected, and
///         <c>TimelineWheelAdjuster.Apply</c> tries the selected-sound rule before the lyric rule,
///         so the stale note took the wheel. A click on the note lane already cleared a selected
///         lyric (<c>ClearSelections</c>); the lyric lane did not return the favour.
///     </para>
/// </summary>
public sealed class LyricClickSelectionTests
{
    [Fact]
    public void ClickingALyric_DropsTheNoteThatWasSelected()
    {
        var selection = new SelectionService();
        selection.SelectedNotes.Add(new GuitarNote());
        selection.SelectedChords.Add(new Chord());
        selection.SelectedHandShapes.Add(new HandShape());
        selection.SelectedAnchors.Add(new FretAnchor());
        selection.SelectedTones.Add(new ToneChange());
        selection.SelectedEventPoints.Add(new EventPoint());
        var lyric = new LyricEvent { Text = "la" };

        EventInteractionTool.SelectLyricAlone(selection, selection, lyric);

        Assert.Same(lyric, selection.SelectedLyric);
        Assert.Empty(selection.SelectedNotes);
        Assert.Empty(selection.SelectedChords);
        Assert.Empty(selection.SelectedHandShapes);
        Assert.Empty(selection.SelectedAnchors);
        Assert.Empty(selection.SelectedTones);
        Assert.Empty(selection.SelectedEventPoints);
    }

    /// <summary>A stacked track row has its OWN selection; its notes are just as stale.</summary>
    [Fact]
    public void ClickingALyric_AlsoClearsTheClickedTracksOwnSelection()
    {
        var editor = new SelectionService();
        var track = new SelectionService();
        track.SelectedNotes.Add(new GuitarNote());
        var lyric = new LyricEvent { Text = "la" };

        EventInteractionTool.SelectLyricAlone(editor, track, lyric);

        Assert.Same(lyric, editor.SelectedLyric);
        Assert.Empty(track.SelectedNotes);
    }

    /// <summary>The press handler goes through the helper — a unit test of a helper nothing calls
    /// would pass while the bug came back.</summary>
    [Fact]
    public void TheLyricPress_SelectsThroughTheHelper()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Input", "Tools",
            "EventInteractionTool.cs")));
        var press = source[source.IndexOf("private void HandleLyricPress", StringComparison.Ordinal)..];
        press = press[..press.IndexOf("public void OnPointerMoved", StringComparison.Ordinal)];

        Assert.Contains("SelectLyricAlone(", press);
        Assert.DoesNotContain("selection.SelectedLyric = lyric;", press);
    }
}

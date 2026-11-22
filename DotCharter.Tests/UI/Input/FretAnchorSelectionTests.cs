using Avalonia.Input;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.Input;
using DotCharter.Tests.UI.Input.Tools;

namespace DotCharter.Tests.UI.Input;

/// <summary>Anchor clicks replace an unrelated selection and preserve intentional multi-selection.</summary>
public sealed class FretAnchorSelectionTests
{
    [Theory]
    [InlineData(false, KeyModifiers.None, false, FretAnchorSelectionChange.None)]
    [InlineData(true, KeyModifiers.None, false, FretAnchorSelectionChange.Replace)]
    [InlineData(true, KeyModifiers.None, true, FretAnchorSelectionChange.Keep)]
    [InlineData(true, KeyModifiers.Control, false, FretAnchorSelectionChange.Toggle)]
    [InlineData(true, KeyModifiers.Control, true, FretAnchorSelectionChange.Toggle)]
    [InlineData(true, KeyModifiers.Shift, false, FretAnchorSelectionChange.Extend)]
    [InlineData(true, KeyModifiers.Shift, true, FretAnchorSelectionChange.Extend)]
    [InlineData(true, KeyModifiers.Control | KeyModifiers.Shift, false, FretAnchorSelectionChange.Extend)]
    public void TheDecisionDependsOnTheHitModifiersAndMembership(bool hit, KeyModifiers modifiers, bool selected,
        FretAnchorSelectionChange expected) => Assert.Equal(expected, FretAnchorSelection.Resolve(hit, modifiers, selected));

    [Fact]
    public void AnUnselectedAnchorReplacesEveryOtherSelection_AndNotifiesAndLogs()
    {
        var selection = new SelectionService();
        var track = Track(selection);
        var anchor = track.Anchors[1];
        selection.SelectedNotes.Add(new GuitarNote());
        selection.SelectedChords.Add(new Chord());
        selection.SelectedHandShapes.Add(new HandShape());
        selection.SelectedEventPoints.Add(new EventPoint());
        selection.SelectedTones.Add(new ToneChange());
        selection.SelectedAnchors.Add(track.Anchors[0]);
        var notifications = 0;
        selection.SelectionChanged += (_, _) => notifications++;
        var logger = new FakeAppLogger();

        FretAnchorSelection.Apply(track, anchor, KeyModifiers.None, null, logger);

        Assert.Same(anchor, Assert.Single(selection.SelectedAnchors));
        Assert.Empty(selection.SelectedNotes);
        Assert.Empty(selection.SelectedChords);
        Assert.Empty(selection.SelectedHandShapes);
        Assert.Empty(selection.SelectedEventPoints);
        Assert.Empty(selection.SelectedTones);
        Assert.Null(selection.SelectedNote);
        Assert.Null(selection.SelectedChord);
        Assert.True(notifications > 0);
        Assert.True(logger.HasEntryContaining("INFO", "Highway selection updated"));
    }

    [Theory]
    [InlineData(KeyModifiers.None, true)]
    [InlineData(KeyModifiers.Control, false)]
    [InlineData(KeyModifiers.Control, true)]
    [InlineData(KeyModifiers.Shift, false)]
    public void SelectedPlainClicksAndModifiedClicksKeepTheOtherSoundsAndAnchors(KeyModifiers modifiers, bool alreadySelected)
    {
        var selection = new SelectionService();
        var track = Track(selection);
        var first = track.Anchors[0];
        var middle = track.Anchors[1];
        var hit = track.Anchors[2];
        var note = new GuitarNote();
        var chord = new Chord();
        var shape = new HandShape();
        var marker = new EventPoint();
        var tone = new ToneChange();
        selection.SelectedNotes.Add(note);
        selection.SelectedChords.Add(chord);
        selection.SelectedHandShapes.Add(shape);
        selection.SelectedEventPoints.Add(marker);
        selection.SelectedTones.Add(tone);
        selection.SelectedAnchors.Add(first);
        if (alreadySelected) selection.SelectedAnchors.Add(hit);
        var notifications = 0;
        selection.SelectionChanged += (_, _) => notifications++;

        FretAnchorSelection.Apply(track, hit, modifiers, first.Position, new FakeAppLogger());

        Assert.Same(note, Assert.Single(selection.SelectedNotes));
        Assert.Same(chord, Assert.Single(selection.SelectedChords));
        Assert.Same(shape, Assert.Single(selection.SelectedHandShapes));
        if (modifiers == KeyModifiers.None)
        {
            Assert.Empty(selection.SelectedEventPoints);
            Assert.Empty(selection.SelectedTones);
        }
        else
        {
            Assert.Same(marker, Assert.Single(selection.SelectedEventPoints));
            Assert.Same(tone, Assert.Single(selection.SelectedTones));
        }
        Assert.Contains(first, selection.SelectedAnchors);
        if (modifiers == KeyModifiers.Control && alreadySelected)
            Assert.DoesNotContain(hit, selection.SelectedAnchors);
        else
            Assert.Contains(hit, selection.SelectedAnchors);
        if (modifiers == KeyModifiers.Shift)
        {
            Assert.Contains(middle, selection.SelectedAnchors);
            Assert.Equal(3, selection.SelectedAnchors.Count);
        }
        else
        {
            Assert.DoesNotContain(middle, selection.SelectedAnchors);
            Assert.Equal(modifiers == KeyModifiers.Control && alreadySelected ? 1 : 2, selection.SelectedAnchors.Count);
        }

        if (modifiers == KeyModifiers.None) Assert.True(notifications > 0);
    }

    [Fact]
    public void SelectingAnUnselectedAnchorThenUsingTheDeleteEntryRemovesOnlyTheAnchor()
    {
        var h = new ChartEditHarness();
        var note = new GuitarNote { Position = new FractionalPosition(2), Fret = 7 };
        var anchor = new FretAnchor(new FractionalPosition(2), 5, 4);
        h.Editor.PlacedNotes.Add(note);
        h.Editor.Anchors.Add(anchor);
        h.Editor.Selection.SelectedNotes.Add(note);
        var track = h.Editor.TrackContexts.GetOrCreate(h.State.ActiveArrangement!);

        FretAnchorSelection.Apply(track, anchor, KeyModifiers.None, null, new FakeAppLogger());
        var coordinator = new GlobalEditingCoordinator(h.State, h.Editor.Selection, h.History,
            RecordingProxy.Create<IClipboardService>().Proxy, h.Tempo, RecordingProxy.Create<IBeatScaleService>().Proxy,
            RecordingProxy.Create<IHighwayEditingService>().Proxy, new FakeConfigurationService(),
            RecordingProxy.Create<ISelectLikeService>().Proxy, RecordingProxy.Create<IFingeringGenerationService>().Proxy,
            h.Editor.TrackContexts, RecordingProxy.Create<IPhraseCatalogService>().Proxy, new FakeAppLogger(),
            FakeLocalization.KeysOnly());

        coordinator.DeleteSelected(EditorFocusArea.NoteHighway);

        Assert.Empty(h.Editor.Anchors);
        Assert.Same(note, Assert.Single(h.Editor.PlacedNotes));
        h.History.Undo();
        Assert.Same(anchor, Assert.Single(h.Editor.Anchors));
        Assert.Same(note, Assert.Single(h.Editor.PlacedNotes));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void APlainPressOnASelectedAnchorKeepsSoundsButClearsEventsAndTones()
    {
        var h = new FretAnchorToolHarness();
        var selection = h.Track.Selection;
        var note = new GuitarNote();
        selection.SelectedNotes.Add(note);
        selection.SelectedAnchors.Add(h.Anchor);
        selection.SelectedEventPoints.Add(new EventPoint());
        selection.SelectedTones.Add(new ToneChange());
        h.Press(508, right: false);
        Assert.Same(note, Assert.Single(selection.SelectedNotes));
        Assert.Same(h.Anchor, Assert.Single(selection.SelectedAnchors));
        Assert.Empty(selection.SelectedEventPoints);
        Assert.Empty(selection.SelectedTones);
        Assert.Empty(h.Launcher.Requests);
    }

    [Fact]
    public void APlainPressOnAnUnselectedAnchorClearsTheSelectedSound()
    {
        var h = new FretAnchorToolHarness();
        h.Track.Selection.SelectedNotes.Add(new GuitarNote());
        h.Press(508, right: false);
        Assert.Empty(h.Track.Selection.SelectedNotes);
        Assert.Same(h.Anchor, Assert.Single(h.Track.Selection.SelectedAnchors));
    }

    private static TrackEditorContext Track(SelectionService selection)
    {
        var arrangement = new Arrangement();
        arrangement.Levels.Add(new ChartLevel());
        var track = new TrackEditorContext(arrangement, selection);
        for (var i = 1; i <= 3; i++) track.Anchors.Add(new FretAnchor(new FractionalPosition(i), i + 2, 4));
        return track;
    }
}

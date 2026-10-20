using System.Collections.ObjectModel;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

public sealed class ChordTemplateActionsTests
{
    [Fact]
    public void FingersDictHelper_Equal_ReturnsTrue_ForSameContent_DifferentOrder()
    {
        var a = new Dictionary<int, int> { { 0, 1 }, { 1, 2 }, { 2, 3 } };
        var b = new Dictionary<int, int> { { 2, 3 }, { 0, 1 }, { 1, 2 } };
        Assert.True(FingersDictHelper.Equal(a, b));
    }

    [Fact]
    public void FingersDictHelper_Equal_ReturnsFalse_ForDifferentValues()
    {
        var a = new Dictionary<int, int> { { 0, 1 }, { 1, 2 } };
        var b = new Dictionary<int, int> { { 0, 1 }, { 1, 9 } };
        Assert.False(FingersDictHelper.Equal(a, b));
        Assert.False(FingersDictHelper.Equal(a, new Dictionary<int, int> { { 0, 1 } }));
    }

    [Fact]
    public void SetTemplateFingersAction_HasEffect_FalseWhenIdentical_TrueWhenDifferent()
    {
        var h = new Harness();
        var template = new ChordTemplate { Fingers = new Dictionary<int, int> { { 0, 1 } } };
        var same = new SetTemplateFingersAction(template,
            new Dictionary<int, int> { { 0, 1 } },
            new Dictionary<int, int> { { 0, 1 } }, h.Editor);
        Assert.False(same.HasEffect);

        var different = new SetTemplateFingersAction(template,
            new Dictionary<int, int> { { 0, 1 } },
            new Dictionary<int, int> { { 0, 2 } }, h.Editor);
        Assert.True(different.HasEffect);
    }

    [Fact]
    public void SetTemplateFingersAction_ExecuteUndo_RoundTrips()
    {
        var h = new Harness();
        var template = new ChordTemplate { Fingers = new Dictionary<int, int> { { 0, 1 } } };
        var oldF = new Dictionary<int, int>(template.Fingers);
        var newF = new Dictionary<int, int> { { 0, 4 }, { 1, 2 } };
        var action = new SetTemplateFingersAction(template, oldF, newF, h.Editor);

        action.Execute();
        Assert.Equal(2, template.Fingers.Count);
        Assert.Equal(4, template.Fingers[0]);

        action.Undo();
        Assert.Single(template.Fingers);
        Assert.Equal(1, template.Fingers[0]);
    }

    /// <summary>A grip someone fingers is no longer deliberately fingerless (#609);
    /// undo makes it fingerless again.</summary>
    [Fact]
    public void SetTemplateFingersAction_ClearsFingerless_AndUndoPutsItBack()
    {
        var h = new Harness();
        var template = new ChordTemplate { Fingerless = true };
        var action = new SetTemplateFingersAction(template, [], new Dictionary<int, int> { { 1, 1 } }, h.Editor);

        Assert.True(action.HasEffect);
        action.Execute();
        Assert.False(template.Fingerless);

        action.Undo();
        Assert.True(template.Fingerless);
        Assert.Empty(template.Fingers);
    }

    /// <summary>Marking a fingering authored sets no finger, so a fingerless grip
    /// stays fingerless.</summary>
    [Fact]
    public void SetTemplateFingersAction_ThatSetsNoFinger_KeepsFingerless()
    {
        var h = new Harness();
        var template = new ChordTemplate { Fingerless = true };

        var action = SetTemplateFingersAction.MarkAuthored(template, h.Editor);
        action.Execute();

        Assert.True(template.Fingerless);
    }

    [Fact]
    public void SetTemplateIdsAction_HasEffect_AndRoundTrip()
    {
        var h = new Harness();
        var c1 = new Chord { TemplateId = 5 };
        var c2 = new Chord { TemplateId = 7 };
        var noop = new SetTemplateIdsAction(new[] { (c1, 5, 5), (c2, 7, 7) }, h.Editor);
        Assert.False(noop.HasEffect);

        var action = new SetTemplateIdsAction(new[] { (c1, 5, 9), (c2, 7, 7) }, h.Editor);
        Assert.True(action.HasEffect);
        action.Execute();
        Assert.Equal(9, c1.TemplateId);
        Assert.Equal(7, c2.TemplateId);
        action.Undo();
        Assert.Equal(5, c1.TemplateId);
        Assert.Equal(7, c2.TemplateId);
    }

    [Fact]
    public void SetHandShapeTemplateIdsAction_RoundTrip()
    {
        var h = new Harness();
        var hs = new HandShape { TemplateId = 1 };
        var action = new SetHandShapeTemplateIdsAction(new[] { (hs, 1, 4) }, h.Editor);
        Assert.True(action.HasEffect);
        action.Execute();
        Assert.Equal(4, hs.TemplateId);
        action.Undo();
        Assert.Equal(1, hs.TemplateId);
    }

    [Fact]
    public void CreateAndSwitchTemplateAction_AddsAndRemovesTemplate_AndRetargetsChord()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var existingCount = arr.ChordTemplates.Count;
        var c = new Chord { TemplateId = 1 };
        var newT = new ChordTemplate { Name = "X" };
        var newC = new Chord { TemplateId = 99 };
        var action = new CreateAndSwitchTemplateAction(arr.ChordTemplates, newT,
            [c], [newC],
            [], [], h.Editor);

        action.Execute();
        Assert.Equal(existingCount + 1, arr.ChordTemplates.Count);
        Assert.Equal(99, c.TemplateId);

        action.Undo();
        Assert.Equal(existingCount, arr.ChordTemplates.Count);
        Assert.Equal(1, c.TemplateId);
    }

    /// <summary>
    ///     <b>Undo must remove the instance it created, not one that merely looks
    ///     like it.</b> <see cref="ChordTemplate" /> has VALUE equality that
    ///     deliberately excludes <see cref="ChordTemplate.Id" /> (the diff engine
    ///     and the power-chord builder need that), so
    ///     <c>ObservableCollection.Remove</c> deletes the FIRST value-equal
    ///     template. Two value-equal templates with different ids are ordinary
    ///     chart state — nothing dedupes them on the way in from an RS-XML /
    ///     psarc / rscp file, and the template editor's create-then-rename path
    ///     mints them by hand. So undoing the create used to delete somebody
    ///     else's STILL-REFERENCED template, leaving those chords with a dangling
    ///     TemplateId — and <c>SngFromDomainMapper</c> filters chords on
    ///     <c>TemplateIndexById.ContainsKey</c>, so they vanished from the
    ///     exported psarc with no error anywhere.
    /// </summary>
    [Fact]
    public void CreateAndSwitchTemplateAction_Undo_RemovesItsOwnInstance_NotAValueEqualTwin()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;

        // The already-charted template, and a chord that depends on it.
        var incumbent = Shape(id: 3, "G5");
        arr.ChordTemplates.Add(incumbent);
        var dependent = new Chord { TemplateId = 3 };

        // A brand-new template that happens to be value-equal to it.
        var minted = Shape(id: 7, "G5");
        Assert.Equal(incumbent, minted);       // value equality: the trap
        Assert.NotSame(incumbent, minted);

        var retargeted = new Chord { TemplateId = 1 };
        var action = new CreateAndSwitchTemplateAction(arr.ChordTemplates, minted,
            [retargeted], [new Chord { TemplateId = 7 }], [], [], h.Editor);

        action.Execute();
        Assert.Equal(2, arr.ChordTemplates.Count);

        action.Undo();

        Assert.Same(incumbent, Assert.Single(arr.ChordTemplates));
        Assert.DoesNotContain(arr.ChordTemplates, t => ReferenceEquals(t, minted));
        // The dependent chord's template is still resolvable — the whole point.
        Assert.Contains(arr.ChordTemplates, t => t.Id == dependent.TemplateId);
    }

    /// <summary>
    ///     <b>An undo node belongs to the arrangement it was recorded on, and
    ///     nothing puts that arrangement back before the node runs.</b>
    ///     <para>
    ///         The action used to read <c>_editor.ActiveArrangement</c> inside
    ///         Execute/Undo. <c>HistoryService</c>'s <c>FocusRequested</c> reaches
    ///         <c>TrackContextRegistry.Focus</c>, which sets <c>FocusedTrack</c>
    ///         and has no subscriber, so on a two-arrangement project the
    ///         arrangement at undo time is simply whichever the user is looking at.
    ///     </para>
    ///     <para>
    ///         Both directions lose data. Undo on Rhythm removed nothing, so the
    ///         template stayed on Lead and was saved into the <c>.rscp</c> as an
    ///         orphan; redo then ADDED it to Rhythm while re-pointing LEAD's chords
    ///         at its id — an id minted as Lead's <c>max(Id) + 1</c>, so on Rhythm
    ///         it either does not exist, in which case
    ///         <c>SngFromDomainMapper</c> filters on
    ///         <c>TemplateIndexById.ContainsKey</c> and drops those chords from the
    ///         exported psarc without a word, or it collides with an unrelated
    ///         template and they adopt its shape.
    ///     </para>
    /// </summary>
    [Fact]
    public void CreateAndSwitchTemplateAction_EditsTheArrangementItWasRecordedOn_NotTheOneOnScreen()
    {
        var h = new Harness();
        var lead = h.Editor.ActiveArrangement!;
        var rhythm = new Arrangement { Name = "Rhythm", ArrangementType = ArrangementType.Rhythm };
        rhythm.Levels.Add(new ChartLevel());
        rhythm.ChordTemplates.Add(Shape(id: 0, "Rhythm's own"));
        h.State.ActiveProject!.Arrangements.Add(rhythm);

        var chord = new Chord { TemplateId = 1 };
        var minted = Shape(id: 4, "Lead shape");
        var action = new CreateAndSwitchTemplateAction(lead.ChordTemplates, minted,
            [chord], [new Chord { TemplateId = 4 }], [], [], h.Editor);

        action.Execute();
        Assert.Contains(lead.ChordTemplates, t => ReferenceEquals(t, minted));

        // The charter switches arrangement, then hits Ctrl+Z.
        h.State.ActiveArrangement = rhythm;
        h.State.ActiveLevel = rhythm.Levels[0];

        action.Undo();

        Assert.DoesNotContain(lead.ChordTemplates, t => ReferenceEquals(t, minted));
        Assert.Equal(1, chord.TemplateId);
        Assert.Single(rhythm.ChordTemplates);

        // …and Ctrl+Y from there puts it back where it came from, so the chord it
        // re-points still resolves.
        action.Execute();

        Assert.Contains(lead.ChordTemplates, t => ReferenceEquals(t, minted));
        Assert.Single(rhythm.ChordTemplates);
        Assert.Contains(lead.ChordTemplates, t => t.Id == chord.TemplateId);
    }

    [Fact]
    public void AddChordTemplatesAction_AddsItsOwnInstance_EvenWhenAValueEqualTwinIsAlreadyThere()
    {
        var collection = new ObservableCollection<ChordTemplate>();
        var incumbent = Shape(id: 3, "G5");
        collection.Add(incumbent);

        var minted = Shape(id: 7, "G5");
        var action = new AddChordTemplatesAction(collection, [minted]);

        action.Execute();

        // Without the instance check the mint is skipped, and every chord this
        // action's siblings just repointed at id 7 has a dangling TemplateId.
        Assert.Equal(2, collection.Count);
        Assert.Contains(collection, t => ReferenceEquals(t, minted));

        action.Undo();

        Assert.Same(incumbent, Assert.Single(collection));
    }

    /// <summary>A template that is only value-equal is not "the same template".</summary>
    private static ChordTemplate Shape(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Frets = new Dictionary<int, int> { { 0, 3 }, { 1, 5 } },
        Fingers = new Dictionary<int, int> { { 0, 1 }, { 1, 3 } }
    };

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];

            var transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly());
            var tracks = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, transport, history, Selection, tempo, tracks, FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public ChartEditorViewModel Editor { get; }
    }
}

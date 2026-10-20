using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     #720: the quality report skips copying an arrangement whose <see cref="Arrangement.GetRevision" />
///     has not moved since its spare copy was filled, so a committed edit that changes an arrangement
///     and does NOT bump it leaves the report checking the chart as it was. These pin "every committed
///     edit bumps every arrangement it changed" through the real <see cref="HistoryService" />: do,
///     undo, redo, a merged gesture tick, a direct push and an apply that threw.
///     <para>
///         The narrow placement — an add/remove/mutate action marks only the arrangement that owns its
///         collection — is the part a new model collection could break, so it is walked by REFLECTION
///         over every collection reachable from an arrangement, the way
///         <c>ChartActionFieldCoverageTests</c> walks fields: each one is edited inside the SECOND
///         arrangement while the editing scope points at the first, and that edit, its undo and its
///         redo must each bump the second and leave the first alone. A collection
///         <c>ChartEditRevisions</c> does not know is still safe (it marks every arrangement), but it
///         fails here until it is added there or excluded with a reason.
///     </para>
/// </summary>
public sealed class ChartEditRevisionCoverageTests
{
    /// <summary>Collections inside an ELEMENT, which no collection action edits: the element is replaced or
    /// mutated whole, through its own collection's action.</summary>
    private static readonly FieldExclusion[] Exclusions =
    [
        new(typeof(Chord), nameof(Chord.ChordNotes), "a chord's notes change through MutateChordsAction over the level's chords"),
        new(typeof(ChordNote), nameof(ChordNote.BendValues), "a chord note's bends change through MutateChordsAction over the level's chords"),
        new(typeof(GuitarNote), nameof(GuitarNote.BendValues), "a note's bends change through MutateNotesAction over the level's notes")
    ];

    private sealed record Rig(ChartProject Project, ProjectState State, HistoryService History)
    {
        public Arrangement Scoped => Project.Arrangements[0];
        public Arrangement Other => Project.Arrangements[1];
        public long[] Revisions() => Project.Arrangements.Select(a => a.GetRevision()).ToArray();
    }

    private static Rig NewRig(DateTime? now = null)
    {
        var project = new ChartProject();
        project.Arrangements.Add(DomainFieldProbe.Populated<Arrangement>());
        project.Arrangements.Add(DomainFieldProbe.Populated<Arrangement>());
        project.Arrangements.Add(DomainFieldProbe.Populated<Arrangement>());
        var state = new ProjectState
        {
            ActiveProject = project,
            ActiveArrangement = project.Arrangements[0],
            ActiveLevel = project.Arrangements[0].Levels[0]
        };
        var history = new HistoryService(new FakeAppLogger(), new EditingScope(state), clock: () => now ?? DateTime.UnixEpoch);
        return new Rig(project, state, history);
    }

    /// <summary>Every collection reachable from <paramref name="owner" />, with the path that reaches it.</summary>
    private static IEnumerable<(string Path, IList Collection, Type Element)> Collections(object owner, string path)
    {
        foreach (var prop in owner.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var type = prop.PropertyType;
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ObservableCollection<>)) continue;
            if (Exclusions.Any(e => e.Owner == owner.GetType() && e.Property == prop.Name)) continue;
            var collection = (IList)prop.GetValue(owner)!;
            var element = type.GetGenericArguments()[0];
            var here = $"{path}.{prop.Name}";
            yield return (here, collection, element);
            if (collection.Count == 0 || element == typeof(string)) continue;
            foreach (var nested in Collections(collection[0]!, here + "[0]")) yield return nested;
        }
    }

    private static IChartAction AddOne(IList collection, Type element)
    {
        var item = element == typeof(string) ? "added" : Activator.CreateInstance(element)!;
        var method = typeof(ChartEditRevisionCoverageTests).GetMethod(nameof(AddTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(element);
        return (IChartAction)method.Invoke(null, [collection, item])!;
    }

    private static AddElementsAction<T> AddTyped<T>(ObservableCollection<T> collection, T item) where T : class =>
        new(collection, [item], _ => default);

    [Fact]
    public void TheExclusions_StillExist()
    {
        foreach (var exclusion in Exclusions)
            Assert.NotNull(exclusion.Owner.GetProperty(exclusion.Property));
    }

    [Fact]
    public void EveryCollectionOfAnArrangement_ItsEditUndoAndRedo_BumpThatArrangementOnly()
    {
        var rig = NewRig();
        var walked = Collections(rig.Other, "Arrangement").ToList();
        Assert.Contains(walked, c => c.Path == "Arrangement.Levels[0].Notes");
        Assert.Contains(walked, c => c.Path == "Arrangement.NotationVoices[0].Chords");

        var failures = new List<string>();
        foreach (var (path, collection, element) in walked)
        {
            var action = AddOne(collection, element);
            var count = collection.Count;
            Step("do", () => rig.History.Do(action), count + 1);
            Step("undo", rig.History.Undo, count);
            Step("redo", rig.History.Redo, count + 1);

            void Step(string what, Action apply, int expectedCount)
            {
                var before = rig.Revisions();
                apply();
                var after = rig.Revisions();
                if (collection.Count != expectedCount) failures.Add($"{path}: {what} did not edit the collection");
                if (after[1] <= before[1]) failures.Add($"{path}: {what} did not bump the arrangement it edited");
                if (after[0] != before[0] || after[2] != before[2])
                    failures.Add($"{path}: {what} bumped an arrangement it did not edit (add it to ChartEditRevisions.Holds)");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Narrow placement is sound only while a collection action edits nothing but its collection
    /// and that collection's elements: no subclass may add its own apply.</summary>
    [Fact]
    public void NoCollectionAction_OverridesTheApplyOfTheThreeBases()
    {
        Type[] bases = [typeof(AddElementsAction<>), typeof(RemoveElementsAction<>), typeof(MutateElementsAction<>)];
        var offenders = new List<string>();
        foreach (var type in typeof(HistoryService).Assembly.GetTypes()
                     .Concat(typeof(DotCharter.Presentation.Screens.Dialogs.QualityReportChecks).Assembly.GetTypes())
                     .Concat(typeof(DotCharter.UI.App).Assembly.GetTypes()))
        {
            if (type.IsAbstract || !DerivesFrom(type, typeof(CollectionActionBase<>))) continue;
            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            if (bases.Contains(definition)) continue;
            if (!bases.Any(b => DerivesFrom(type, b))) offenders.Add($"{type.FullName}: not an add, remove or mutate action");
            var own = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name is nameof(IChartAction.Execute) or nameof(IChartAction.Undo));
            offenders.AddRange(own.Select(m => $"{type.FullName}.{m.Name}"));
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static bool DerivesFrom(Type type, Type generic)
    {
        for (var t = type.BaseType; t != null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == generic) return true;
        }

        return false;
    }

    [Fact]
    public void AnActionTheHistoryCannotPlace_BumpsEveryArrangement_OnDoUndoAndRedo()
    {
        var rig = NewRig();
        var key = rig.Project.Chart.KeyAccidentals;
        var beat = new BeatMarker { Time = 0, Bpm = 120 };
        rig.Project.Beats.Add(beat);
        IChartAction[] actions =
        [
            new SetKeySignatureAction(rig.Project.Chart, key + 1, true),
            new MutateBeatsAction(rig.Project.Beats, [beat], [new BeatMarker { Time = 5, Bpm = 120 }]),
            new AddNotesAction([], [new GuitarNote()])
        ];
        foreach (var action in actions)
        {
            AssertAllBumped(rig, () => rig.History.Do(action), action.GetType().Name + " do");
            AssertAllBumped(rig, rig.History.Undo, action.GetType().Name + " undo");
            AssertAllBumped(rig, rig.History.Redo, action.GetType().Name + " redo");
        }
    }

    [Fact]
    public void APostPass_AlsoBumpsTheScopedArrangement()
    {
        var rig = NewRig();
        var before = rig.Revisions();
        rig.History.Do(new AddNotesAction(rig.Other.Levels[0].Notes, [new GuitarNote()], () => { }));
        var after = rig.Revisions();

        Assert.True(after[0] > before[0]);
        Assert.True(after[1] > before[1]);
        Assert.Equal(before[2], after[2]);
    }

    [Fact]
    public void ACompositeBumpsWhatItsChildrenReach_AndEverythingWhenOneChildCannotBePlaced()
    {
        var rig = NewRig();
        var before = rig.Revisions();
        rig.History.Do(new CompositeAction([
            new AddNotesAction(rig.Other.Levels[0].Notes, [new GuitarNote()]),
            new AddNotesAction(rig.Project.Arrangements[2].Levels[0].Notes, [new GuitarNote()])
        ]));
        var after = rig.Revisions();
        Assert.Equal(before[0], after[0]);
        Assert.True(after[1] > before[1] && after[2] > before[2]);

        AssertAllBumped(rig, () => rig.History.Do(new CompositeAction([
            new AddNotesAction(rig.Other.Levels[0].Notes, [new GuitarNote()]),
            new SetKeySignatureAction(rig.Project.Chart, 3, false)
        ])), "a composite with a child it cannot place");
    }

    /// <summary>A merged tick moves no history node, and still edits the chart.</summary>
    [Fact]
    public void AMergedGestureTick_BumpsLikeANewNode()
    {
        var rig = NewRig();
        var notes = rig.Other.Levels[0].Notes;
        var note = notes[0];
        MutateNotesAction Tick(int fret) =>
            new(notes, [note], [note.Clone()], [new GuitarNote { Position = note.Position, Fret = fret }], null, MergeVerbs.Sustain);

        rig.History.Do(Tick(5));
        var node = rig.History.CurrentNodeId;
        var before = rig.Other.GetRevision();
        rig.History.Do(Tick(6));

        Assert.Equal(node, rig.History.CurrentNodeId);
        Assert.True(rig.Other.GetRevision() > before);
    }

    [Fact]
    public void ADirectPush_OfAnEditTheCallerApplied_Bumps()
    {
        var rig = NewRig();
        var notes = rig.Other.Levels[0].Notes;
        var added = new GuitarNote();
        notes.Add(added);
        var before = rig.Other.GetRevision();

        rig.History.PushAction(new AddNotesAction(notes, [added]));

        Assert.True(rig.Other.GetRevision() > before);
    }

    [Fact]
    public void AnApplyThatThrew_BumpsEveryArrangement()
    {
        var rig = NewRig();
        AssertAllBumped(rig, () => Assert.Throws<InvalidOperationException>(() => rig.History.Do(new ThrowingAction())), "a throwing apply");
    }

    [Fact]
    public void RestoringIntoAnArrangement_BumpsIt()
    {
        var live = DomainFieldProbe.Populated<Arrangement>();
        var before = live.GetRevision();

        ChartProjectRestorer.RestoreArrangement(live, DomainFieldProbe.Populated<Arrangement>());

        Assert.True(live.GetRevision() > before);
    }

    private static void AssertAllBumped(Rig rig, Action apply, string what)
    {
        var before = rig.Revisions();
        apply();
        var after = rig.Revisions();
        for (var i = 0; i < before.Length; i++) Assert.True(after[i] > before[i], $"{what}: arrangement {i} was not bumped");
    }

    private sealed class ThrowingAction : IChartAction
    {
        public void Execute() => throw new InvalidOperationException("half applied");

        public void Undo()
        {
        }
    }
}

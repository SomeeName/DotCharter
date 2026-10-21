using DotCharter.Core.Linking;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Linking;
using DotCharter.Tests.Core.Linking;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     A live editing session over <see cref="LinkFixture" />'s chart (one pattern measure placed
///     at bars 1, 4 and 7, each copy in a different neighbourhood) with a real
///     <see cref="HistoryService" /> on an <see cref="EditingScope" />, so every edit goes through
///     the same funnel the app's does — the link reconciler included.
/// </summary>
internal sealed class LinkHarness
{
    public LinkHarness(ChartProject project, AppConfig? config = null)
    {
        Config = config ?? LinkFixture.Config(MinNoteDistanceMode.MeasureFraction);
        Rule = MeasureContentOptions.Detection with { Spacing = ChartSpacingEnforcer.SpacingGapOf(Config) };
        State.ActiveProject = project;
        State.ActiveArrangement = project.Arrangements[0];
        State.ActiveLevel = Hardest(project.Arrangements[0]);
        History = new HistoryService(new FakeAppLogger(), new EditingScope(State));
    }

    public ProjectState State { get; } = new();
    public HistoryService History { get; }
    public AppConfig Config { get; }
    public MeasureContentOptions Rule { get; }
    public ChartProject Project => State.ActiveProject!;
    public Arrangement Lead => Project.Arrangements[0];
    public Arrangement Rhythm => Project.Arrangements[1];

    /// <summary>The fixture, cleaned up as a load would, with a Rhythm part that is a copy of Lead.</summary>
    public static LinkHarness WithRhythm(int? seed = null, AppConfig? config = null)
    {
        var project = LinkFixture.Project(seed);
        var rhythm = ChartProjectCloner.CloneArrangement(project.Arrangements[0]);
        rhythm.Name = "Rhythm";
        rhythm.ArrangementType = ArrangementType.Rhythm;
        project.Arrangements.Add(rhythm);
        var harness = new LinkHarness(project, config);
        ChartSpacingEnforcer.EnforceProject(project, harness.Config);
        return harness;
    }

    public static ChartLevel Hardest(Arrangement arrangement) => arrangement.Levels.MaxBy(l => l.DifficultyIndex)!;

    /// <summary>Links one-measure runs (arrangement, bar) through the history.</summary>
    public LinkGroup Link(params (Arrangement Arrangement, int Measure)[] runs)
    {
        var requests = runs.Select(r => new LinkRunRequest(r.Arrangement, LinkFixture.At(r.Measure, 0), LinkFixture.At(r.Measure + 1, 0))).ToList();
        var refusal = LinkGroupCreator.TryBuild(Project, requests, "Riff", Rule, out var action);
        Assert.Equal(LinkCreateRefusal.None, refusal);
        Assert.True(History.Do(action!));
        return Project.LinkGroups[^1];
    }

    /// <summary>The note starting at <paramref name="at" /> on <paramref name="stringIndex" /> in the hardest level.</summary>
    public static GuitarNote NoteAt(Arrangement arrangement, FractionalPosition at, int stringIndex) =>
        Hardest(arrangement).Notes.Single(n => n.Position == at && n.StringIndex == stringIndex);

    /// <summary>One editing gesture on one note, with the editor's spacing post-pass.</summary>
    public MutateNotesAction Mutate(Arrangement arrangement, GuitarNote note, Action<GuitarNote> change, string verb = "")
    {
        var level = Hardest(arrangement);
        var after = note.Clone();
        change(after);
        return new MutateNotesAction(level.Notes, [note], [note.Clone()], [after],
            () => ChartSpacingEnforcer.EnforceLevel(level, Project.Beats, Config), verb);
    }

    public bool Edit(Arrangement arrangement, GuitarNote note, Action<GuitarNote> change, string verb = "") =>
        History.Do(Mutate(arrangement, note, change, verb));

    /// <summary>The detection key of every live placement of <paramref name="group" />, in order.</summary>
    public List<MeasureContentKey?> Keys(LinkGroup group) =>
        [.. group.Placements.Select(p => LinkRuns.Keys(Project, group, Rule).GetValueOrDefault(p))];

    /// <summary>The integrity (exact stored content) key of one bar of one arrangement.</summary>
    public string Integrity(Arrangement arrangement, int measure) =>
        MeasureContentIndex.ForHardestLevel(arrangement, Project.Beats, MeasureContentOptions.Integrity)
            .KeyFor(measure).Canonical;

    /// <summary>Every bar of every arrangement, exactly.</summary>
    public List<string> Snapshot() =>
        [.. Project.Arrangements.SelectMany(a => Enumerable.Range(0, MeasureGrid.Build(Project.Beats).Count).Select(m => Integrity(a, m)))];

    public static void AssertLinkedAndEqual(LinkHarness h, LinkGroup group, int placements)
    {
        Assert.Contains(group, h.Project.LinkGroups);
        Assert.Equal(placements, group.Placements.Count);
        var keys = h.Keys(group);
        Assert.All(keys, k => Assert.NotNull(k));
        Assert.Single(keys.Select(k => k!.Canonical).Distinct());
    }
}

using System.Xml.Linq;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     Ignored chart-check warnings (#1032): stored in the project, they survive a save and a load, a changed
///     subject brings a finding back, an error is never ignored, and the list is an undoable edit. The fixture is
///     the issue's own case: one word ("baddy") sung on one note in every line, the identical D13 finding each time.
/// </summary>
public sealed class FindingIgnoresTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_ignored_findings_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.dcproj");

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void IgnoreAll_SurvivesSaveAndLoad_AndStopIgnoring_RoundTripsToo()
    {
        var project = LyricProject(3);
        var baddy = Baddy(project);
        Assert.Equal(3, baddy.Count);
        Assert.Single(baddy.Select(FindingIgnores.LikeKey).Distinct());

        Apply(project, add: baddy, remove: []);
        var loaded = SaveAndLoad(project);

        Assert.Equal(project.IgnoredFindings, loaded.IgnoredFindings);
        var (shown, ignored) = FindingIgnores.Partition(Check(loaded), loaded);
        Assert.Equal(3, ignored.Count);
        Assert.DoesNotContain(shown, issue => issue.Message == baddy[0].Message);

        Apply(loaded, add: [], remove: Baddy(loaded));
        var reloaded = SaveAndLoad(loaded);
        Assert.Empty(reloaded.IgnoredFindings);
        Assert.Null(XDocument.Load(ProjectPath).Root!.Element(RscpIgnoredFindings.RootElement));
        Assert.Empty(FindingIgnores.Partition(Check(reloaded), reloaded).Ignored);
    }

    [Fact]
    public void AnEditedLine_BringsItsFindingBack_TheOthersStayIgnored()
    {
        var project = LyricProject(3);
        Apply(project, add: Baddy(project), remove: []);

        // The second line's other word changes: same rule, same sentence, but its subject moved.
        project.VocalTracks[0].Phrases[1].Lyrics[0].Text = "yeah";

        var (shown, ignored) = FindingIgnores.Partition(Check(project), project);
        Assert.Equal(2, ignored.Count);
        var back = Assert.Single(shown, issue => issue.Message == ignored[0].Message);
        Assert.Equal(1, back.Lyric!.Phrase);
    }

    [Fact]
    public void AnEditedNote_BringsItsFindingBack()
    {
        var project = LyricProject(1);
        var note = new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 };
        project.Arrangements[0].Levels[0].Notes.Add(note);
        var warning = new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "a warning", 500, Note: note);
        Apply(project, add: [warning], remove: []);
        Assert.Single(FindingIgnores.Partition([warning], project).Ignored);

        note.Fret = 5;

        Assert.Single(FindingIgnores.Partition([warning], project).Shown);
    }

    [Fact]
    public void AnError_IsNeverIgnored_EvenWhenTheFileListsIt()
    {
        var project = LyricProject(1);
        var error = new ArrangementIssue(ArrangementIssueType.FirstPhraseNotEmpty, "an error", 1000);
        Assert.Equal(ArrangementIssueSeverity.Important, error.Severity);
        Assert.False(FindingIgnores.IsIgnorable(error));

        // A hand-edited (or older) file that lists it anyway.
        project.IgnoredFindings.Add(FindingIgnores.EntryFor(error, project));
        var loaded = SaveAndLoad(project);

        var (shown, ignored) = FindingIgnores.Partition([error], loaded);
        Assert.Same(error, Assert.Single(shown));
        Assert.Empty(ignored);
    }

    [Fact]
    public void TheList_IsAnUndoableEdit()
    {
        var project = LyricProject(2);
        var action = new SetIgnoredFindingsAction(project.IgnoredFindings,
            Baddy(project).Select(issue => FindingIgnores.EntryFor(issue, project)), []);
        Assert.True(action.HasEffect);

        action.Execute();
        Assert.Equal(2, project.IgnoredFindings.Count);
        action.Undo();
        Assert.Empty(project.IgnoredFindings);
        Assert.False(new SetIgnoredFindingsAction(project.IgnoredFindings, [], [new IgnoredFinding("D13", "x", "y")]).HasEffect);
    }

    [Fact]
    public void TheEntry_DoesNotDependOnTheCulture()
    {
        var project = LyricProject(1);
        var note = new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3, LeftHandFinger = -1 };
        var warning = new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "a warning", 500, Note: note);
        var invariant = FindingIgnores.EntryFor(warning, project);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // Swedish formats a negative number with U+2212, not '-'.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
            Assert.Equal(invariant, FindingIgnores.EntryFor(warning, project));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    private static void Apply(ChartProject project, IReadOnlyList<ArrangementIssue> add, IReadOnlyList<ArrangementIssue> remove) =>
        new SetIgnoredFindingsAction(project.IgnoredFindings,
            add.Select(issue => FindingIgnores.EntryFor(issue, project)),
            remove.Select(issue => FindingIgnores.EntryFor(issue, project))).Execute();

    private ChartProject SaveAndLoad(ChartProject project)
    {
        var serializer = new RscpChartSerializer();
        serializer.Save(ProjectPath, project);
        return serializer.Load(ProjectPath);
    }

    private static IReadOnlyList<ArrangementIssue> Check(ChartProject project) => LyricStyleRule.CheckAll(project, project.Beats);

    private static List<ArrangementIssue> Baddy(ChartProject project) =>
        [.. Check(project).Where(issue => issue.MessageKey == "Validation_LyricSeveralSyllablesOnOneNote")];

    /// <summary><paramref name="lines" /> lines of "oh baddy", one bar apart, on a 120 bpm grid.</summary>
    internal static ChartProject LyricProject(int lines)
    {
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arrangement);
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });

        var track = new VocalTrack { Name = "Lead Vocals" };
        for (var line = 0; line < lines; line++)
        {
            var phrase = new LyricPhrase { Position = new FractionalPosition(line * 4) };
            phrase.Lyrics.Add(new LyricEvent { Text = "oh", Position = new FractionalPosition(line * 4), EndPosition = new FractionalPosition(line * 4 + 1) });
            phrase.Lyrics.Add(new LyricEvent
            {
                Text = "baddy", Position = new FractionalPosition(line * 4 + 1), EndPosition = new FractionalPosition(line * 4 + 2),
                IsPhraseEnd = true
            });
            phrase.EndPosition = new FractionalPosition(line * 4 + 2);
            track.Phrases.Add(phrase);
        }

        project.VocalTracks.Add(track);
        return project;
    }
}

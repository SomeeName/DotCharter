using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 P5 review M5/M6/M7: the Findings page's "Fix all" asks first (design D's "Fix N findings across M sounds?")
///     and writes ONE undo step that one undo restores; a chord's fingering finding offers "Open grip editor" (the Shape
///     page); the repair count is worked out once per re-read and not at all while the page is off screen.
/// </summary>
public sealed class SelectionFindingsFixAllTests
{
    [Fact]
    public void FixAll_AsksFirst_ThenWritesOneUndoStep_ThatOneUndoRestores()
    {
        var (f, findings, asked, issues) = Build(SoundSelectionKind.Note, SoundSection.Findings);
        var before = f.Arrangement.ToneChanges.Select(t => t.Position).ToArray();
        var steps = f.History.GetUndoSteps().Count;

        findings.FixAllCommand.Execute(null);

        var (text, confirm) = Assert.Single(asked);
        Assert.Equal("Fix 2 findings across 2 sounds?", text);
        Assert.Equal(before, f.Arrangement.ToneChanges.Select(t => t.Position));

        confirm();
        Assert.Equal(steps + 1, f.History.GetUndoSteps().Count);
        Assert.All(f.Arrangement.ToneChanges.Zip(before), p => Assert.NotEqual(p.Second, p.First.Position));

        f.History.Undo();
        Assert.Equal(before, f.Arrangement.ToneChanges.Select(t => t.Position));
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void TheRepairCount_IsWorkedOutOncePerReread_AndNotWhileThePageIsHidden()
    {
        var (_, findings, _, _) = Build(SoundSelectionKind.Note, SoundSection.Placement);
        findings.Reread();
        findings.Reread();
        Assert.Equal(0, findings.FixablePasses);

        var (_, shown, _, _) = Build(SoundSelectionKind.Note, SoundSection.Findings);
        shown.Reread();
        _ = shown.FixAllLabel;
        _ = shown.CanFixAll;
        _ = shown.FixableCount;
        Assert.Equal(1, shown.FixablePasses);
        Assert.Equal(2, shown.FixableCount);
    }

    [Fact]
    public void AChordFingeringFinding_OffersTheGripEditor_WhichOpensTheShapePage()
    {
        var f = new ChordLadderFixture();
        var sections = new SoundSectionsViewModel { Kind = SoundSelectionKind.Chord, HasChords = true };
        sections.ChooseCommand.Execute(SoundSection.Findings);
        var issue = new ArrangementIssue(ArrangementIssueType.FingerMissingOnFrettedString, "L07", 0, Chord: f.First);
        IReadOnlyList<InspectorFieldIssue> rows = [.. InspectorFieldIssue.Fold([issue], s => string.Join(",", s), FakeLocalization.English())];
        using var findings = new SelectionFindingsViewModel(() => rows, () => [issue], f.Editor, f.History,
            FakeLocalization.English(), _ => { }, sections, (_, _) => { });

        Assert.True(findings.CanOpenGrip);
        findings.OpenGripCommand.Execute(null);
        Assert.Equal(SoundSection.Shape, sections.Section);
    }

    private static (ChordLadderFixture, SelectionFindingsViewModel, List<(string, Action)>, List<ArrangementIssue>) Build(
        SoundSelectionKind kind, SoundSection section)
    {
        var f = new ChordLadderFixture();
        var issues = new List<ArrangementIssue>();
        foreach (var beat in new[] { 4, 6 })
        {
            var at = new FractionalPosition(beat);
            var note = new GuitarNote { Position = at, Fret = 5 };
            f.Arrangement.Levels[0].Notes.Add(note);
            f.Arrangement.ToneChanges.Add(new ToneChange { Position = at, ToneName = "lead" });
            issues.Add(new ArrangementIssue(ArrangementIssueType.ToneChangeOnNote, "I30", at.GetPositionMs(f.Project.Beats), Note: note));
        }

        var sections = new SoundSectionsViewModel { Kind = kind };
        sections.ChooseCommand.Execute(section);
        var asked = new List<(string, Action)>();
        var findings = new SelectionFindingsViewModel(() => [], () => issues, f.Editor, f.History, FakeLocalization.English(),
            _ => { }, sections, (text, go) => asked.Add((text, go)));
        return (f, findings, asked, issues);
    }
}

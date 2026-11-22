using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Link next set in the inspector makes the link real: the note rings into the
///     next sound on its string, so the save pass keeps the flag and the highway draws
///     one continued sustain (owner report: the follower kept its own note head).
/// </summary>
public sealed class InspectorLinkNextReachTests
{
    [Fact]
    public void LinkNextOnInTheInspector_RingsIntoTheNextNoteOnTheString()
    {
        var f = new ChordLadderFixture();
        f.Selection.SelectedChords.Clear();
        var a = InspectorStagedEditsTests.AddNote(f, 20, 15);
        a.Sustain = new FractionalPosition(new Fraction(1, 2));
        var b = InspectorStagedEditsTests.AddNote(f, 22, 15);
        f.Selection.SelectedNotes.Add(a);
        using var staged = InspectorStagedEditsTests.Engine(f);

        staged.StageBulk(BulkDraft.None with { Flags = new Dictionary<BulkFlag, bool> { [BulkFlag.LinkNext] = true } });
        Assert.True(staged.Apply(StagedPage.Selection));

        Assert.True(a.LinkNext);
        Assert.Equal(b.Position, a.Position + a.Sustain);
    }
}

using System.Collections.ObjectModel;

namespace DotCharter.Tests.Services.State;

/// <summary>#520: the identity removal a level squash or regeneration drops its orphaned
/// templates with — never a value-equal twin, and back in its own slot on undo.</summary>
public sealed class RemoveChordTemplatesActionTests
{
    [Fact]
    public void RemovesTheInstance_NotAValueEqualTwin_AndUndoPutsItBackInItsSlot()
    {
        var twin = new ChordTemplate { Id = 0, Name = "", Frets = { [0] = 5, [1] = 7 } };
        var keep = new ChordTemplate { Id = 1, Name = "A", Frets = { [0] = 5 } };
        var orphan = new ChordTemplate { Id = 2, Name = "", Frets = { [0] = 5, [1] = 7 } };
        var last = new ChordTemplate { Id = 3, Name = "B", Frets = { [0] = 7 } };
        Assert.Equal(twin, orphan);
        var templates = new ObservableCollection<ChordTemplate> { twin, keep, orphan, last };
        var action = new RemoveChordTemplatesAction(templates, [orphan]);

        action.Execute();
        Assert.Equal([twin, keep, last], templates, ReferenceEqualityComparer.Instance);

        action.Undo();
        Assert.Equal([twin, keep, orphan, last], templates, ReferenceEqualityComparer.Instance);

        action.Execute();
        Assert.Equal([twin, keep, last], templates, ReferenceEqualityComparer.Instance);
    }

    [Fact]
    public void SeveralApart_ComeBackInOrder()
    {
        var all = Enumerable.Range(0, 6).Select(i => new ChordTemplate { Id = i, Frets = { [0] = i } }).ToList();
        var templates = new ObservableCollection<ChordTemplate>(all);
        var action = new RemoveChordTemplatesAction(templates, [all[4], all[0], all[2]]);
        Assert.True(action.HasEffect);

        action.Execute();
        Assert.Equal([all[1], all[3], all[5]], templates, ReferenceEqualityComparer.Instance);

        action.Undo();
        Assert.Equal(all, templates, ReferenceEqualityComparer.Instance);
    }

    [Fact]
    public void NothingToRemove_HasNoEffect() =>
        Assert.False(new RemoveChordTemplatesAction(new ObservableCollection<ChordTemplate>(), []).HasEffect);
}

using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>Inspector string-mask chip rows.</summary>
public sealed class InspectorStaleStateTests
{
    /// <summary>The chip row was rebuilt only when the set of strings changed, so a retune over
    /// the same strings kept the previous tuning's names.</summary>
    [Fact]
    public void TheStringChips_FollowATuningChange_OverTheSameStrings()
    {
        var mask = new StringMaskViewModel(new ProjectState(), FakeLocalization.English());

        mask.SyncChips([0, 1], 6, ["E", "A", "D", "G", "B", "E"]);
        Assert.Equal(["E (6)", "A (5)"], mask.Chips.Select(c => c.Name));

        mask.SyncChips([0, 1], 6, ["D", "A", "D", "G", "B", "E"]);
        Assert.Equal(["D (6)", "A (5)"], mask.Chips.Select(c => c.Name));
    }

    /// <summary>An unchanged row is not rebuilt: the same chip objects stay.</summary>
    [Fact]
    public void AnUnchangedChipRow_IsKept()
    {
        var mask = new StringMaskViewModel(new ProjectState(), FakeLocalization.English());
        string[] tuning = ["E", "A", "D", "G", "B", "E"];
        mask.SyncChips([0, 1], 6, tuning);
        var first = mask.Chips[0];

        mask.SyncChips([1, 0], 6, tuning);

        Assert.Same(first, mask.Chips[0]);
    }
}

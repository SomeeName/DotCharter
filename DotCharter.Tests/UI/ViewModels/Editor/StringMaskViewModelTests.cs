using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The inspector's mask verbs and keyboard readouts, driven on the extracted
///     <see cref="StringMaskViewModel" />: both verbs write the ONE mask on
///     <see cref="ProjectState" /> (never a second selector), and the readouts
///     re-read it when the inspector forwards a change.
/// </summary>
public sealed class StringMaskViewModelTests
{
    [Fact]
    public void Toggle_WritesTheProjectsMask_AndClearDropsIt()
    {
        var state = new ProjectState();
        var vm = new StringMaskViewModel(state, FakeLocalization.KeysOnly());
        // The empty mask reads from the resx, never StringEditMask.ToString()'s
        // hard-coded English "all" (#118).
        Assert.Equal("Inspector_Queue_MaskAll", vm.MaskReadout);

        vm.ToggleStringMaskCommand.Execute(3);
        vm.ToggleStringMaskCommand.Execute(4);

        Assert.True(state.StringEditMask.IsExplicit(3));
        Assert.True(state.StringEditMask.IsExplicit(4));
        Assert.False(state.StringEditMask.IsExplicit(0));
        // No arrangement, so the strings are unnamed: "? (3)", "? (2)" on six.
        Assert.Equal("strings ? (3), ? (2)", new StringMaskViewModel(state, FakeLocalization.English()).MaskReadout);
        Assert.Equal(GuitarStringLabel.MaskReadout(state.StringEditMask, 6, null, FakeLocalization.KeysOnly()),
            vm.MaskReadout);

        vm.ClearStringMaskCommand.Execute(null);

        Assert.True(state.StringEditMask.IsEmpty);
    }

    [Fact]
    public void TheReadouts_FollowTheForwardedChanges()
    {
        var state = new ProjectState();
        var localization = FakeLocalization.KeysOnly();
        var vm = new StringMaskViewModel(state, localization);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        state.PreviewNoteFret = 12;
        vm.NotifyPenFretChanged();
        vm.NotifyMaskChanged();

        Assert.Equal(localization.Format("Inspector_Queue_PenFret", 12), vm.PenFretReadout);
        Assert.Equal([nameof(StringMaskViewModel.PenFretReadout), nameof(StringMaskViewModel.MaskReadout)], raised);
    }
}

using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>the tone preview toggle comes back once, when it first becomes available.</summary>
public sealed class TonePreviewRestoreTests
{
    [Fact]
    public void ASavedOnToggle_TurnsOnOnce_WhenThePreviewBecomesAvailable()
    {
        var restore = new TonePreviewRestore((true, true));
        var on = 0;
        var rs = 0;

        Assert.False(restore.Apply(false, false, () => rs++, () => on++));
        Assert.True(restore.Apply(true, true, () => rs++, () => on++));
        Assert.False(restore.Apply(true, true, () => rs++, () => on++));
        Assert.Equal((1, 1), (on, rs));
    }

    [Fact]
    public void AClickBeforeTheRestore_SupersedesIt()
    {
        var restore = new TonePreviewRestore((true, false));
        var on = 0;
        restore.Cancel();
        Assert.False(restore.Apply(true, true, () => { }, () => on++));
        Assert.Equal(0, on);
    }

    [Fact]
    public void ARocksmithTargetThatIsNotReady_IsNotChosen()
    {
        var restore = new TonePreviewRestore((true, true));
        var rs = 0;
        restore.Apply(true, false, () => rs++, () => { });
        Assert.Equal(0, rs);
    }

    [Fact]
    public void AnOffToggle_DoesNothing()
    {
        var restore = new TonePreviewRestore((false, false));
        Assert.False(restore.Apply(true, true, () => throw new InvalidOperationException(), () => throw new InvalidOperationException()));
    }
}

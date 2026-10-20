using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     The settings footer prints "N unsaved changes", and the rule for that
///     number is a BASELINE COMPARISON: a switch flipped and flipped back reads
///     zero, and a save or a load retakes the baseline. <see cref="SettingsChangeSet" />
///     is the Presentation-side counter every migrated tab reports through.
/// </summary>
public sealed class SettingsChangeSetTests
{
    private sealed class Page
    {
        public bool Toggle;
        public int Number = 4;
        public string Text = "start";
    }

    private static (SettingsChangeSet Set, Page Page) Build()
    {
        var page = new Page();
        var set = new SettingsChangeSet();
        set.Track("tab.Toggle", () => page.Toggle);
        set.Track("tab.Number", () => page.Number);
        set.Track("tab.Text", () => page.Text);
        set.Rebaseline();
        return (set, page);
    }

    [Fact]
    public void AFreshBaseline_CountsNothing()
    {
        var (set, _) = Build();
        Assert.Empty(set.Differences());
    }

    [Fact]
    public void EachChangedSetting_CountsOnce_AndIsNamed()
    {
        var (set, page) = Build();

        page.Toggle = true;
        Assert.Equal(["tab.Toggle"], set.Differences());

        page.Number = 9;
        page.Text = "changed";
        Assert.Equal(["tab.Toggle", "tab.Number", "tab.Text"], set.Differences());
    }

    [Fact]
    public void FlippingASettingBackAgain_LeavesNothingUnsaved()
    {
        var (set, page) = Build();
        page.Toggle = true;
        page.Toggle = false;
        Assert.Empty(set.Differences());
    }

    [Fact]
    public void Rebaselining_IsWhatASaveDoes()
    {
        var (set, page) = Build();
        page.Number = 42;
        Assert.Single(set.Differences());

        set.Rebaseline();
        Assert.Empty(set.Differences());
    }

    [Fact]
    public void ASettingTrackedAfterTheBaseline_IsUnchangedUntilTheNextBaseline()
    {
        var (set, page) = Build();
        set.Track("tab.Late", () => page.Text.Length);
        Assert.Empty(set.Differences());

        set.Rebaseline();
        page.Text = "longer";
        Assert.Contains("tab.Late", set.Differences());
    }

    [Fact]
    public void AnEmptyKey_IsRefused()
    {
        var set = new SettingsChangeSet();
        Assert.Throws<ArgumentException>(() => set.Track("", () => 1));
    }

    [Fact]
    public void ATrackedPropertyChange_RunsTheCallback_AndAnUntrackedOneDoesNot()
    {
        var owner = new Notifying();
        var set = new SettingsChangeSet();
        var calls = 0;
        set.TrackProperties(owner, "tab.", [nameof(Notifying.Volume)], () => calls++);

        owner.Raise(nameof(Notifying.Volume));
        owner.Raise(nameof(Notifying.Status));
        owner.Raise(null);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ACallback_OnAnOwnerThatRaisesNoChanges_IsRefused()
    {
        var set = new SettingsChangeSet();

        Assert.Throws<ArgumentException>(() =>
            set.TrackProperties(new Page(), "tab.", [], () => { }));
    }

    private sealed class Notifying : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public int Volume { get; set; }

        public string Status { get; set; } = "";

        public void Raise(string? name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}

using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The WORKSPACE's resizable height (owner, v0.48.5; one height for the whole
///     region below the routing canvas since W1, 2026-09-16) — its own view model
///     since it left <see cref="PedalboardViewModel" />'s partials. Pure: a fake
///     config, no Avalonia.
/// </summary>
public sealed class RigWorkspaceViewModelTests
{
    /// <summary>A rig with no config service at all — every preset-only test —
    /// still opens the workspace at the shipped height rather than at zero.
    /// <c>AppConfig.RigWorkspaceHeight</c> is NULLABLE precisely so "never
    /// dragged" is distinguishable from "dragged to nothing".</summary>
    [Fact]
    public void Height_defaults_when_nothing_has_been_persisted()
    {
        var bare = new RigWorkspaceViewModel(null);
        Assert.Equal(RigWorkspaceViewModel.DefaultHeight, bare.Height);
        Assert.False(bare.HasUserHeight);

        var config = new FakeConfigurationService();
        Assert.Null(config.AppConfig.RigWorkspaceHeight);
        var fresh = new RigWorkspaceViewModel(config);
        Assert.Equal(RigWorkspaceViewModel.DefaultHeight, fresh.Height);
        Assert.False(fresh.HasUserHeight);
    }

    /// <summary>An install that dragged the tone drawer before W1 keeps that
    /// split: the pre-W1 key is the seed when the new one is absent. Without
    /// this the whole region below the canvas snaps back to the default the
    /// first time the rig is opened after an update.</summary>
    [Fact]
    public void Height_seedsFromThePreW1DrawerHeight_whenTheNewKeyIsAbsent()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.RigToneBrowserHeight = 612;

        var workspace = new RigWorkspaceViewModel(config);

        Assert.Equal(612, workspace.Height);
        Assert.True(workspace.HasUserHeight);
    }

    /// <summary>Dragging the splitter writes the height through to AppConfig,
    /// and a REBUILT rig comes back at it. Without the second half this is a
    /// resize that forgets itself every launch.</summary>
    [Fact]
    public void Height_persists_and_is_restored_on_rebuild()
    {
        var config = new FakeConfigurationService();
        _ = new RigWorkspaceViewModel(config)
        {
            Height = 612
        };

        Assert.Equal(612, config.AppConfig.RigWorkspaceHeight);
        Assert.Equal(612, new RigWorkspaceViewModel(config).Height);
    }

    /// <summary>The bounds hold on the WAY IN as well as the way out: a config
    /// edited by hand (or written by an older build) must not be able to open
    /// the drawer at 4 px or at the full height of the screen. Both ends clamp,
    /// and the clamped value is what persists.</summary>
    [Theory]
    [InlineData(10, RigWorkspaceViewModel.MinHeight)]
    [InlineData(4000, RigWorkspaceViewModel.MaxHeight)]
    public void Height_clamps_on_set_and_on_load(double requested, double expected)
    {
        var config = new FakeConfigurationService();
        var workspace = new RigWorkspaceViewModel(config)
        {
            Height = requested
        };
        Assert.Equal(expected, workspace.Height);
        Assert.Equal(expected, config.AppConfig.RigWorkspaceHeight);

        config.AppConfig.RigWorkspaceHeight = requested;
        Assert.Equal(expected, new RigWorkspaceViewModel(config).Height);
    }

    /// <summary>The view re-lays its workspace row off these two notifications
    /// (<c>AmpFxRigView.OnWorkspaceHeightChanged</c>); a set that lands on the
    /// height already held after a drag raises nothing.</summary>
    [Fact]
    public void Height_raisesHeightAndHasUserHeight_once_per_real_change()
    {
        var workspace = new RigWorkspaceViewModel(new FakeConfigurationService());
        var raised = new List<string?>();
        workspace.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        workspace.Height = 700;
        workspace.Height = 700.2;

        Assert.Equal(
            [nameof(RigWorkspaceViewModel.Height), nameof(RigWorkspaceViewModel.HasUserHeight)],
            raised);
    }

    /// <summary>The floor must leave the drawer's own chrome usable and the
    /// ceiling must leave the routing canvas visible — the two numbers the
    /// view's row Min/MaxHeight are set from.</summary>
    [Fact]
    public void Height_boundsAreOrdered_andLeaveRoomForBothHalves()
    {
        Assert.True(RigWorkspaceViewModel.MinHeight >= 260);
        Assert.True(RigWorkspaceViewModel.MaxHeight > RigWorkspaceViewModel.DefaultHeight);
        Assert.InRange(RigWorkspaceViewModel.DefaultHeight,
            RigWorkspaceViewModel.MinHeight, RigWorkspaceViewModel.MaxHeight);
    }

    /// <summary>The board hands its OWN config service to the workspace, so
    /// both rig hosts read and write the one persisted height.</summary>
    [Fact]
    public void ThePedalboard_ownsOneWorkspace_overItsInjectedConfig()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.RigWorkspaceHeight = 777;

        var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English(), config);

        Assert.Same(rig.Workspace, rig.Workspace);
        Assert.Equal(777, rig.Workspace.Height);
        rig.Workspace.Height = 640;
        Assert.Equal(640, config.AppConfig.RigWorkspaceHeight);
    }
}

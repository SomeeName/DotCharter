using static DotCharter.Tests.Presentation.Settings.GraphicsStubs;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Graphics on the Presentation layer: load, live apply, the
///     unsaved counter, save, revert, the latency wizard's write-through and
///     search. Uses the English resx through <see cref="FakeLocalization" /> so a missing
///     label key surfaces as a raw key in the search catalogue.
/// </summary>
public sealed class GraphicsSettingsViewModelTests
{
    private sealed record Rig(
        GraphicsSettingsViewModel Vm, FakeConfigurationService Config, ProjectState State,
        FakeDevice Device, FakeWizard Wizard);

    private static Rig NewRig(Action<AppConfig>? seed = null)
    {
        var config = new FakeConfigurationService();
        config.AppConfig.MaxFps = 60;
        config.AppConfig.PresentMode = "Fifo";
        config.AppConfig.MsaaSamples = 4;
        seed?.Invoke(config.AppConfig);
        var state = new ProjectState();
        var device = new FakeDevice();
        var wizard = new FakeWizard();
        var vm = new GraphicsSettingsViewModel(config, state, device, FakeLocalization.English(), wizard);
        vm.Load();
        return new Rig(vm, config, state, device, wizard);
    }

    [Fact]
    public void Load_ReadsThePersistedValuesAndSelectsTheMatchingChoices()
    {
        var rig = NewRig(c =>
        {
            c.MaxFps = 144;
            c.PresentMode = "Mailbox";
            c.MsaaSamples = 8;
            c.EnableBloom = false;
        });

        Assert.Equal("144", rig.Vm.SelectedMaxFps!.Label);
        Assert.Same(rig.Vm.PresentModeOptions[1], rig.Vm.SelectedPresentMode);
        Assert.Equal("8×", rig.Vm.SelectedMsaa!.Label);
        Assert.False(rig.Vm.EnableBloom);
        Assert.False(rig.State.EnableBloom);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    /// <summary>Before the device initialises it reports FIFO only; a Save then
    /// must not clobber a persisted Mailbox with the only mode on offer.</summary>
    [Fact]
    public void APersistedModeTheDeviceDoesNotReportYet_StaysSelectedAndSurvivesSave()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.PresentMode = "Mailbox";
        var device = new FakeDevice { SupportedPresentModes = ["Fifo"] };
        var vm = new GraphicsSettingsViewModel(config, new ProjectState(), device, FakeLocalization.English(), new FakeWizard());
        vm.Load();

        Assert.Equal(2, vm.PresentModeOptions.Count);
        vm.Save();

        Assert.Equal("Mailbox", config.AppConfig.PresentMode);
    }

    [Fact]
    public void EditsApplyLive_ThroughTheDeviceSeamAndTheState()
    {
        var rig = NewRig();
        rig.Device.Calls.Clear();

        rig.Vm.SelectedMaxFps = rig.Vm.MaxFpsOptions.Single(o => o.Label == "240");
        rig.Vm.SelectedMsaa = rig.Vm.MsaaOptions[1];
        rig.Vm.EnableVignette = !rig.Vm.EnableVignette;

        Assert.Contains("fps:240", rig.Device.Calls);
        Assert.Contains("msaa:2", rig.Device.Calls);
        Assert.Equal(rig.Vm.EnableVignette, rig.State.EnableVignette);
    }

    [Fact]
    public void EachEdit_CountsOnce_AndPuttingItBackCountsNothing()
    {
        var rig = NewRig();
        var original = rig.Vm.SelectedMsaa;

        rig.Vm.RenderScalePercent += 10;
        rig.Vm.SelectedMsaa = rig.Vm.MsaaOptions[0]; // Off; the rig loads 4×
        rig.Vm.EnableBloom = !rig.Vm.EnableBloom;
        Assert.Equal(3, rig.Vm.UnsavedSettings.Count);

        rig.Vm.RenderScalePercent -= 10;
        rig.Vm.SelectedMsaa = original;
        rig.Vm.EnableBloom = !rig.Vm.EnableBloom;
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    /// <summary>Load rebuilds the three choice lists, so every selection is a
    /// fresh instance for the same choice. Counting by reference would report
    /// three changes on a page nobody touched.</summary>
    [Fact]
    public void AReloadThatReSelectsTheSameChoices_IsNotAChange()
    {
        var rig = NewRig(c => { c.MaxFps = 60; c.PresentMode = "Immediate"; c.MsaaSamples = 2; });
        rig.Vm.Load();

        Assert.Contains(rig.Vm.SelectedMaxFps, rig.Vm.MaxFpsOptions);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    /// <summary>The effect toggles live on ProjectState, which Play Mode's own
    /// settings modal also writes; a change from that side must reach the row
    /// and the counter.</summary>
    [Fact]
    public void AnEffectFlippedOnTheStateElsewhere_IsSeenAndCounted()
    {
        var rig = NewRig();
        var raised = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.State.EnableDepthFog = !rig.State.EnableDepthFog;

        Assert.Contains(nameof(GraphicsSettingsViewModel.EnableDepthFog), raised);
        Assert.Contains(nameof(GraphicsSettingsViewModel.UnsavedSettings), raised);
        Assert.Single(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public void Save_PersistsEverything_AppliesTheFingeringOverlay_AndClearsTheCount()
    {
        var rig = NewRig();
        rig.Vm.SelectedMaxFps = rig.Vm.MaxFpsOptions.Single(o => o.Label == "120");
        rig.Vm.SelectedPresentMode = rig.Vm.PresentModeOptions[2];
        rig.Vm.Show3DFingering = true;
        rig.Vm.EnableShowlightFog = !rig.Vm.EnableShowlightFog;
        var fog = rig.Vm.EnableShowlightFog;

        rig.Vm.Save();

        Assert.Equal(120, rig.Config.AppConfig.MaxFps);
        Assert.Equal("Immediate", rig.Config.AppConfig.PresentMode);
        Assert.True(rig.Config.AppConfig.Show3DFingering);
        Assert.Equal(fog, rig.Config.AppConfig.EnableShowlightFog);
        Assert.Contains("fingering:True", rig.Device.Calls);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public void TheFingeringOverlay_AppliesLive_AndRevertTakesItBack()
    {
        var rig = NewRig();
        rig.Device.Calls.Clear();

        rig.Vm.Show3DFingering = true;
        Assert.Equal("fingering:True", Assert.Single(rig.Device.Calls));
        Assert.False(rig.Config.AppConfig.Show3DFingering); // applied, not saved

        rig.Vm.Revert();
        Assert.Equal("fingering:False", rig.Device.Calls[^1]);
    }

    [Fact]
    public void Revert_PutsThePersistedValuesBack_OnTheScreenAndTheState()
    {
        var rig = NewRig();
        var bloom = rig.Vm.EnableBloom;

        rig.Vm.EnableBloom = !bloom;
        rig.Vm.Revert();

        Assert.Equal(bloom, rig.State.EnableBloom);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public void TheLatencyWizardsResult_ReachesTheSlider_ClampedToTheDeviceMaximum()
    {
        var rig = NewRig();

        rig.Wizard.Apply(42);
        Assert.Equal(42, rig.Vm.VisualLeadMs);

        rig.Wizard.Apply(10_000);
        Assert.Equal(150, rig.Vm.VisualLeadMs);

        rig.Vm.Dispose();
        Assert.False(rig.Wizard.HasSubscribers);
    }

    [Fact]
    public void TheSearchCatalogue_HasNoRawResourceKeys()
    {
        var rig = NewRig();

        foreach (var group in rig.Vm.SearchGroups)
        {
            Assert.DoesNotContain("Settings_", group.Title, StringComparison.Ordinal);
            foreach (var row in group.Rows)
                Assert.DoesNotContain("Settings_", row.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Search_HidesWhatDoesNotMatch_AndOpensAdvancedForAnAdvancedHit()
    {
        var rig = NewRig();
        var bloom = rig.Vm.SearchGroups.SelectMany(g => g.Rows).Single(r => r.Key == "graphics.bloom");
        Assert.False(rig.Vm.IsAdvancedOpen);

        rig.Vm.ApplySearch(bloom.Label);

        Assert.DoesNotContain("graphics.bloom", rig.Vm.HiddenKeys);
        Assert.Contains("graphics.renderScale", rig.Vm.HiddenKeys);
        Assert.Contains(GraphicsSettingsViewModel.GroupKey(0), rig.Vm.HiddenKeys);
        Assert.True(rig.Vm.IsAdvancedOpen);

        rig.Vm.ApplySearch("");
        Assert.Empty(rig.Vm.HiddenKeys);
    }

    /// <summary>
    ///     Every setting the contract lets the view WRITE is counted by the
    ///     footer. Derived by reflection so a new toggle added without its
    ///     tracker entry fails here instead of reading "Everything saved"; the
    ///     old page shipped sixteen effect toggles exactly that way.
    /// </summary>
    [Fact]
    public void EverySettableBoolOnTheContract_CountsAsOneUnsavedChange()
    {
        var settable = typeof(DotCharter.Presentation.Contracts.Settings.IGraphicsSettingsScreen)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && p.CanWrite && p.Name != "IsAdvancedOpen")
            .ToList();
        Assert.True(settable.Count >= 18, $"only {settable.Count} settable toggles found; the walk went vacuous");

        foreach (var property in settable)
        {
            var rig = NewRig();
            property.SetValue(rig.Vm, !(bool)property.GetValue(rig.Vm)!);
            Assert.True(rig.Vm.UnsavedSettings.Count == 1,
                $"{property.Name}: {rig.Vm.UnsavedSettings.Count} unsaved change(s) after one flip");
        }
    }
}

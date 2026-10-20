using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Services.Update;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.Views.Settings;
using JuceSharp.StemSeparation.Runtime;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Settings ▸ Stem models over the REAL view models and an injected adapter list:
///     with no GPU that can run the model, DirectML is listed greyed with its reason
///     and the keyboard steps past it; with two GPUs the GPU picker shows, defaults to
///     the fastest, greys the one too small for the model (its reason laid out) and a
///     keyboard pick persists; the old CUDA runtime row shows while its folder holds
///     bytes. The open dropdowns are laid out for real, because a greyed entry whose
///     reason line collapsed is the "looks live" fault again. With
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, the
///     states are written as <c>stem-*-&lt;culture&gt;.png</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class StemProviderPickerUiPerfSmokeTests : IDisposable
{
    private const ulong Gb = 1UL << 30;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-stem-picker-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WithoutAUsableGpu_ThePickerGreysDirectMlWithItsReason_AndTheKeyboardSkipsIt()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var localization = LocalizationService.Instance;
            var (vm, backend, _) = await Build(
                [new StemGpuAdapter(0, "Intel(R) UHD Graphics 770", 128UL << 20, false),
                 new StemGpuAdapter(1, "Microsoft Basic Render Driver", 0, true)],
                legacyBytes: 0);
            var (window, view) = Show(vm);
            try
            {
                var picker = Named<ComboBox>(view, "StemExecutionProvider");
                Assert.True(picker.Bounds is { Width: > 1, Height: > 1 }, $"the picker laid out as {picker.Bounds}");
                Assert.Equal("CPU", Assert.IsAssignableFrom<IStemExecutionProviderOption>(picker.SelectedItem).Label);
                Assert.False(Named<ComboBox>(view, "StemGpuAdapter").IsEffectivelyVisible,
                    "the GPU picker shows on a machine with no GPU to pick");
                Assert.False(Named<Grid>(view, "StemGpuRuntimeGroup").IsEffectivelyVisible,
                    "the old CUDA runtime row shows with nothing on disk");
                Capture(window, $"stem-models-cpu-{localization.Culture.Name}.png");

                picker.IsDropDownOpen = true;
                Settle(window);
                var directMl = Assert.IsType<ComboBoxItem>(picker.ContainerFromIndex(0));
                Assert.Equal("DirectML", Assert.IsAssignableFrom<IStemExecutionProviderOption>(directMl.DataContext).Label);
                Assert.False(directMl.IsEnabled, "DirectML can be picked although no GPU can run it");
                AssertReasonLaidOut(directMl, localization.Format("Settings_StemModels_DirectMlUnavailable", "3 GB"));
                Capture(window, $"stem-provider-open-{localization.Culture.Name}.png");

                // The keyboard walks past the greyed entry: Up from CPU stays on CPU.
                picker.IsDropDownOpen = false;
                Settle(window);
                Assert.True(picker.Focus(), "the picker refused focus");
                window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
                Settle(window);
                Assert.Equal(StemExecutionProviderOptions.Cpu, backend.PreferredExecutionProvider);
                Assert.Equal(1, picker.SelectedIndex);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WithTwoGpus_TheGpuPickerShows_DefaultsToAutomatic_AndGreysTheOneTooSmall()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var localization = LocalizationService.Instance;
            var (vm, _, config) = await Build(
                [new StemGpuAdapter(0, "Intel(R) UHD Graphics 770", 128UL << 20, false),
                 new StemGpuAdapter(1, "NVIDIA GeForce RTX 3080", 10 * Gb, false),
                 new StemGpuAdapter(2, "NVIDIA GeForce RTX 5070 Ti", 16 * Gb, false)],
                legacyBytes: 4096);
            var (window, view) = Show(vm);
            try
            {
                var provider = Named<ComboBox>(view, "StemExecutionProvider");
                Assert.Equal("DirectML", Assert.IsAssignableFrom<IStemExecutionProviderOption>(provider.SelectedItem).Label);

                var gpu = Named<ComboBox>(view, "StemGpuAdapter");
                Assert.True(gpu.IsEffectivelyVisible && gpu.Bounds is { Width: > 1, Height: > 1 },
                    $"the GPU picker laid out as {gpu.Bounds}");
                Assert.Equal(localization.Format("Settings_StemModels_GpuAuto", "NVIDIA GeForce RTX 5070 Ti · 16 GB"),
                    Assert.IsAssignableFrom<IStemGpuOption>(gpu.SelectedItem).Label);

                var legacy = Named<Button>(view, "StemGpuRemove");
                Assert.True(legacy.IsEffectivelyVisible && legacy.Bounds is { Width: > 1, Height: > 1 },
                    $"the old CUDA runtime's Remove laid out as {legacy.Bounds}");
                Capture(window, $"stem-models-two-gpus-{localization.Culture.Name}.png");

                gpu.IsDropDownOpen = true;
                Settle(window);
                var small = Assert.IsType<ComboBoxItem>(gpu.ContainerFromIndex(3));
                Assert.False(small.IsEnabled, "a GPU too small for the model can be picked");
                AssertReasonLaidOut(small, localization.Format("Settings_StemModels_GpuTooSmallFmt", "3 GB", "128 MB"));
                Capture(window, $"stem-gpu-open-{localization.Culture.Name}.png");

                gpu.IsDropDownOpen = false;
                Settle(window);
                Assert.True(gpu.Focus(), "the GPU picker refused focus");
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Settle(window);
                Assert.Equal("2:NVIDIA GeForce RTX 5070 Ti", config.AppConfig.StemSeparationGpuAdapter);
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Settle(window);
                Assert.Equal("1:NVIDIA GeForce RTX 3080", config.AppConfig.StemSeparationGpuAdapter);

                // ...and the keyboard cannot step onto the greyed one.
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Settle(window);
                Assert.Equal("1:NVIDIA GeForce RTX 3080", config.AppConfig.StemSeparationGpuAdapter);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     #267: signed out of GitHub, the 13 s BS-RoFormer SW (hosted on the app's private
    ///     releases) shows a DISABLED Download with its reason laid out under the row — never a
    ///     live button, and never a row that just lost its verb with no word why.
    /// </summary>
    [Fact]
    public async Task SignedOut_The13sRow_ShowsADisabledDownload_AndItsReason()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var localization = LocalizationService.Instance;
            var (vm, _, _) = await Build(
                [new StemGpuAdapter(0, "NVIDIA GeForce RTX 5070 Ti", 16 * Gb, false)], legacyBytes: 0,
                assets: new SignedOutAssets());
            var (window, view) = Show(vm);
            try
            {
                var reason = view.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Name == "StemModelUnavailableReason" && t.IsEffectivelyVisible);
                var row = Assert.IsAssignableFrom<IStemModelRow>(reason.DataContext);
                Assert.Equal("BS-RoFormer SW 6-stem 13 s (ONNX)", row.DisplayName);
                Assert.Equal(localization["Settings_StemModels_NeedsGitHubSignIn"], reason.Text);
                Assert.True(reason.Bounds is { Width: > 1, Height: > 1 }, $"the reason laid out as {reason.Bounds}");

                var buttons = view.GetVisualDescendants().OfType<Button>()
                    .Where(b => ReferenceEquals(b.DataContext, row) && b.IsEffectivelyVisible).ToList();
                var off = Assert.Single(buttons);
                Assert.Equal("StemModelDownloadUnavailable", off.Name);
                Assert.False(off.IsEffectivelyEnabled, "the 13 s row offers a live Download while signed out");
                Assert.True(off.Bounds is { Width: > 1, Height: > 1 }, $"the disabled Download laid out as {off.Bounds}");
                Assert.Equal(reason.Text, ToolTip.GetTip(off));

                // The public 4 s row beside it keeps its live Download.
                var sw4 = view.GetVisualDescendants().OfType<Button>().Single(b =>
                    b.DataContext is IStemModelRow { DisplayName: "BS-RoFormer SW 6-stem (ONNX)" } &&
                    b.IsEffectivelyVisible);
                Assert.True(sw4.IsEffectivelyEnabled);

                // …and the two are DRAWN differently: a disabled button styled like a live one
                // is the "looks live" fault with extra steps.
                Assert.NotEqual(Ink(sw4), Ink(off));
                Capture(window, $"stem-models-signed-out-{localization.Culture.Name}.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private async Task<(StemModelsSettingsViewModel Vm, StemBackendViewModel Backend, FakeConfigurationService Config)> Build(
        IReadOnlyList<StemGpuAdapter> adapters, int legacyBytes, IGitHubReleaseAssetDownloader? assets = null)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "models"));
        var legacy = new LegacyGpuRuntimeFolder(Path.Combine(_dir, "models"));
        if (legacyBytes > 0)
        {
            Directory.CreateDirectory(legacy.RuntimeDirectory);
            File.WriteAllBytes(Path.Combine(legacy.RuntimeDirectory, "cudnn64_9.dll"), new byte[legacyBytes]);
        }

        var localization = LocalizationService.Instance;
        var config = new FakeConfigurationService();
        config.AppConfig.StemSeparationExecutionProvider = "Cuda";
        var manager = new StemModelManager(config, logger: null, modelsDirectoryOverride: Path.Combine(_dir, "models"),
            releaseAssets: assets);
        var ui = new InlineUiDispatcher();
        var backend = new StemBackendViewModel(manager, config, localization, gpu: new FakeGpuEnvironment(adapters));
        var gpuRuntime = new GpuRuntimeViewModel(localization, ui, legacy);
        var vm = new StemModelsSettingsViewModel(manager, localization, ui, backend, gpuRuntime,
            new StemCacheViewModel(localization, ui));
        vm.Load();
        await backend.RefreshHardwareAsync();
        await gpuRuntime.RefreshStatusAsync();
        return (vm, backend, config);
    }

    private static (Window Window, StemModelsSettingsView View) Show(StemModelsSettingsViewModel vm)
    {
        var view = new StemModelsSettingsView { DataContext = vm };
        var window = new Window
        {
            Width = 1100, Height = 2600, Content = view,
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x07, 0x0E))
        };
        window.Show();
        Settle(window);
        return (window, view);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Color? Ink(Button button) =>
        (button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First().Foreground
            as ISolidColorBrush)?.Color;

    private static void AssertReasonLaidOut(ComboBoxItem item, string text)
    {
        var reason = item.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);
        Assert.True(reason.IsEffectivelyVisible && reason.Bounds is { Width: > 1, Height: > 1 },
            $"the greyed entry's reason laid out as {reason.Bounds}");
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new PngBitmapEncoderOptions());
    }

    private sealed class FakeGpuEnvironment(IReadOnlyList<StemGpuAdapter> adapters) : IStemGpuEnvironment
    {
        public bool IsDirectMlAvailable => true;

        public IReadOnlyList<StemGpuAdapter> Adapters() => adapters;
    }

    private sealed class SignedOutAssets : IGitHubReleaseAssetDownloader
    {
        public bool RequiresSignIn => true;

        public bool IsSignedIn => false;

        public event Action? SignInChanged
        {
            add { }
            remove { }
        }

        public Task<string> DownloadVerifiedAsync(GitHubReleaseAssetRef asset, string destinationPath, string sha256,
            long exactBytes, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
            throw new GitHubSignInRequiredException("signed out");
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}

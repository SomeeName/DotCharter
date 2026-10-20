using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Audio.StemSeparation.GuitarRoles;
using DotCharter.Tests.Services.Audio.GuitarRoles;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Editor.Inspector;
using JuceSharp.AudioIo;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Guitar roles (#260) end to end through the REAL shell: the app's own DI
///     (<see cref="IGuitarRoleStems" /> → <c>GuitarRoleStemService</c>), the real
///     stem mixer and the Mixer inspector tab the user sees, over real WAV stems on
///     disk. The switch must come up LIVE for a splittable guitar, turning it on must
///     replace the guitar strip with Lead + Rhythm, and a mono guitar must show the
///     switch DISABLED with its reason — never live.
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> it writes the PNGs the PR shows
///         (<see cref="NarrowWidthLayout.ArtifactDirectory" />).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class GuitarRolesMixerUiPerfSmokeTests
{
    [Fact]
    public async Task TheMixerTab_OffersTheSplit_PlaysTheHalves_AndRefusesAMonoGuitar() =>
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var vm = services.GetRequiredService<MainWindowViewModel>();
            var state = services.GetRequiredService<ProjectState>();
            foreach (var dialog in vm.Dialogs.AllDialogs)
            {
                if (dialog.IsOpen)
                    dialog.RequestClose();
            }

            vm.CommandPalette.Close();
            vm.IsGameplayPresentation = false;
            vm.SelectedMainTabIndex = 0;
            vm.IsInspectorVisible = true;
            var previousProject = state.ActiveProject;
            var previousPath = state.CurrentProjectPath;
            var dir = Path.Combine(Path.GetTempPath(), "dotcharter-guitarroles-ui-" + Guid.NewGuid().ToString("N"));
            var stems = Path.Combine(dir, "stems");
            Directory.CreateDirectory(stems);
            try
            {
                var centre = CentreSideSplitterTests.Notes(1, 0.2f, 0.23);
                WriteStereo(Path.Combine(stems, "guitar.wav"),
                    CentreSideSplitterTests.Sum(centre, CentreSideSplitterTests.Notes(2, 0.2f, 0.31)),
                    CentreSideSplitterTests.Sum(centre, CentreSideSplitterTests.Notes(3, 0.2f, 0.29)));
                var mono = CentreSideSplitterTests.Notes(4, 0.2f, 0.25);
                WriteStereo(Path.Combine(stems, "guitar-mono.wav"), mono, mono);
                foreach (var name in new[] { "drums", "bass", "vocals" })
                {
                    var noise = CentreSideSplitterTests.Noise(name.Length, 0.05f);
                    WriteStereo(Path.Combine(stems, name + ".wav"), noise, noise);
                }

                NarrowWidthLayout.SeedSong(state);
                var project = state.ActiveProject!;
                project.Stems.Clear();
                foreach (var name in new[] { "drums", "bass", "vocals", "guitar" })
                    project.Stems.Add(new Stem { Name = name, Path = Path.Combine(stems, name + ".wav"), Volume = 1.0 });
                state.CurrentProjectPath = Path.Combine(dir, "song.rscp");

                var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1400 };
                window.Show();
                vm.Inspector.SelectedTabIndex = InspectorViewModel.MixerTabIndex;
                var roles = vm.StemMixer.GuitarRoles ?? throw new InvalidOperationException("The app built the mixer without Guitar roles.");

                // 1. A splittable guitar: the switch comes up live, off.
                await WaitFor(window, () => vm.StemMixer.Tracks.Any(t => t.Name == "guitar") && roles.CanToggle,
                    "the switch never came up live for a splittable guitar");
                // #601: the stems are a section of the Mixer's rail; open it, as its entry's click does.
                window.GetVisualDescendants().OfType<MixerInspectorView>().Single().Rail.Choose("MixerStemsBody");
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var view = window.GetVisualDescendants().OfType<GuitarRolesView>().Single();
                var toggle = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "GuitarRolesToggle");
                Assert.True(toggle.IsEffectivelyVisible && toggle.IsEnabled);
                Assert.True(toggle.Bounds.Width > 1 && toggle.Bounds.Height > 1, $"the switch laid out as {toggle.Bounds}");
                Assert.True(view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuitarRolesCaption").Bounds.Height > 1,
                    "the approximation caption rendered nothing");
                // The Stems card sits below the Levels card: scroll it into the frame.
                BringStemsIntoView(view);
                NarrowWidthLayout.Settle(window);
                view.IsVisible = false;
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, "guitar-roles-0-before");
                view.IsVisible = true;
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, "guitar-roles-1-off");

                // 2. On: the guitar strip is replaced by its two halves.
                toggle.IsChecked = true;
                await WaitFor(window, () => vm.StemMixer.Tracks.Any(t => t.Name == GuitarRoleStems.LeadName) && !roles.IsWorking,
                    "turning the switch on never put Lead + Rhythm in the mixer");
                var names = vm.StemMixer.Tracks.Select(t => t.Name).ToArray();
                Assert.Contains(GuitarRoleStems.RhythmName, names);
                Assert.DoesNotContain("guitar", names);
                Assert.True(project.GuitarRolesSplit);
                Assert.True(toggle.IsChecked);
                Assert.True(roles.HasStatus, "the measured split is not shown");
                BringStemsIntoView(view);
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, "guitar-roles-2-on");

                // 3. A mono guitar: refused, with its reason, never live.
                toggle.IsChecked = false;
                await WaitFor(window, () => vm.StemMixer.Tracks.Any(t => t.Name == "guitar"), "turning it off did not bring the guitar back");
                project.Stems.Single(s => s.Name == "guitar").Path = Path.Combine(stems, "guitar-mono.wav");
                vm.StemMixer.Rebuild();
                await WaitFor(window, () => roles is { IsWorking: false, CanToggle: false } && roles.HasStatus,
                    "a mono guitar never got its verdict");
                Assert.False(toggle.IsEnabled);
                Assert.Equal(DotCharter.Localization.LocalizationService.Instance["Mixer_GuitarRoles_Mono"], roles.Status);
                var status = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuitarRolesStatus");
                Assert.True(status.IsEffectivelyVisible && status.Bounds.Height > 1, "the reason line rendered nothing");
                BringStemsIntoView(view);
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, "guitar-roles-3-mono");
            }
            finally
            {
                if (state.ActiveProject is { } p) p.GuitarRolesSplit = false;
                state.ActiveProject = previousProject;
                state.CurrentProjectPath = previousPath;
                BestEffortDelete.Directory(dir);
            }
        }, CancellationToken.None);

    /// <summary>The Stems card sits below the Levels card: scroll its strips (and the
    /// switch above them) into the frame for the capture.</summary>
    private static void BringStemsIntoView(GuitarRolesView view)
    {
        var strips = ((Panel)view.Parent!).Children.OfType<ItemsControl>().Single();
        strips.BringIntoView();
    }

    private static async Task WaitFor(Window window, Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"Deadlock detector: {what}.");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        NarrowWidthLayout.Settle(window);
    }

    private static void WriteStereo(string path, float[] left, float[] right)
    {
        using var writer = new WavWriter(path, 44100, 2, WavSampleFormat.Pcm16);
        writer.WritePlanar([left, right]);
    }
}

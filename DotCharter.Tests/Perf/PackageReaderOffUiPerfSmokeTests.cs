using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Fakes.Settings;
using DotCharter.Services.Plugins;
using DotCharter.Services.Infrastructure.Features;
using DotCharter.Services.Library;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Onboarding;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Library;
using DotCharter.UI.Views.PlayMode;
using DotCharter.UI.Views.Settings;
using DotCharter.UI.Views.Shell.Menus;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The app WITHOUT the package reader (#887), booted for real: <c>DOTCHARTER_PACKAGE_READER=off</c>
///     is read by <c>App.OnFrameworkInitializationCompleted</c>, so the dispatch's app is the build a
///     public user gets. Its Library, Song Select and Settings ▸ Files construct, attach and lay out,
///     and every package-reading entry is absent. The pure half is <c>UI/PackageFeaturesGateTests</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class PackageReaderOffUiPerfSmokeTests
{
    private const double Width = 1600;
    private const double Height = 1000;

    private static async Task WithReaderOff(Action<IServiceProvider> body)
    {
        var saved = Environment.GetEnvironmentVariable(AppPlugins.PackageReaderEnvironmentVariable);
        Environment.SetEnvironmentVariable(AppPlugins.PackageReaderEnvironmentVariable, "off");
        try
        {
            await HeadlessSessionFixture.Dispatch(() =>
            {
                var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
                body(services);
            }, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPlugins.PackageReaderEnvironmentVariable, saved);
        }
    }

    private static void LaidOut(Control view, Action assert)
    {
        var window = new Window { Width = Width, Height = Height, Content = view };
        window.Show();
        try
        {
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0, 0, Width, Height));
            view.UpdateLayout();
            Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{view.GetType().Name} laid out as {view.Bounds}");
            assert();
        }
        finally
        {
            window.Close();
        }
    }

    private static Control Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"{root.GetType().Name} has no {name}");

    [Fact]
    public async Task TheBootedApp_ReadsNoPackages_AndItsLibraryAndSongSelectLayOut()
    {
        await WithReaderOff(services =>
        {
            Assert.Equal(PackageFeatures.None, services.GetRequiredService<IPackageFeatures>());
            var extensions = services.GetServices<ILibraryPackageSource>().SelectMany(s => s.FileExtensions).ToList();
            Assert.Equal([".dcsong"], extensions);
            Assert.Null(services.GetService<DotCharter.Tone.Ai.IToneAnchorLibrary>());
            Assert.False(services.GetRequiredService<OnboardingWizardViewModel>().HasLibraryStep);

            var vm = services.GetRequiredService<MainWindowViewModel>();
            Assert.False(vm.Project.PackageFeatures.ReadsGamePackages);

            LaidOut(new LibraryView { DataContext = new LibraryScreen(vm.SongBrowser) }, () => { });

            var play = new PlayModeView { DataContext = vm.PlayMode };
            LaidOut(play, () =>
            {
                vm.PlayMode.CurrentScreen = PlayModeScreen.SongSelect;
                play.UpdateLayout();
                var songSelect = Named(play, "SongSelectScreenRoot");
                Assert.True(songSelect.Bounds is { Width: > 1, Height: > 1 }, $"Song Select laid out as {songSelect.Bounds}");
                vm.PlayMode.CurrentScreen = PlayModeScreen.Start;
            });
        });
    }

    [Fact]
    public async Task SettingsFiles_FromTheBootedApp_HasNoPackageGroups_ButKeepsTheCompanionPackage()
    {
        await WithReaderOff(services =>
        {
            using var scope = services.CreateScope();
            var files = scope.ServiceProvider.GetRequiredService<IFilesSettingsScreen>();
            var view = new FilesSettingsView { DataContext = files };

            LaidOut(view, () =>
            {
                Assert.False(Named(view, "PsarcHardestOnlyRow").IsEffectivelyVisible, "the package open options are still shown");
                var companion = view.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Single(t => Equals(t.GetValue(Avalonia.Automation.AutomationProperties.NameProperty),
                        DotCharter.Localization.LocalizationService.Instance["Settings_SaveCompanionPsarc_RowLabel"]));
                Assert.True(companion.IsEffectivelyVisible && companion.Bounds.Width > 1, "the companion package (the writer) went with the reader");
            });

            using var dialogScope = services.CreateScope();
            Assert.False(dialogScope.ServiceProvider.GetRequiredService<IToneDesignerScreen>().CanImportFromPackages);
        });
    }

    [Fact]
    public async Task TheFilesFake_PackageReaderOff_HidesTheSameRows()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new FilesSettingsView { DataContext = FakeFilesSettingsScreen.PackageReaderOff() };
            LaidOut(view, () => Assert.False(Named(view, "PsarcHardestOnlyRow").IsEffectivelyVisible));
            var on = new FilesSettingsView { DataContext = FakeFilesSettingsScreen.Default() };
            LaidOut(on, () => Assert.True(Named(on, "PsarcHardestOnlyRow").IsEffectivelyVisible));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task APackageMarkedMenuRow_IsRemoved_OnlyWithoutTheReader()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            static MenuItem Menu()
            {
                var root = new MenuItem { Header = "File" };
                var reads = new MenuItem { Header = "Open game package" };
                ContestedMenu.SetFeature(reads, PackageFeatures.GamePackagesId);
                root.Items.Add(new MenuItem { Header = "Export Rocksmith" });
                root.Items.Add(reads);
                return root;
            }

            var off = Menu();
            var on = Menu();

            Assert.Equal(1, ContestedMenu.Prune(off, ContestedFeatures.Enabled, PackageFeatures.None));
            Assert.Equal(0, ContestedMenu.Prune(on, ContestedFeatures.Enabled, PackageFeatures.All));
            Assert.Equal(["Export Rocksmith"], off.Items.OfType<MenuItem>().Select(m => (string)m.Header!));
            Assert.Equal(2, on.Items.Count);
        }, CancellationToken.None);
    }
}

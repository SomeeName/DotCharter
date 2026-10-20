using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tonelab;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Tone Designer's "Preview in: DotCharter rig / Rocksmith" switch, laid out in the
///     real dialog host: the selector measures, Rocksmith is DISABLED with its reason printed
///     while no tonelab session is ready (a capability the app cannot honour never looks live),
///     and enabled with no reason once one is.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneDesignerPreviewTargetUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;

    [Fact]
    public async Task WithoutASession_RocksmithIsGreyed_AndItsReasonIsOnScreen()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = CreateViewModel(new FakeTonelabSession { State = TonelabSessionState.Disconnected });
            var dialog = new ToneDesignerDialogView { DataContext = vm };

            WithLaidOutDialog(dialog, () =>
            {
                var panel = Named<StackPanel>(dialog, "PreviewTargetPanel");
                Assert.True(panel.Bounds.Width > 1 && panel.Bounds.Height > 1, $"the preview target selector laid out {panel.Bounds}");

                var clone = Named<RadioButton>(dialog, "PreviewTargetClone");
                var inGame = Named<RadioButton>(dialog, "PreviewTargetInGame");
                Assert.True(clone.IsChecked);
                Assert.True(clone.IsEffectivelyEnabled);
                Assert.False(inGame.IsEffectivelyEnabled);
                Assert.True(inGame.Bounds.Width > 1, "the disabled Rocksmith choice must still be SHOWN");

                var reason = Named<TextBlock>(dialog, "InGameReason");
                Assert.True(reason.IsEffectivelyVisible);
                Assert.True(reason.Bounds.Height > 1, $"the reason laid out {reason.Bounds}");
                Assert.Equal(((DotCharter.Presentation.Contracts.Dialogs.IToneDesignerScreen)vm).PreviewTargets.InGameUnavailableReason, reason.Text);
                Assert.False(string.IsNullOrWhiteSpace(reason.Text));
                Assert.False(Named<TextBlock>(dialog, "InGameStatus").IsEffectivelyVisible);
                var reconnect = Named<Button>(dialog, "InGameReconnect");
                Assert.True(reconnect.IsEffectivelyVisible && reconnect.IsEffectivelyEnabled, "Reconnect must be offered while no game is attached");
                Assert.True(reconnect.Bounds.Width > 1, $"Reconnect laid out {reconnect.Bounds}");
            });
            vm.Dispose();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WithAReadySession_RocksmithCanBeChosen()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = CreateViewModel(new FakeTonelabSession { State = TonelabSessionState.Ready });
            var dialog = new ToneDesignerDialogView { DataContext = vm };

            WithLaidOutDialog(dialog, () =>
            {
                var inGame = Named<RadioButton>(dialog, "PreviewTargetInGame");
                Assert.True(inGame.IsEffectivelyEnabled);
                Assert.False(Named<TextBlock>(dialog, "InGameReason").IsEffectivelyVisible);

                inGame.IsChecked = true;
                Assert.True(((DotCharter.Presentation.Contracts.Dialogs.IToneDesignerScreen)vm).PreviewTargets.IsInGame);
                Assert.False(Named<RadioButton>(dialog, "PreviewTargetClone").IsChecked);
            });
            vm.Dispose();
        }, TestContext.Current.CancellationToken);
    }

    private static T Named<T>(Control root, string name) where T : Control
    {
        var control = root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        Assert.True(control is not null, $"the Tone Designer has no {name}");
        return control!;
    }

    /// <summary>Hosted in a real <see cref="DialogContainerView" />, where the dialog grammar's styles live.</summary>
    private static void WithLaidOutDialog(Control dialog, Action assert)
    {
        var host = new DialogContainerView();
        host.DialogPanel.Children.Clear();
        host.DialogPanel.Children.Add(dialog);
        var window = new Window { Width = HostWidth, Height = HostHeight, Content = host };
        window.Show();
        try
        {
            host.Measure(new Size(HostWidth, HostHeight));
            host.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            assert();
        }
        finally
        {
            window.Close();
        }
    }

    private static ToneDesignerViewModel CreateViewModel(ITonelabSession session)
    {
        var project = new ChartProject();
        project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead" });
        var tone = RsToneAuthoring.CreateDefaultTone("clean", "Clean", RsGearCatalog.Shared);
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "Clean", Json = RsToneJsonWriter.Write(tone) });
        var chain = new FakeDspChainService([]);
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var loc = DotCharter.Localization.LocalizationService.Instance;
        var services = new ChartEditDialogServices(RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy,
            history, new ProjectState { ActiveProject = project }, new FakeConfigurationService(), loc, logger);
        return new ToneDesignerViewModel(services, new FakeFileStorageService(), FakeTonelabSession.Targets(new TonePreviewSession(chain, new DcRigApplier(chain)), session),
            new ToneDesignerTools(new DotCharter.Services.Editing.Tones.ToneGenerationRun(history, new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter(), logger),
                new DotCharter.Presentation.Dialogs.DialogsSlot()),
            new DialogMemory(), DotCharter.Services.Plugins.PackageFeatures.All);
    }
}

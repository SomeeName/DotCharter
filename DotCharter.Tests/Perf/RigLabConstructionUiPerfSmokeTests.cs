using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using DotCharter.RigLab;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The standalone rig's window, built over a real <see cref="RigLabHost" />
///     with no audio device: construct, attach to a shown window, lay out every
///     tab, and assert each measured NON-ZERO — a view whose styles failed still
///     arranges, it just has no size.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigLabConstructionUiPerfSmokeTests
{
    [Fact]
    public Task TheWindow_BuildsAndLaysOutEveryTab() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "riglab-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            using var config = new RigLabConfigService(Path.Combine(dir, RigLabConfigService.FileName), TimeSpan.Zero);
            using var host = new RigLabHost(config, new GatedInlineDispatcher(), useNullAudio: true,
                namLibraryRoot: Path.Combine(dir, "nam"));
            var shell = new RigLabShellViewModel(host.Screen, host.Plugins, host.Settings, host.Ai);
            var window = new RigLabWindow { DataContext = shell, Width = 1440, Height = 900 };
            window.Show();
            try
            {
                Settle(window);
                AssertMeasured(window, "RigLabBar");
                AssertMeasured(window, "RigLabTabs");
                Assert.False(host.Screen.ShowCloseButton, "RigLab's window is the rig's only host; its ✕ is the window's");

                foreach (var (tab, panel, view) in new[]
                         {
                             (RigLabTab.Rig, "RigLabRigPanel", "RigLabRig"),
                             (RigLabTab.Plugins, "RigLabPluginsPanel", "RigLabPlugins"),
                             (RigLabTab.Audio, "RigLabAudioPanel", "RigLabAudio"),
                             (RigLabTab.Ai, "RigLabAiPanel", "RigLabAi")
                         })
                {
                    shell.Tab = tab;
                    Settle(window);
                    Assert.True(Find(window, panel).IsVisible, $"{tab}: its panel is not the one showing");
                    AssertMeasured(window, view);
                    Capture(window, $"riglab-{tab.ToString().ToLowerInvariant()}.png");
                }

                // The rig itself, not just its host panel.
                shell.Tab = RigLabTab.Rig;
                Settle(window);
                var rig = Assert.IsType<AmpFxRigView>(Find(window, "RigLabRig"));
                Assert.True(rig.GetVisualDescendants().OfType<Control>().Count() > 50,
                    "the rig view built almost nothing — its templates did not apply");
                AssertMeasured(window, "AudioSettingsRows", tab: RigLabTab.Audio, shell);
                // The row labels are plain TextBlocks: they read only if the view
                // hands them the design system's ink. Fluent's default is dark, and
                // on the dark ground that was invisible in the first capture.
                var rows = Assert.IsType<Grid>(Find(window, "AudioSettingsRows"));
                var labels = rows.Children.OfType<TextBlock>().Where(t => Grid.GetColumn(t) == 0).ToList();
                Assert.True(labels.Count >= 5, $"only {labels.Count} labels in the audio rows");
                foreach (var label in labels)
                {
                    var ink = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(label.Foreground).Color;
                    var luminance = (0.2126 * ink.R + 0.7152 * ink.G + 0.0722 * ink.B) / 255.0;
                    Assert.True(luminance > 0.5, $"'{label.Text}' is drawn in {ink} — unreadable on the dark ground");
                }
                // The Plugins tab is the SHARED rack; a fresh riglab.json has no
                // chain, so it shows the empty well (the slot list is hidden
                // until there is a row — Perf/AmpFxRackUiPerfSmokeTests lays out
                // a full one).
                AssertMeasured(window, "RigLabVstRack", tab: RigLabTab.Plugins, shell);
                AssertMeasured(window, "VstRackEmpty", tab: RigLabTab.Plugins, shell);
                AssertMeasured(window, "VstRackAddButton", tab: RigLabTab.Plugins, shell);
                AssertMeasured(window, "AiProviderCard", tab: RigLabTab.Ai, shell);
                AssertMeasured(window, "AiTone3000Card", tab: RigLabTab.Ai, shell);

                // The Codex CLI's model presets: hidden for Claude Code, and for
                // Codex a panel whose two buttons measure — set on the view model
                // the window really binds, so the flag, binding and markup are one path.
                host.Ai.ProviderKind = DotCharter.Tone.Ai.AiProviderKind.ClaudeCli;
                Settle(window);
                Assert.False(Find(window, "AiCodexPresets").IsEffectivelyVisible, "Codex presets shown for Claude Code");
                // Claude Code's three presets (Haiku, Opus, Sonnet) all measure.
                AssertMeasured(window, "AiClaudePresets", tab: RigLabTab.Ai, shell);
                // The Reasoning row (#644): the app's row, laid out beside the model it is sent with.
                AssertMeasured(window, "AiEffortRow", tab: RigLabTab.Ai, shell);
                AssertMeasured(window, "AiEffortPicker", tab: RigLabTab.Ai, shell);
                AssertMeasured(window, "AiEffortDesc", tab: RigLabTab.Ai, shell);
                var claudePresets = Assert.IsAssignableFrom<Panel>(Find(window, "AiClaudePresets"));
                var claudeButtons = claudePresets.Children.OfType<Button>().ToList();
                Assert.Equal(3, claudeButtons.Count);
                foreach (var button in claudeButtons)
                {
                    Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0,
                        $"a Claude preset button measured {button.Bounds}");
                }

                host.Ai.ProviderKind = DotCharter.Tone.Ai.AiProviderKind.CodexCli;
                AssertMeasured(window, "AiCodexPresets", tab: RigLabTab.Ai, shell);
                var codexPresets = Assert.IsAssignableFrom<Panel>(Find(window, "AiCodexPresets"));
                var presetButtons = codexPresets.Children.OfType<Button>().ToList();
                Assert.Equal(2, presetButtons.Count);
                foreach (var button in presetButtons)
                {
                    Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0,
                        $"a Codex preset button measured {button.Bounds}");
                }

                Capture(window, "riglab-ai-codex.png");
                host.Ai.ProviderKind = DotCharter.Tone.Ai.AiProviderKind.None;
                Settle(window);
                Assert.False(Find(window, "AiEffortRow").IsEffectivelyVisible, "Reasoning row shown with no provider");

                // "Design a tone" in the rig's Tones mode: the SHARED core panel,
                // through the window's own template — and none of DotCharter's
                // matcher blocks, which need the charting stack.
                shell.Tab = RigLabTab.Rig;
                host.Rig.IsBrowserOpen = true;
                var design = Assert.IsType<ToneDesignCoreViewModel>(host.Rig.Browser.ToneDesign);
                design.IsExpanded = true;
                Settle(window);
                var core = Assert.IsType<ToneDesignCorePanel>(Find(window, "ToneDesignCorePanelRoot"));
                AssertMeasured(window, "ToneDesignRoot");
                AssertMeasured(window, "ToneDesignSetupBanner"); // no provider configured in a fresh riglab.json
                // RigLab has no extras to show: the slot is empty, so the host is
                // never templated (it sits in the thread's scroller, which a
                // not-configured panel does not lay out at all).
                Assert.Null(core.ExtrasTemplate);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name == "ToneMatchBlock");
                Capture(window, "riglab-tones-design.png");

                // …and the one route that needs no provider: the setup banner's
                // "Use ChatGPT (copy & paste)" opens the copy & paste block, over
                // the host's REAL design service and clipboard adapter.
                AssertMeasured(window, "ToneDesignUseManual");
                design.OpenManualCommand.Execute(null);
                design.Description = "a glassy blackface clean";
                Settle(window);
                AssertMeasured(window, "ToneDesignManual");
                AssertMeasured(window, "ToneDesignCopyPrompt");
                AssertMeasured(window, "ToneDesignReply");
                AssertMeasured(window, "ToneDesignImport");
                Assert.True(design.CopyPromptCommand.CanExecute(null), "RigLab's designer has no clipboard to copy to");
                Assert.False(Find(window, "ToneDesignRun").IsVisible, "Design is drawn with no provider to answer it");
                Capture(window, "riglab-tones-design-chatgpt.png");
            }
            finally
            {
                window.Close();
                BestEffortDelete.Directory(dir);
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>The rig-only theme resolves: every include in it names a file
    /// the rig assembly really ships.</summary>
    [Fact]
    public Task TheRigTheme_Loads() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var include = new StyleInclude(new Uri("avares://DotCharter.RigLab/"))
            {
                Source = new Uri("avares://DotCharter.Tone.UI/Styles/RigTheme.axaml")
            };
            var loaded = Assert.IsAssignableFrom<Avalonia.Styling.Styles>(include.Loaded);
            Assert.True(loaded.Count >= 10, $"RigTheme.axaml merged only {loaded.Count} style sets");
        }, TestContext.Current.CancellationToken);

    /// <summary>With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
    /// <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, writes the tab as a PNG for a design
    /// review; otherwise nothing (the assertions above run either way).</summary>
    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    private static Control Find(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no control named {name} in the RigLab window");

    private static void AssertMeasured(Window window, string name, RigLabTab? tab = null,
        RigLabShellViewModel? shell = null, bool allowZeroHeight = false)
    {
        if (tab is { } t && shell is not null)
        {
            shell.Tab = t;
            Settle(window);
        }

        var control = Find(window, name);
        Assert.True(control.Bounds.Width > 0, $"{name} measured zero wide");
        if (!allowZeroHeight) Assert.True(control.Bounds.Height > 0, $"{name} measured zero high");
    }
}

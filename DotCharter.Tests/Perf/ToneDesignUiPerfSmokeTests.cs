using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;
using DotCharter.Tests.Presentation.Settings;
using DotCharter.UI.Views.Settings;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Builds the AI tone-design surfaces for real — the rig drawer's
///     <see cref="ToneDesignPanel" /> and the Settings ▸ AI tab — attaches each
///     to a window and lays it out.
///
///     <para>
///         Construction alone would not be enough. A view whose styles failed
///         still ARRANGES; it just has no size, and that is the shape every one
///         of the five silent XAML faults in this repo takes (an unresolvable
///         <c>StaticResource</c>, a <c>StringFormat</c> given a Binding, a
///         <c>RelayCommand&lt;T&gt;</c> fed a literal CommandParameter — which
///         throws during LAYOUT, not construction — a view that renders as
///         "Not Found: …", and a <c>Space*</c> token in a <c>Padding</c>). So
///         every case here measures.
///     </para>
///     <para>
///         <b>And one case is about a cost, not a fault.</b> The collapsed panel
///         must realize its HEADER and nothing else. The body lives in a
///         <c>ContentControl</c>'s <c>ContentTemplate</c> precisely so that a
///         drawer open does not pay for a panel nobody asked for; move that
///         markup inline and the template is instantiated with the drawer, the
///         assertion below goes red, and the drawer's first-layout budget grows
///         for a surface that is not even on screen.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneDesignUiPerfSmokeTests
{
    private const double Width = 520;
    private const double Height = 900;

    // ---- fakes ----------------------------------------------------------------

    private sealed class StubDesignService : IToneDesignService
    {
        /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
        public Task<DotCharter.Tone.Ai.ToneDesignManualPrompt> BuildManualPromptAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, CancellationToken ct) =>
            throw new NotSupportedException("this fake never builds a copy & paste prompt");

        /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
        public Task<DotCharter.Tone.Ai.ToneDesignResult> ImportReplyAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, string reply, IProgress<string>? status,
            CancellationToken ct) =>
            throw new NotSupportedException("this fake never imports a pasted answer");

        public bool IsConfigured { get; init; } = true;

        public event EventHandler? ConfiguredChanged;

        public void NotifyConfigurationChanged() => ConfiguredChanged?.Invoke(this, EventArgs.Empty);

        public Task<ToneDesignResult> DesignAsync(
            ToneDesignRequest request, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("the smoke tests never run a design");
    }

    private sealed class StubSecretStore : IAiSecretStore
    {
        public string? Read(AiProviderKind kind) => "stored";
        public void Write(AiProviderKind kind, string? secret) { }
        public bool IsPersistent => true;
    }

    private sealed class StubFactory : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) =>
            throw new NotSupportedException("the smoke tests never probe");

        public bool Supports(AiProviderKind kind)
            => kind is AiProviderKind.Claude or AiProviderKind.OpenAiCompatible;
    }

    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    private static ToneDesignViewModel NewPanelVm(bool configured = true)
    {
        var vm = new ToneDesignViewModel(
            () => new StubDesignService { IsConfigured = configured },
            _ => { },
            () => new DspTonePreset("live", "Live", DspToneCategory.Clean, "", Rig("amp"), DspToneOrigin.User),
            (name, category, _, rig) => new DspTonePreset("s", name, category, "", rig, DspToneOrigin.User), LocalizationService.Instance,
            id => id);
        return vm;
    }

    /// <summary>A finished design, so the result block's own templates — the
    /// confidence pill, the warnings and the four actions — are REALIZED. An
    /// unresolvable resource inside a block nothing ever shows is exactly the
    /// fault that ships.</summary>
    private static void GiveItAResult(ToneDesignViewModel vm)
    {
        var result = new ToneDesignResult(
            new DspTonePreset("ai", "Rat Lead", DspToneCategory.Lead, "", Rig("rat", "amp", "cab"),
                DspToneOrigin.User),
            "A Rat into a clean power amp keeps the mids forward.",
            0.84,
            ["gain clamped to 10"],
            "{}",
            new AiUsage("m", 1, 1));
        vm.SetResultForTests(result, "Rat → Amp → Cab");
    }

    // ---- the panel ------------------------------------------------------------

    [Fact]
    public async Task TheCollapsedPanel_RealizesItsHeaderAndNothingElse()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm();
            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                var header = Find(panel, "ToneDesignHeader");
                Assert.True(header.Bounds is { Width: > 1, Height: > 1 },
                    $"the panel header laid out as {header.Bounds} — it rendered nothing");

                var host = Find(panel, "ToneDesignBodyHost");
                var realized = host.GetVisualDescendants().Count();
                Assert.True(realized == 0,
                    $"the COLLAPSED panel realized {realized} body elements. The body belongs in the "
                    + "ContentControl's ContentTemplate — inline markup is built with the drawer and "
                    + "adds its first layout to every drawer open.");
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheExpandedPanel_BuildsAttachesAndMeasuresNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm();
            vm.IsExpanded = true;
            GiveItAResult(vm);

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                // With an answer up the composer's ONE box is the refinement and
                // its button is Tweak; the prompt and Design are the same slot's
                // other state and must be out of the way.
                foreach (var name in new[]
                         {
                             "ToneDesignBody", "ToneDesignTweak", "ToneDesignRefine",
                             "ToneDesignResult", "ToneDesignHearIt", "ToneDesignChain"
                         })
                {
                    var control = Find(panel, name);
                    Assert.True(control.IsVisible, $"'{name}' realized but is not visible");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
                        $"'{name}' laid out as {control.Bounds} — its styles failed silently");
                }

                Assert.False(Find(panel, "ToneDesignPrompt").IsVisible, "two boxes in the composer");
                Assert.False(Find(panel, "ToneDesignRun").IsVisible, "Design and Tweak are both up");

                // The setup banner is the OTHER branch and must be out of the way.
                Assert.False(Find(panel, "ToneDesignSetupBanner").IsVisible);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoProviderConfigured_ThePanelIsTheSetupLineAlone()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(configured: false);
            vm.IsExpanded = true;

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                var banner = Find(panel, "ToneDesignSetupBanner");
                Assert.True(banner.IsVisible);
                Assert.True(banner.Bounds is { Width: > 1, Height: > 1 },
                    $"the setup banner laid out as {banner.Bounds} — it rendered nothing");

                // Not merely disabled: a capability the app cannot honour must
                // not be on screen looking live.
                Assert.False(Find(panel, "ToneDesignBody").IsVisible);

                // The one route that needs no provider has its door on the banner
                // (Perf/ToneDesignManualRouteUiPerfSmokeTests walks through it).
                var door = Find(panel, "ToneDesignUseManual");
                Assert.True(door.IsEffectivelyVisible && door.Bounds is { Width: > 1, Height: > 1 },
                    $"the banner's ChatGPT door laid out as {door.Bounds}");
            });
        }, CancellationToken.None);
    }

    // ---- the settings tab -----------------------------------------------------

    [Fact]
    public async Task TheAiSettingsTab_BuildsAttachesAndMeasuresNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var config = new FakeConfigurationService();
            config.AppConfig.Ai = new AiProviderSettings(
                AiProviderKind.OpenAiCompatible, "llama", "http://localhost:1234");
            var vm = AiTabRig.Create(config, new StubSecretStore(), new StubFactory());
            vm.Load();

            var tab = new AiSettingsView { DataContext = vm };
            AttachAndLayout(tab, () =>
            {
                Assert.True(tab.Bounds is { Width: > 1, Height: > 1 },
                    $"the AI settings view laid out as {tab.Bounds} — it rendered nothing");

                // The OpenAI-compatible branch is the one with the most markup:
                // base URL, the two preset buttons and the key row.
                foreach (var name in new[]
                         {
                             "AiProviderGroup", "AiProviderPicker", "AiModelBox",
                             "AiBaseUrlBox", "AiApiKeyBox", "AiTestButton", "AiKeyState"
                         })
                {
                    var control = Find(tab, name);
                    Assert.True(control.IsVisible, $"'{name}' realized but is not visible");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
                        $"'{name}' laid out as {control.Bounds} — its styles failed silently");
                }

                Assert.False(Find(tab, "AiUnavailableNote").IsVisible);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithoutTheAiServices_TheTabSaysSoOnScreen()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = AiTabRig.Create(new FakeConfigurationService());
            vm.Load();

            var tab = new AiSettingsView { DataContext = vm };
            AttachAndLayout(tab, () =>
            {
                var note = Find(tab, "AiUnavailableNote");
                Assert.True(note.IsVisible, "a build with no AI services showed no reason");
                Assert.True(note.Bounds is { Width: > 1, Height: > 1 },
                    $"the unavailable note laid out as {note.Bounds} — it rendered nothing");
                Assert.False(Find(tab, "AiProviderPicker").IsEnabled);
            });
        }, CancellationToken.None);
    }

    // ---- the match-model block (Settings ▸ AI) --------------------------------

    /// <summary>
    ///     The block that gives a real user a way to GET the rig estimator.
    ///     It is hidden without a downloader, so the sweep in
    ///     <c>SettingsConstructionUiPerfSmokeTests</c> — which lays every tab out
    ///     with NO DataContext — walks straight past it. This is the only case
    ///     that measures it.
    /// </summary>
    [Fact]
    public async Task TheMatchModelBlock_BuildsAttachesAndMeasuresNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var downloads = new FakeRigEstimatorModelDownloadService();
            var vm = AiTabRig.Create(new FakeConfigurationService(), downloads: downloads);
            vm.Load();

            var tab = new AiSettingsView { DataContext = vm };
            AttachAndLayout(tab, () =>
            {
                foreach (var name in new[]
                         {
                             "AiMatchModelGroup", "AiMatchModelStatus",
                             "AiMatchModelDownload", "AiMatchModelOpenFolder", "AiMatchModelRefresh",
                             "AiMatchLibraryStatus", "AiMatchLibraryDownload"
                         })
                {
                    var control = Find(tab, name);
                    Assert.True(control.IsVisible, $"'{name}' realized but is not visible");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
                        $"'{name}' laid out as {control.Bounds} — its styles failed silently");
                }

                // A status line that measured but says nothing is the failure
                // this block exists to prevent: "IsAvailable is false and nobody
                // says why" is exactly the state it replaces.
                Assert.NotEqual(string.Empty, vm.Match.ModelStatus);
                Assert.NotEqual(string.Empty, vm.Match.LibraryStatus);

                // The Cancel twins are realized-but-hidden, which is the state a
                // `IsVisible` binding gets wrong silently.
                Assert.False(Find(tab, "AiMatchModelCancel").IsVisible);
                Assert.False(Find(tab, "AiMatchLibraryCancel").IsVisible);
            });
        }, CancellationToken.None);
    }

    /// <summary>Without a downloader the whole block stays off screen rather
    /// than offering a button that cannot work.</summary>
    [Fact]
    public async Task WithNoDownloader_TheMatchModelBlockIsNotShown()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = AiTabRig.Create(new FakeConfigurationService());
            vm.Load();

            var tab = new AiSettingsView { DataContext = vm };
            AttachAndLayout(tab, () =>
                Assert.False(Find(tab, "AiMatchModelGroup").IsVisible));
        }, CancellationToken.None);
    }

    // ---- helpers --------------------------------------------------------------

    private static Control Find(Control root, string name)
    {
        var control = root.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == name);
        Assert.True(control != null,
            $"'{name}' is not in the visual tree — the surface never realized (a XAML fault or a rename).");
        return control!;
    }

    private static void AttachAndLayout(Control view, Action assert)
    {
        var window = new Window { Width = Width, Height = Height, Content = view };
        // Show() roots the tree and raises OnAttachedToLogicalTree, which is
        // where a Button calls CanExecute; the layout pass is driven through the
        // VIEW because a headless Window measured directly reports
        // "Invalid size returned for Measure".
        window.Show();
        try
        {
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0, 0, Width, Height));
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}

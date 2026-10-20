using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Builds the tone-design panel's "Match from audio" block for real,
///     attaches it to a window and lays it out.
///
///     <para>
///         Construction is not enough. A view whose styles failed still
///         ARRANGES; it simply has no size, and that is the shape every one of
///         this repo's silent XAML faults takes. So every case here MEASURES,
///         and the candidate rows are realized with a seeded result — an
///         unresolvable resource inside a list nothing ever shows is exactly the
///         fault that ships.
///     </para>
///     <para>
///         <b>And two cases are about a cost, not a fault.</b> A build with no
///         matcher registered must realize the match block's own subtree and
///         nothing inside the results list; and the panel with a matcher but no
///         search yet must realize the block WITHOUT the list. The body already
///         lives in a <c>ContentTemplate</c> so the collapsed PANEL costs
///         nothing (<see cref="ToneDesignUiPerfSmokeTests" /> pins that); this
///         class pins that opening the panel does not additionally pay for a
///         results list nobody has asked for.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneDesignMatchUiPerfSmokeTests
{
    private const double Width = 520;
    private const double Height = 900;

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

        public bool IsConfigured => true;

        /// <summary>Never raised: these cases are about the MATCH block, and a
        /// configuration that moves mid-layout is the design half's business.</summary>
        public event EventHandler? ConfiguredChanged { add { } remove { } }

        public void NotifyConfigurationChanged() { }

        public Task<ToneDesignResult> DesignAsync(
            ToneDesignRequest request, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("the smoke tests never run a design");
    }

    private sealed class StubMatcher : IToneMatcher
    {
        public Task<ToneMatchResult> MatchAsync(
            ToneMatchRequest request, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("the smoke tests never run a match");
    }

    private sealed class StubIsolator : IStemIsolator
    {
        public Task<MonoAudio> IsolateGuitarAsync(
            string songAudioPath, TimeSpan from, TimeSpan to, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("the smoke tests never isolate");
    }

    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    private static ToneDesignViewModel NewPanelVm(bool withMatcher, bool withEngine = true)
    {
        var vm = new ToneDesignViewModel(
            static () => new StubDesignService(),
            static _ => { },
            static () => new DspTonePreset("live", "Live", DspToneCategory.Clean, "", Rig("amp"), DspToneOrigin.User),
            static (name, category, _, rig) => new DspTonePreset("s", name, category, "", rig, DspToneOrigin.User), LocalizationService.Instance,
            static id => id);

        vm.ConfigureMatch(
            () => withMatcher ? new StubMatcher() : null,
            static () => new StubIsolator(),
            // An engine is what the "record my playing" button needs; a build
            // without one still matches, over the bundled clip.
            audio: () => withEngine ? new FakeAudioPlaybackService() : null);
        vm.IsExpanded = true;
        return vm;
    }

    /// <summary>Finished candidates without a matcher, so the list's own
    /// template — the title, the chain summary and the match-percent pill — is
    /// REALIZED and measured.</summary>
    private static void GiveItCandidates(ToneDesignViewModel vm) =>
        vm.SetMatchCandidatesForTests(new ToneMatchResult(
            [
                new ToneMatchCandidate(Rig("rat", "amp", "cab"), 0.21, "retrieval"),
                new ToneMatchCandidate(Rig("ts9", "amp"), 0.44, "refined")
            ],
            0.2,
            TimeSpan.FromSeconds(4),
            ["rendered 400 rigs"]));

    [Fact]
    public async Task TheMatchBlockWithResults_BuildsAttachesAndMeasuresNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withMatcher: true);
            GiveItCandidates(vm);

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                foreach (var name in new[]
                         {
                             "ToneMatchBlock", "ToneMatchRun", "ToneMatchRecord",
                             "ToneMatchResults", "ToneMatchList"
                         })
                {
                    var control = Find(panel, name);
                    Assert.True(control.IsVisible, $"'{name}' realized but is not visible");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
                        $"'{name}' laid out as {control.Bounds} — its styles failed silently");
                }

                // The selected candidate is published as the panel's RESULT, so
                // the shared result block and its four actions must be on screen
                // for a matched tone exactly as for a designed one.
                var result = Find(panel, "ToneDesignResult");
                Assert.True(result.IsVisible, "a matched candidate did not publish a result block");
                Assert.True(result.Bounds is { Width: > 1, Height: > 1 },
                    $"the result block laid out as {result.Bounds} for a matched tone");
                Assert.True(Find(panel, "ToneDesignHearIt").Bounds is { Width: > 1, Height: > 1 },
                    "Hear it did not lay out for a matched tone");

                // Two candidates, two realized rows — a list that measured
                // non-zero while realizing nothing would pass every assertion
                // above.
                var rows = Find(panel, "ToneMatchList").GetVisualDescendants()
                    .OfType<ListBoxItem>().Count();
                Assert.Equal(2, rows);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoSearchYet_TheBlockIsOnScreenButTheResultsListIsNot()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withMatcher: true);

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                var block = Find(panel, "ToneMatchBlock");
                Assert.True(block.IsVisible);
                Assert.True(block.Bounds is { Width: > 1, Height: > 1 },
                    $"the match block laid out as {block.Bounds} — it rendered nothing");

                // Opening the panel must not pay for a results list nobody has
                // asked for.
                var results = Find(panel, "ToneMatchResults");
                Assert.False(results.IsVisible, "the results list is on screen before any search");
                Assert.True(results.Bounds.Height <= 1,
                    $"an invisible results list still took {results.Bounds.Height} px of the drawer");

                // The line that says where the reference guitar will come from is
                // the one thing this state has to SAY.
                var hint = Find(panel, "ToneMatchDiHint");
                Assert.True(hint.IsVisible);
                Assert.True(hint.Bounds is { Width: > 1, Height: > 1 },
                    $"the reference-DI hint laid out as {hint.Bounds}");
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoEngine_TheRecordButtonIsGone_ButTheMatchButtonIsNot()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withMatcher: true, withEngine: false);

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                // Nothing to record from, so the button that records is not on
                // screen — but matching over the bundled clip still is.
                Assert.False(Find(panel, "ToneMatchRecord").IsVisible);
                var run = Find(panel, "ToneMatchRun");
                Assert.True(run.IsVisible);
                Assert.True(run.Bounds is { Width: > 1, Height: > 1 },
                    $"the match button laid out as {run.Bounds} — its styles failed silently");
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoMatcherRegistered_TheMatchBlockIsNotOnScreenAtAll()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withMatcher: false);

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                var block = Find(panel, "ToneMatchBlock");
                // Not merely disabled: a capability the app cannot honour must
                // not be on screen looking live.
                Assert.False(block.IsVisible, "a build with no matcher still showed the match block");
                Assert.True(block.Bounds.Height <= 1,
                    $"the hidden match block still took {block.Bounds.Height} px of the drawer");

                // …and the rest of the panel is untouched by its absence.
                Assert.True(Find(panel, "ToneDesignRun").Bounds is { Width: > 1, Height: > 1 },
                    "hiding the match block broke the design half of the panel");
            });
        }, CancellationToken.None);
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>
    ///     The one-click offer beside the match status line. Until it existed
    ///     the panel could SEE that the trained prior was unavailable — the
    ///     estimator lane's <c>IsAvailable</c> is false on every machine that
    ///     never ran <c>export_onnx.py</c> — and could do nothing about it, so
    ///     the whole model lane sat dark behind a silent `return null`.
    /// </summary>
    /// <summary>The library-build bar is realized, visible and measured while the
    /// status line is a build line, and hidden for any other line. A bar that
    /// measured zero would be the "1/4000 with nothing else" state exactly.</summary>
    [Fact]
    public async Task TheLibraryBuildBar_ShowsWhileBuilding_AndMeasuresNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withMatcher: true);
            vm.ConfigureMatch(static () => new StubMatcher(), static () => new StubIsolator());
            vm.MatchStatus = "Building tone library: 12/96 · ~3 min left";

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                var bar = Assert.IsType<ProgressBar>(Find(panel, "ToneMatchProgress"));
                Assert.True(bar.IsVisible, "the bar is hidden while the library builds");
                Assert.True(bar.Bounds is { Width: > 1, Height: > 1 },
                    $"the bar laid out as {bar.Bounds} — its styles failed silently");
                Assert.Equal(0.125, bar.Value, 6);
                Assert.False(bar.IsIndeterminate, "a determinate stage must not show an indeterminate bar");

                vm.MatchStatus = "Searching for a rig…";
                panel.UpdateLayout();
                Assert.False(bar.IsVisible, "any other line hides the bar");
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheMatchModelOffer_BuildsAttachesAndMeasuresNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withMatcher: true);
            vm.ConfigureMatch(
                static () => new StubMatcher(),
                static () => new StubIsolator(),
                modelDownloads: static () => new FakeRigEstimatorModelDownloadService());

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                Assert.True(vm.ShowMatchModelOffer,
                    "with no model installed the panel should be offering the download");

                foreach (var name in new[]
                         {
                             "ToneMatchModelOffer", "ToneMatchModelOfferText", "ToneMatchModelDownload"
                         })
                {
                    var control = Find(panel, name);
                    Assert.True(control.IsVisible, $"'{name}' realized but is not visible");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
                        $"'{name}' laid out as {control.Bounds} — its styles failed silently");
                }

                Assert.NotEqual(string.Empty, vm.MatchModelOffer);
            });
        }, CancellationToken.None);
    }

    /// <summary>With the model already installed the offer is off screen; the
    /// match block itself is unchanged.</summary>
    [Fact]
    public async Task WithTheModelInstalled_TheOfferIsNotShown()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withMatcher: true);
            vm.ConfigureMatch(
                static () => new StubMatcher(),
                static () => new StubIsolator(),
                modelDownloads: static () => new FakeRigEstimatorModelDownloadService { IsInstalled = true });

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                Assert.False(vm.ShowMatchModelOffer);
                Assert.False(Find(panel, "ToneMatchModelOffer").IsVisible);
                Assert.True(Find(panel, "ToneMatchBlock").Bounds is { Height: > 1 });
            });
        }, CancellationToken.None);
    }

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
        // Show() roots the tree and raises OnAttachedToLogicalTree, which is where
        // a Button calls CanExecute; the layout pass is driven through the VIEW
        // because a headless Window measured directly reports "Invalid size
        // returned for Measure".
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

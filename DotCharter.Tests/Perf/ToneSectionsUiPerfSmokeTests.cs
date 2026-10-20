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
///     Builds the "Match by section" list for real, attaches it to a window and
///     lays it out.
///
///     <para>
///         Construction is not enough. A view whose styles failed still
///         ARRANGES; it simply has no size, and that is the shape every one of
///         this repo's silent XAML faults takes. So every case here MEASURES, and
///         the section rows and their candidate lists are realized with seeded
///         data — an unresolvable resource, a <c>StringFormat</c> over a
///         <c>Binding</c> or a <c>Space*</c> in a <c>Thickness</c> inside a
///         template nothing ever shows is exactly the fault that ships.
///     </para>
///     <para>
///         <b>And one case is about a COST, not a fault.</b> A build with no
///         section slice registered must not realize this subtree at all: the
///         block is hosted in a <c>ContentTemplate</c>, and a template is not
///         instantiated until its host is MEASURED.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneSectionsUiPerfSmokeTests
{
    private const double Width = 520;
    private const double Height = 1200;

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

        /// <summary>Never raised: these cases are about the SECTION block.</summary>
        public event EventHandler? ConfiguredChanged { add { } remove { } }

        public void NotifyConfigurationChanged() { }

        public Task<ToneDesignResult> DesignAsync(
            ToneDesignRequest request, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("the smoke tests never run a design");
    }

    private sealed class StubSetMatcher : IToneSetMatcher
    {
        public Task<ToneSetMatchResult> MatchAsync(
            ToneSetMatchRequest request, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("the smoke tests never run a match");
    }

    private sealed class StubProposer : IToneSectionProposer
    {
        public Task<IReadOnlyList<ToneSectionProposal>> ProposeAsync(
            string songAudioPath, string stem, IReadOnlyList<ToneSection> chartSpans,
            IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("the smoke tests never propose");
    }

    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    private static ToneDesignViewModel NewPanelVm(bool withSections)
    {
        var vm = new ToneDesignViewModel(
            static () => new StubDesignService(),
            static _ => { },
            static () => new DspTonePreset("live", "Live", DspToneCategory.Clean, "", Rig("amp"), DspToneOrigin.User),
            static (name, category, _, rig) => new DspTonePreset("s", name, category, "", rig, DspToneOrigin.User), LocalizationService.Instance,
            static id => id);

        vm.ConfigureMatch(static () => null, static () => null);
        vm.ConfigureSections(
            () => withSections ? new StubProposer() : null,
            () => withSections ? new StubSetMatcher() : null);
        vm.IsExpanded = true;
        return vm;
    }

    /// <summary>Seeds a section with finished candidates, so the row template AND
    /// the candidate list's own template are REALIZED and measured.</summary>
    private static void GiveItASection(ToneDesignViewModel vm)
    {
        var row = vm.Sections.AddForTests(
            "Clean intro", DotCharter.Services.Ai.Match.ToneStems.Guitar, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        row.PublishCandidates(
            new ToneMatchResult(
                [
                    new ToneMatchCandidate(Rig("comp", "amp", "cab"), 0.21, "retrieval"),
                    new ToneMatchCandidate(Rig("ts9", "amp"), 0.44, "refined")
                ],
                0.2,
                TimeSpan.FromSeconds(4),
                ["rendered 400 rigs"]),
            vm);
    }

    [Fact]
    public async Task TheSectionList_BuildsAttachesAndMeasuresNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withSections: true);
            GiveItASection(vm);

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                foreach (var name in new[]
                         {
                             "ToneSectionsHost", "ToneSectionsRoot", "ToneSectionsAdd",
                             "ToneSectionsPropose", "ToneSectionsMatchAll", "ToneSectionsApply",
                             "ToneSectionsDefaultStem", "ToneSectionsList", "ToneSectionsRows"
                         })
                {
                    var control = Find(panel, name);
                    Assert.True(control.IsVisible, $"'{name}' realized but is not visible");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
                        $"'{name}' laid out as {control.Bounds} — its styles failed silently");
                }

                // One section, one realized row — a list that measured non-zero
                // while realizing nothing would pass every assertion above. The
                // row's own candidate ListBox is the marker: exactly one per
                // realized row template, and it exists nowhere else in this view.
                var rows = Find(panel, "ToneSectionsRows").GetVisualDescendants()
                    .OfType<ListBox>().Count();
                Assert.Equal(1, rows);

                // …and the candidate list inside the row, which is where the
                // per-candidate template would hide a resource fault.
                var candidates = Find(panel, "ToneSectionsRows").GetVisualDescendants()
                    .OfType<ListBoxItem>().Count();
                Assert.Equal(2, candidates);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoSectionsYet_TheBlockIsOnScreenButTheListIsNot()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withSections: true);

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                var root = Find(panel, "ToneSectionsRoot");
                Assert.True(root.IsVisible);
                Assert.True(root.Bounds is { Width: > 1, Height: > 1 },
                    $"the section block laid out as {root.Bounds} — it rendered nothing");

                // Opening the panel must not pay for a section list nobody has
                // added to.
                var list = Find(panel, "ToneSectionsList");
                Assert.False(list.IsVisible, "the section list is on screen before any section exists");
                Assert.True(list.Bounds.Height <= 1,
                    $"an invisible section list still took {list.Bounds.Height} px of the drawer");
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoSectionSliceRegistered_TheBlockIsNotOnScreenAtAll()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewPanelVm(withSections: false);

            var panel = new ToneDesignPanel { DataContext = vm };
            AttachAndLayout(panel, () =>
            {
                var host = Find(panel, "ToneSectionsHost");
                // Not merely disabled: a capability the app cannot honour must
                // not be on screen looking live.
                Assert.False(host.IsVisible, "a build with no section slice still showed the block");
                Assert.True(host.Bounds.Height <= 1,
                    $"the hidden section host still took {host.Bounds.Height} px of the drawer");

                // The template is not instantiated for an unmeasured host, so the
                // subtree does not exist at all — not merely hidden.
                Assert.DoesNotContain(
                    host.GetVisualDescendants().OfType<Control>(),
                    c => c.Name == "ToneSectionsRoot");

                // …and the rest of the panel is untouched by its absence.
                Assert.True(Find(panel, "ToneDesignRun").Bounds is { Width: > 1, Height: > 1 },
                    "hiding the section block broke the design half of the panel");
            });
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

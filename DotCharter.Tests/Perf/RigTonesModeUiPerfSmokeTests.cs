using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     W4 (2026-09-16) — THE TONES MODE.
///     <para>
///         The tone designer, the matcher and the Tones list used to share the
///         rig's fixed-height WORKSPACE with the module editor, and at 1600×900
///         that left the designer a 184 px viewport: the prompt field alone is
///         64 px and its header is 56, so "From the recording" and "Match by
///         section" were two and three scroll-lengths below the thing that
///         produces them. They are a MODE now — the header's preset pill switches
///         into it, the routing canvas is replaced by a read-only chain strip,
///         the gear rail stays — and the whole body below the header is theirs.
///     </para>
///     <para>
///         The two cases that decide it are
///         <see cref="TheFourDesignControls_AreOnScreenTogether_AtTheSmallestWindow" />
///         (the panel/mode decision, measured) and
///         <see cref="EveryToneCard_IsNameTagsAndChain_WithTheDescriptionInItsTooltip" />. Both were run
///         against the DRAWER first; the numbers they printed are in the run
///         report, and they are why the drawer is gone.
///     </para>
///     <para>
///         <c>*UiPerfSmokeTests</c> + <c>[Collection("HeadlessUI")]</c>: needs the
///         real Avalonia session, so it runs in the headless suite's own
///         <c>dotnet test</c>, not the default gate.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigTonesModeUiPerfSmokeTests(ITestOutputHelper output)
{
    // ---- fakes ---------------------------------------------------------------

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

    private sealed class StubProposer : IToneSectionProposer
    {
        public Task<IReadOnlyList<ToneSectionProposal>> ProposeAsync(
            string songAudioPath, string stem, IReadOnlyList<ToneSection> chartSpans,
            IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("the smoke tests never propose");
    }

    private sealed class StubSetMatcher : IToneSetMatcher
    {
        public Task<ToneSetMatchResult> MatchAsync(
            ToneSetMatchRequest request, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("the smoke tests never match a set");
    }

    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    /// <summary>The description a real factory tone carries, at the length that
    /// used to be cut: the card drew two lines and an ellipsis.</summary>
    private const string LongDescription =
        "A mid-forward crunch built around a transparent overdrive into a British "
        + "voiced head, with the cabinet's low cut lifted so palm mutes stay tight "
        + "under a busy mix. Roll the guitar volume back for the verse.";

    private static IReadOnlyList<DspTonePreset> WordyFactory() =>
    [
        new("f1", "British Crunch", DspToneCategory.Crunch, LongDescription, Rig("overdrive", "amp", "cab"),
            DspToneOrigin.BuiltIn),
        new("f2", "Glass Clean", DspToneCategory.Clean, LongDescription, Rig("compressor", "amp", "cab"),
            DspToneOrigin.BuiltIn),
        new("f3", "Stadium Lead", DspToneCategory.Lead, LongDescription, Rig("overdrive", "amp", "cab", "delay"),
            DspToneOrigin.BuiltIn)
    ];

    // ---- the rig -------------------------------------------------------------

    /// <summary>The rig screen over a real chain, with the AI slice wired, IN the
    /// Tones mode. 1600×900 is the smallest window the capture harnesses use.</summary>
    private static void WithTonesMode(
        Action<AmpFxRigView, AmpFxRigViewModel, PedalboardViewModel, Window> body,
        double width = 1600, double height = 900, bool enterMode = true,
        IReadOnlyList<DspTonePreset>? factory = null)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        var chain = new FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(
            chain, new FakeDspTonePresetService(factory ?? WordyFactory()), LocalizationService.Instance,
            toneDesignPanel: ToneDesignPanels.Real(static () => new StubDesignService()));
        foreach (var id in new[] { "overdrive", "amp", "cab" })
            rig.AddGear(catalogue.Single(m => m.Id == id));

        // The match and section slices arrive through their own Configure calls,
        // exactly as the composition root wires them. With neither, both blocks
        // are off screen by design and the four-control case would pass by
        // asserting about controls that do not exist.
        var design = ToneDesignPanels.Of(rig.Browser);
        design.ConfigureMatch(
            static () => new StubMatcher(),
            static () => new StubIsolator(),
            audio: static () => new FakeAudioPlaybackService());
        design.ConfigureSections(
            static () => new StubProposer(),
            static () => new StubSetMatcher());
        design.IsExpanded = true;

        var model = new AmpFxRigViewModel(rig);
        var view = new AmpFxRigView { DataContext = model };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            if (enterMode) rig.IsBrowserOpen = true;
            for (var pass = 0; pass < 6; pass++) window.UpdateLayout();
            body(view, model, rig, window);
        }
        finally
        {
            window.Close();
        }
    }

    private static Control Find(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"'{name}' is not in the tree");

    // ══ 1. THE DECISION: all four controls on screen at once ═════════════════

    /// <summary>
    ///     THE ACCEPTANCE CRITERION for the panel/mode decision. The prompt
    ///     field, Design, "From the recording" (the match block's record/run
    ///     pair) and "Match by section" must all be on screen at the same time,
    ///     nothing scrolled, at the smallest supported window.
    ///     <para>
    ///         Measured against the DRAWER (prototype (a)) at 1600×900 before the
    ///         mode existed: the designer's scroller had a 184 px viewport and
    ///         wanted 742 px, so only the prompt and Design were on screen — the
    ///         numbers are in the run report. That is the whole argument for (b).
    ///     </para>
    ///     <para>
    ///         Since 2026-09-27 the designer is a COLUMN with the composer (the
    ///         prompt and Design) PINNED under its thread, and the match blocks
    ///         in the thread's own scroller. So "on screen" is asked of each
    ///         control against what actually clips it: the window, and — for the
    ///         two in the thread — that scroller's viewport, unscrolled. A
    ///         control with no scroller over it is simply never scrolled away.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task TheFourDesignControls_AreOnScreenTogether_AtTheSmallestWindow(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithTonesMode((view, model, _, window) =>
        {
            Assert.True(model.IsTonesMode, "the preset pill did not switch into the Tones mode");
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

            var four = new[]
            {
                ("the prompt field", Find(view, "ToneDesignPrompt")),
                ("Design", Find(view, "ToneDesignRun")),
                ("From the recording", Find(view, "ToneMatchRecord")),
                ("Match by section", Find(view, "ToneSectionsRoot"))
            };

            var offenders = new List<string>();
            foreach (var (label, c) in four)
            {
                Assert.True(c.Bounds.Height > 1, $"'{label}' measured {c.Bounds.Height:F1} px — it rendered nothing");

                var inWindow = c.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                var line = $"{width}x{height}: {label} y={inWindow:F0}..{inWindow + c.Bounds.Height:F0} of {window.Bounds.Height:F0}";
                if (inWindow < -1 || inWindow + c.Bounds.Height > window.Bounds.Height + 1)
                    offenders.Add($"{label} outside the window at y={inWindow:F0}");

                if (c.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
                {
                    Assert.True(Math.Abs(scroll.Offset.Y) < 0.5, $"{scroll.Name} starts scrolled");
                    var top = c.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
                    var bottom = top + c.Bounds.Height;
                    line += $", in {scroll.Name ?? "a scroller"} y={top:F0}..{bottom:F0} of {scroll.Viewport.Height:F0}";
                    if (bottom > scroll.Viewport.Height + 1 || top < -1)
                        offenders.Add($"{label} at y={top:F0}..{bottom:F0} of a {scroll.Viewport.Height:F0} px viewport");
                }

                output.WriteLine(line);
            }

            Assert.True(offenders.Count == 0,
                $"the designer's four controls are not on screen together at {width:F0}x{height:F0} — "
                + string.Join("; ", offenders) + ". The prompt, Design, 'From the recording' and "
                + "'Match by section' are one feature and must be reachable without scrolling away "
                + "from each other.");
        }, width, height), CancellationToken.None);
    }

    // ══ 2. a tone card is short: name, one line, the chain ════════════════

    /// <summary>
    ///     A tone card is THREE things: its name (two lines at most), one quiet
    ///     line of category and tags, and the chain as colour bars. The owner found
    ///     the redesign's card — the whole description plus the chain spelled out —
    ///     "way too much text for each tone" (2026-09-27), so the description and
    ///     the chain in words moved to the card's TOOLTIP, where they are still
    ///     whole. This pins both halves: nothing of the description is drawn on a
    ///     card, every card carries it in its tooltip, and a card stays compact.
    ///     <para>
    ///         Proven non-vacuous by putting the description block back on the card:
    ///         the case fails naming the drawn description.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(2560, 1440)]
    public async Task EveryToneCard_IsNameTagsAndChain_WithTheDescriptionInItsTooltip(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithTonesMode((view, _, _, window) =>
        {
            var cards = Find(view, "RigBrowserCards");
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

            var buttons = cards.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("FirePresetCard")).ToList();
            Assert.True(buttons.Count >= 3, $"found {buttons.Count} tone cards - the card grid did not realize");

            var drawn = cards.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == LongDescription);
            Assert.True(drawn == 0, $"{drawn} tone card(s) still draw their description on the card");

            // open each card's tooltip the way a hover does: its bindings resolve
            // once it is shown, so that is where the description has to be
            var withTip = 0;
            foreach (var card in buttons)
            {
                ToolTip.SetIsOpen(card, true);
                for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
                if (ToolTip.GetTip(card) is Control tip
                    && tip.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == LongDescription))
                {
                    withTip++;
                }

                ToolTip.SetIsOpen(card, false);
            }
            Assert.True(withTip >= 3, $"only {withTip} card(s) carry the description in their tooltip");

            foreach (var card in buttons)
            {
                output.WriteLine($"{width}x{height}: card '{AutomationProperties.GetName(card)}' {card.Bounds.Height:F0} px");
                Assert.True(card.Bounds.Height is > 40 and <= 120,
                    $"the card '{AutomationProperties.GetName(card)}' is {card.Bounds.Height:F0} px tall - a card is a name, a line and the chain");
            }

            var names = cards.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "RigToneCardName").ToList();
            Assert.NotEmpty(names);
            Assert.All(names, n => Assert.Equal(2, n.MaxLines));
        }, width, height), CancellationToken.None);
    }

    /// <summary>
    ///     Cards in one row start on one line. The grid is a WrapPanel, which gives a row the
    ///     tallest card's height, and a Button centres itself in that slot — so a card with a
    ///     shorter description floated down its row and every row looked ragged.
    /// </summary>
    [Fact]
    public async Task ToneCardsInARow_StartOnOneLine()
    {
        IReadOnlyList<DspTonePreset> mixed =
        [
            new("m1", "British Crunch", DspToneCategory.Crunch, LongDescription, Rig("overdrive", "amp", "cab"),
                DspToneOrigin.BuiltIn),
            new("m2", "Glass Clean", DspToneCategory.Clean, "Clean.", Rig("compressor", "amp", "cab"),
                DspToneOrigin.BuiltIn),
            new("m3", "Stadium Lead", DspToneCategory.Lead, LongDescription, Rig("overdrive", "amp", "cab", "delay"),
                DspToneOrigin.BuiltIn)
        ];
        await HeadlessSessionFixture.Dispatch(() => WithTonesMode((view, _, _, window) =>
        {
            var host = Find(view, "RigBrowserCards");
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
            var cards = host.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("FirePresetCard")).ToList();
            Assert.True(cards.Count >= 3, $"found {cards.Count} tone cards — the grid did not realize");

            foreach (var card in cards)
            {
                output.WriteLine($"{card.Name ?? AutomationProperties.GetName(card)}: y={card.Bounds.Y:F1} " +
                                 $"h={card.Bounds.Height:F1} margin-top={card.Margin.Top:F1}");
                Assert.True(Math.Abs(card.Bounds.Y - card.Margin.Top) < 0.5,
                    $"'{AutomationProperties.GetName(card)}' starts {card.Bounds.Y - card.Margin.Top:F1} px below " +
                    "its row — a shorter card is centred in the row instead of standing on its top");
            }
        }, 2560, 1440, factory: mixed), CancellationToken.None);
    }

    // ══ 3. the mode is a mode: leaving it restores the canvas ════════════════

    /// <summary>
    ///     Entering the Tones mode replaces the routing canvas with the read-only
    ///     chain strip and keeps the gear rail; leaving it returns the canvas and
    ///     the module editor EXACTLY as they were. "Exactly as left" is the whole
    ///     reason the mode is a sibling of the canvas body rather than a re-layout
    ///     of it — nothing inside <c>RigFlatBody</c> is touched while the mode is
    ///     up.
    ///     <para>
    ///         Proven non-vacuous by making the mode collapse <c>RigDock</c>: the
    ///         editor then came back with no selection and this failed on
    ///         <c>IsDockBodyVisible</c>.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    public async Task EnteringAndLeavingTheMode_RestoresTheCanvasAndTheEditor(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithTonesMode((view, model, rig, window) =>
        {
            var canvas = view.FindControl<Border>("RigBoard")!;
            var gear = view.FindControl<Border>("RigGearPanel")!;

            // OUT of the mode first, with a block selected.
            rig.IsBrowserOpen = false;
            rig.SelectNodeAt(1);
            for (var pass = 0; pass < 5; pass++) window.UpdateLayout();
            Assert.True(model.IsDockBodyVisible, "selecting a block must open its editor");
            var beforeCanvas = canvas.Bounds;
            var beforeGear = gear.Bounds;
            var selected = rig.SelectedNode;

            // IN: the canvas is gone, the strip is up, the rail has not moved.
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            rig.IsBrowserOpen = true;
            for (var pass = 0; pass < 5; pass++) window.UpdateLayout();
            t0.Stop();
            var line = $"{width}x{height}: mode switch laid out in {t0.Elapsed.TotalMilliseconds:F1} ms";
            output.WriteLine(line);
            // LOGGED, never asserted: a wall clock in a suite that runs beside
            // seven other agents is a flake, and the ≤150 ms budget is a claim
            // about a real frame on a real box. Written beside the captures when
            // one is asked for, so a reader has the number with the picture.
            if (Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                lock (typeof(RigTonesModeUiPerfSmokeTests))
                    File.AppendAllText(Path.Combine(dir, "tones-mode-switch.txt"), line + Environment.NewLine);
            }

            Assert.True(model.IsTonesMode);
            // The chain strip is in the Tones VIEW (its own name scope and
            // template since 2026-09-27), in the visual tree once the mode is up.
            var strip = (Border)Find(view, "RigChainStrip");
            Assert.False(canvas.IsEffectivelyVisible, "the routing canvas is still up inside the Tones mode");
            Assert.True(strip.Bounds.Height > 1, $"the chain strip measured {strip.Bounds.Height:F1} px");
            Assert.True(gear.IsEffectivelyVisible, "the gear rail must stay in the Tones mode");
            Assert.True(Math.Abs(gear.Bounds.Width - beforeGear.Width) < 0.5,
                $"the gear rail is {beforeGear.Width:F0} px outside the mode and {gear.Bounds.Width:F0} px inside it");

            // OUT again: everything back as it was.
            rig.IsBrowserOpen = false;
            for (var pass = 0; pass < 5; pass++) window.UpdateLayout();

            Assert.Same(selected, rig.SelectedNode);
            Assert.True(model.IsDockBodyVisible, "leaving the mode must bring the editor back as it was");
            Assert.True(Math.Abs(canvas.Bounds.Height - beforeCanvas.Height) < 0.5,
                $"the canvas was {beforeCanvas.Height:F1} px before the mode and {canvas.Bounds.Height:F1} px after");
            Assert.True(Math.Abs(canvas.Bounds.Width - beforeCanvas.Width) < 0.5,
                $"the canvas WIDTH moved {beforeCanvas.Width:F1} -> {canvas.Bounds.Width:F1}");
        }, width, height, enterMode: false), CancellationToken.None);
    }

    // ══ 4. one scroll region ═════════════════════════════════════════════════

    /// <summary>
    ///     The Tones mode has exactly TWO scroll regions — the card grid and the
    ///     designer's thread, side by side — and NEITHER is inside the other.
    ///     The designer's own scroller, the card grid's and the category rail's
    ///     were three, stacked two deep (W4): the wheel landed on whichever the
    ///     pointer was over and neither outer extent told the truth about its
    ///     content. Two columns that each scroll their own content are not that
    ///     fault; one inside the other is.
    ///     <para>
    ///         Text boxes are excluded for the reason
    ///         <see cref="RigScrollRegionsUiPerfSmokeTests" /> excludes them: a
    ///         field scrolling its own text is not a region of this screen.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheTonesMode_HasTheGridAndTheThread_AndNoScrollerInsideAnother()
    {
        await HeadlessSessionFixture.Dispatch(() => WithTonesMode((view, _, _, window) =>
        {
            var mode = Find(view, "RigToneBrowser");
            for (var pass = 0; pass < 5; pass++) window.UpdateLayout();

            // The SAME predicate RigScrollRegionsUiPerfSmokeTests uses: a
            // scroller with both axes Disabled cannot scroll and is not a region,
            // and a control scrolling its own interior text is not a region.
            static bool Scrolls(ScrollBarVisibility v) => v != ScrollBarVisibility.Disabled;
            static bool IsRegion(ScrollViewer s) =>
                (Scrolls(s.HorizontalScrollBarVisibility) || Scrolls(s.VerticalScrollBarVisibility))
                && !s.GetVisualAncestors().OfType<TextBox>().Any();

            var regions = mode.GetVisualDescendants().OfType<ScrollViewer>()
                .Where(s => s.IsEffectivelyVisible)
                .Where(IsRegion)
                .ToList();

            foreach (var s in regions)
                output.WriteLine($"scroll region: {s.Name ?? "(anonymous)"}");

            Assert.Equal(["RigTonesScroll", "ToneDesignScroll"], regions.Select(s => s.Name).Order());
            var nested = regions.Where(s => s.GetVisualAncestors().OfType<ScrollViewer>().Any(IsRegion)).ToList();
            Assert.True(nested.Count == 0,
                "a scroll region inside another: " + string.Join(", ", nested.Select(s => s.Name ?? "(anonymous)")));
        }), CancellationToken.None);
    }

    // ══ 6. the match score reads honestly (W4 round 2, over W5b) ═════════════

    /// <summary>
    ///     Seeds a rated, an UNRATED and a within-noise candidate into the panel.
    ///     The three are the states W5b's score can be in, and the whole point of
    ///     binding them is that they must not render alike.
    /// </summary>
    private static void GiveItThreeScores(ToneDesignViewModel vm)
    {
        static MatchScoreBlock Block(string name, double share, double contribution, double reference) =>
            new(name, 1.0, contribution, reference, share, false);

        // RATED and OUTSIDE the target's own variation. The floor is a HIGH
        // percent, not a low one: SelfDistancePercent is 100·(1 − self/reference),
        // so a target whose two windows are CLOSE to each other (self 0.10 of a
        // 1.0 reference) puts the floor at 90 — and a candidate at 62 is still
        // measurably short of it. Getting this backwards is easy and the case
        // caught it: the first fixture had self 0.80, i.e. a floor of 20, and
        // every rated candidate was "within the noise".
        var rated = new ToneMatchCandidate(Rig("rat", "amp", "cab"), 0.38, "retrieval")
        {
            Score = new MatchScore(62, null, 0.38, 0.10, 1.0,
                [Block("dynamics", 0.41, 0.30, 0.20), Block("spectrum", 0.22, 0.10, 0.30)])
        };

        // UNRATED: no number at all, and a reason. Percent NULL is not 0.
        var unrated = new ToneMatchCandidate(Rig("ts9", "amp"), 0.55, "refined")
        {
            Score = new MatchScore(null, MatchUnrated.NoReference, 0.55, double.NaN, double.NaN, [])
        };

        // WITHIN THE TARGET'S OWN NOISE: rated, but at/past the self-distance
        // floor, so the caveat line has to be on screen.
        var noisy = new ToneMatchCandidate(Rig("od", "amp", "cab"), 0.05, "retrieval")
        {
            Score = new MatchScore(95, null, 0.05, 0.10, 1.0,
                [Block("dynamics", 0.50, 0.05, 0.02)])
        };

        vm.SetMatchCandidatesForTests(new ToneMatchResult(
            [rated, unrated, noisy], 0.2, TimeSpan.FromSeconds(4), ["rendered 400 rigs"]));
    }

    /// <summary>
    ///     THE ACCEPTANCE CRITERION for the score half: a rated candidate, an
    ///     unrated one and a within-noise one each render the right thing, each
    ///     measures non-zero, and <b>0 % and UNRATED do not look alike</b>.
    ///     <para>
    ///         The last is the one W5b's report asks for and the one a percentage
    ///         alone cannot give: an unrated candidate is a framed
    ///         <c>FireStatusPill Warn</c> printing "Unrated — …", a rated one is
    ///         bare accent mono printing a number. This asserts BOTH the text and
    ///         that exactly one of the two marks is realized per row.
    ///     </para>
    ///     <para>
    ///         Proven non-vacuous by binding the unrated row's pill to
    ///         <c>IsRated</c> instead of <c>!IsRated</c>: every row then showed
    ///         the pill and the case failed on the rated row.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task AMatchCandidate_ShowsItsScore_AndUnratedNeverLooksLikeZero()
    {
        await HeadlessSessionFixture.Dispatch(() => WithTonesMode((view, _, rig, window) =>
        {
            GiveItThreeScores(ToneDesignPanels.Of(rig.Browser));
            for (var pass = 0; pass < 5; pass++) window.UpdateLayout();

            var rows = Find(view, "ToneMatchList").GetVisualDescendants()
                .OfType<Border>().Where(b => b.Classes.Contains("FireListRow")).ToList();
            Assert.True(rows.Count == 3, $"the candidate list realized {rows.Count} rows, not 3");

            var seenRated = 0;
            var seenUnrated = 0;
            var seenBlocks = 0;
            var seenCaveat = 0;

            foreach (var row in rows)
            {
                var score = row.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(t => t.Name == "ToneMatchRatedScore");
                var pill = row.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Name == "ToneMatchUnratedPill");
                Assert.True(score is not null && pill is not null,
                    "a candidate row realized neither score mark — the template changed");

                var ratedUp = score!.IsEffectivelyVisible;
                var unratedUp = pill!.IsEffectivelyVisible;
                output.WriteLine($"row: rated={ratedUp} ('{score.Text}') unrated={unratedUp}");

                Assert.True(ratedUp != unratedUp,
                    "a candidate row shows BOTH the rated score and the unrated pill, or neither — "
                    + "0 % and 'unrated' must be distinguishable at a glance");

                if (ratedUp)
                {
                    seenRated++;
                    Assert.True(score.Bounds is { Width: > 1, Height: > 1 },
                        $"the rated score laid out as {score.Bounds}");
                    Assert.Contains("%", score.Text ?? "", StringComparison.Ordinal);
                }
                else
                {
                    seenUnrated++;
                    Assert.True(pill.Bounds is { Width: > 1, Height: > 1 },
                        $"the unrated pill laid out as {pill.Bounds}");
                    var text = pill.GetVisualDescendants().OfType<TextBlock>().First().Text ?? "";
                    Assert.DoesNotContain("%", text, StringComparison.Ordinal);
                    Assert.True(text.Length > 3, $"the unrated pill printed '{text}'");
                }

                // the per-block breakdown, where the candidate has one
                var blocks = row.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("FireMatchBlock") && b.IsEffectivelyVisible).ToList();
                foreach (var b in blocks)
                    Assert.True(b.Bounds is { Width: > 1, Height: > 1 }, $"a breakdown chip laid out as {b.Bounds}");
                seenBlocks += blocks.Count;

                var caveat = row.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(t => t.Name == "ToneMatchNoiseCaveat");
                if (caveat is { IsEffectivelyVisible: true })
                {
                    seenCaveat++;
                    Assert.True(caveat.Bounds.Height > 1, "the noise caveat laid out as nothing");
                    Assert.False(string.IsNullOrWhiteSpace(caveat.Text), "the noise caveat is blank");
                }
            }

            Assert.True(seenRated == 2, $"{seenRated} rated rows, expected 2");
            Assert.True(seenUnrated == 1, $"{seenUnrated} unrated rows, expected 1");
            Assert.True(seenBlocks >= 3,
                $"{seenBlocks} breakdown chips over three candidates — the headline is being shown alone");
            Assert.True(seenCaveat == 1,
                $"{seenCaveat} within-noise caveats, expected exactly 1 — a candidate inside the "
                + "target's own variation must say so, and one outside it must not");

            // …and the scale note, once under the list rather than per row.
            var scale = Find(view, "ToneMatchScaleNote");
            Assert.True(scale.IsEffectivelyVisible && scale.Bounds.Height > 1,
                $"the scale note laid out as {scale.Bounds} — a percentage with no scale beside it is "
                + "the thing W5b removed");
        }), CancellationToken.None);
    }

    // ══ 7. the empty workspace tells its two states apart ════════════════════

    /// <summary>
    ///     THE EMPTY WORKSPACE SAYS THE TRUE SENTENCE (W4 round 2).
    ///     <c>IsDockBodyVisible</c> is false in two different situations and the
    ///     body printed "Nothing selected" in both — which is a LIE in the
    ///     second, where the dock bar directly above is naming the selected unit
    ///     and the canvas is drawing it lit. Caught by the orchestrator in
    ///     <c>rig-1600x900-closed.png</c>, not by any assertion.
    ///     <para>
    ///         Both variants are measured here, with their text, and the hidden
    ///         one has to NAME the unit — a generic "the editor is hidden" would
    ///         pass a weaker assertion and still not tell the player which block
    ///         they are looking at the bar for.
    ///     </para>
    ///     <para>
    ///         Proven non-vacuous by pointing both variants at
    ///         <c>!IsDockBodyVisible</c> again: the case fails on "both empty
    ///         variants are on screen at once".
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheEmptyWorkspace_SaysWhetherNothingIsSelected_OrTheEditorIsHidden()
    {
        await HeadlessSessionFixture.Dispatch(() => WithTonesMode((view, model, rig, window) =>
        {
            var noSelection = Find(view, "RigDockEmptyNoSelection");
            var hidden = Find(view, "RigDockEmptyHidden");
            var hiddenBody = (TextBlock)Find(view, "RigDockEmptyHiddenBody");
            var toggle = (Button)Find(view, "RigDockToggle");

            void Settle()
            {
                for (var pass = 0; pass < 5; pass++) window.UpdateLayout();
            }

            // (1) NOTHING SELECTED — the empty-chain state.
            rig.SelectNodeAt(-1);
            Settle();
            Assert.True(model.HasNoDockSelection && !model.IsEditorHidden);
            Assert.True(noSelection.IsEffectivelyVisible, "the no-selection body is not up");
            Assert.False(hidden.IsEffectivelyVisible, "the HIDDEN body is up with nothing selected");
            Assert.True(noSelection.Bounds is { Width: > 1, Height: > 1 },
                $"the no-selection body laid out as {noSelection.Bounds}");

            // (2) A UNIT SELECTED AND HIDDEN — the state the capture caught.
            rig.SelectNodeAt(1);
            var unit = model.DockUnit!.DisplayName;
            model.ToggleDockCommand.Execute(null);
            Settle();

            Assert.True(model.IsEditorHidden && !model.HasNoDockSelection);
            Assert.False(model.IsDockBodyVisible, "the editor body is still up after HIDE");
            Assert.True(hidden.IsEffectivelyVisible, "the hidden body is not up");
            Assert.False(noSelection.IsEffectivelyVisible,
                "the body still says 'nothing selected' while a unit IS selected and merely hidden");
            Assert.True(hidden.Bounds is { Width: > 1, Height: > 1 },
                $"the hidden body laid out as {hidden.Bounds}");

            output.WriteLine($"hidden body: '{hiddenBody.Text}' · toggle: '{toggle.Content}'");
            Assert.Contains(unit, hiddenBody.Text ?? "", StringComparison.Ordinal);

            // …and the way OUT of the state is the button that caused it, whose
            // caption has to move with it: a control captioned HIDE is not the
            // control a player looks at to unhide.
            var hiddenLabel = toggle.Content as string ?? "";
            Assert.False(string.IsNullOrWhiteSpace(hiddenLabel), "the dock toggle lost its caption");

            model.ToggleDockCommand.Execute(null);
            Settle();
            Assert.True(model.IsDockBodyVisible, "SHOW did not bring the editor back");
            Assert.False(hidden.IsEffectivelyVisible);
            Assert.False(noSelection.IsEffectivelyVisible);
            Assert.NotEqual(hiddenLabel, toggle.Content as string ?? "");
            // `enterMode: false` — this state lives in the CANVAS body, which
            // the Tones mode replaces; entering it would hide the very thing
            // under test and every visibility assertion would read false.
        }, enterMode: false), CancellationToken.None);
    }

    // ══ 5. the capture dump ══════════════════════════════════════════════════

    /// <summary>
    ///     THE CAPTURE DUMP, same idiom as
    ///     <see cref="RigCanvasOwnershipUiPerfSmokeTests" />: inert unless BOTH
    ///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
    ///     <c>DOTCHARTER_RIG_CAPTURE_DIR=&lt;dir&gt;</c> are set. Writes
    ///     <c>tones-&lt;w&gt;x&lt;h&gt;.png</c>. It still lays the mode out with
    ///     the variables unset and asserts it arranged, so the case is not a no-op
    ///     in CI.
    /// </summary>
    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task TheTonesMode_LaysOut_AndIsDumpedWhenAsked(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithTonesMode((view, _, _, window) =>
        {
            var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
            var dumping = UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir);
            if (dumping) Directory.CreateDirectory(dir!);

            for (var pass = 0; pass < 6; pass++) window.UpdateLayout();
            Assert.True(Find(view, "RigTonesBody").Bounds.Height > 1, "the Tones mode did not lay out");

            if (!dumping) return;
            using var frame = window.CaptureRenderedFrame();
            Assert.True(frame is not null,
                "CaptureRenderedFrame returned null — DOTCHARTER_UI_SWEEP_DUMP=1 has to be set "
                + "BEFORE the session starts, or UseHeadlessDrawing is still true");
            frame!.Save(Path.Combine(dir!, $"tones-{width:F0}x{height:F0}.png"),
                new PngBitmapEncoderOptions());
        }, width, height), CancellationToken.None);
    }
}

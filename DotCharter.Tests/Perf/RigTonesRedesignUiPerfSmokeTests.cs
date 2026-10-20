using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The rig's Tones mode and the AI tone designer over a REALISTIC fixture:
///     the real factory catalogue plus the player's own tones (long names
///     included), a live chain of six units, and a design that has actually been
///     answered and refined — a conversation of three turns, a long chain, a
///     paragraph of rationale, validator warnings and a TONE3000 download. An
///     empty fixture hides layout bugs (a list of three short names never wraps,
///     never trims and never pushes anything off screen).
///     <para>
///         Both hosts' designers: DotCharter's (the core panel plus Match /
///         Sections in its extras slot) and RigLab's (the core panel alone,
///         mapped the way <c>RigLabWindow</c> maps it).
///     </para>
///     <para>
///         The capture half is inert unless <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///         <c>DOTCHARTER_RIG_CAPTURE_DIR</c> are both set; the layout half always
///         runs, so the class is not a no-op in CI.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigTonesRedesignUiPerfSmokeTests(ITestOutputHelper output)
{
    // ---- fakes -----------------------------------------------------------------

    /// <summary>Answers every request with a rich design, instantly. The second
    /// and later answers are the REFINED tone, so a tweak visibly changes it.</summary>
    private sealed class RichDesignService : IToneDesignService
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

        private int _calls;
        public bool IsConfigured => true;
        public event EventHandler? ConfiguredChanged { add { } remove { } }
        public void NotifyConfigurationChanged() { }

        public Task<ToneDesignResult> DesignAsync(
            ToneDesignRequest request, IProgress<string>? status, CancellationToken ct)
        {
            status?.Report("Asking the model…");
            _calls++;
            var name = _calls == 1 ? "Texas Flood Crunch" : "Texas Flood Crunch — Tighter Low End";
            var tone = new DspTonePreset("ai-" + _calls, name, DspToneCategory.Crunch,
                "A Tube Screamer pushing a blackface amp.",
                Rig("compressor", "overdrive", "amp", "cab", "eq", "delay", "reverb"), DspToneOrigin.User);
            return Task.FromResult(new ToneDesignResult(
                tone,
                "A Tube Screamer with the drive low and the level high pushes the amp's front end "
                + "instead of adding its own clipping, which is what gives the mids their vocal bark. "
                + "The compressor first evens out the pick attack, the EQ after the cab trims the "
                + "boom below 120 Hz so palm mutes stay tight, and a short slap delay plus a small "
                + "room fill the space without washing out the attack.",
                0.84,
                ["gain clamped to 10 (asked for 14)", "reverb mix lowered to 0.35 to keep the attack"],
                "{}",
                new AiUsage("claude-sonnet-4-5", 2400, 900),
                TimeSpan.FromSeconds(12.4),
                [new NamCaptureManifest(1, 2, "Blackface Deluxe Reverb — Vibrato Channel", "someone_on_tone3000",
                    "amp", "nam", "standard", "CC BY 4.0", "", "", "deluxe.nam", "", 1, DateTimeOffset.UnixEpoch)]));
        }
    }

    private sealed class StubMatcher : IToneMatcher
    {
        public Task<ToneMatchResult> MatchAsync(ToneMatchRequest request, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubIsolator : IStemIsolator
    {
        public Task<MonoAudio> IsolateGuitarAsync(string songAudioPath, TimeSpan from, TimeSpan to,
            IProgress<string>? status, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StubProposer : IToneSectionProposer
    {
        public Task<IReadOnlyList<ToneSectionProposal>> ProposeAsync(string songAudioPath, string stem,
            IReadOnlyList<ToneSection> chartSpans, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubSetMatcher : IToneSetMatcher
    {
        public Task<ToneSetMatchResult> MatchAsync(ToneSetMatchRequest request, IProgress<string>? status,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    /// <summary>The player's own tones, long names on purpose.</summary>
    private static IReadOnlyList<DspTonePreset> UserTones() =>
    [
        new("u1", "Sunday Worship Swell — Ambient Pad With Long Shimmer Tail", DspToneCategory.Ambient,
            "Volume swells into a shimmer; keep the pick soft.", Rig("compressor", "amp", "cab", "delay", "reverb"),
            DspToneOrigin.User),
        new("u2", "Drop C Chug", DspToneCategory.HighGain, "", Rig("gate", "overdrive", "amp", "cab"),
            DspToneOrigin.User),
        new("u3", "Überdrive Grundton (Proberaum)", DspToneCategory.Crunch,
            "Der Sound aus dem Proberaum, etwas weniger Bass.", Rig("overdrive", "amp", "cab", "eq"),
            DspToneOrigin.User),
        new("u4", "Bass DI + Amp Blend", DspToneCategory.Bass, "Blend of a clean DI and a driven amp.",
            Rig("compressor", "amp", "cab"), DspToneOrigin.User)
    ];

    // ---- the rig ---------------------------------------------------------------

    private enum DesignerHost { DotCharter, RigLab }

    /// <summary>What the designer column is showing.</summary>
    public enum DesignerState
    {
        /// <summary>Folded away — the mode's default: the library has the width.</summary>
        Closed,

        /// <summary>Open, nothing asked yet.</summary>
        Fresh,

        /// <summary>Open, a design asked for and refined once.</summary>
        Answered
    }

    private static void WithTones(
        Action<AmpFxRigView, AmpFxRigViewModel, PedalboardViewModel, Window> body,
        double width, double height, DesignerHost host = DesignerHost.DotCharter,
        DesignerState state = DesignerState.Answered, string rail = "all", string search = "",
        bool enterMode = true)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        var service = new RichDesignService();
        var panel = host == DesignerHost.DotCharter
            ? ToneDesignPanels.Real(() => service)
            : seams => new ToneDesignCoreViewModel(() => service, seams.Apply, seams.SnapshotLive, seams.SaveUser, seams.Localization,
                seams.ModuleName, post: seams.Post, logger: seams.Logger);

        var chain = new FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(
            chain, new FakeDspTonePresetService([.. DspTonePresetLibrary.All, .. UserTones()]), LocalizationService.Instance,
            toneDesignPanel: panel);
        foreach (var id in new[] { "compressor", "overdrive", "amp", "cab", "delay", "reverb" })
        {
            if (catalogue.FirstOrDefault(m => m.Id == id) is { } module)
                rig.AddGear(module);
        }

        var design = Assert.IsAssignableFrom<ToneDesignCoreViewModel>(rig.Browser.ToneDesign);
        if (design is ToneDesignViewModel full)
        {
            full.ConfigureMatch(static () => new StubMatcher(), static () => new StubIsolator(),
                audio: static () => new FakeAudioPlaybackService());
            full.ConfigureSections(static () => new StubProposer(), static () => new StubSetMatcher());
        }

        design.IsExpanded = state != DesignerState.Closed;
        if (state == DesignerState.Answered)
        {
            design.Description = "Stevie Ray Vaughan's Texas Flood rhythm tone — fat, a bit of hair, "
                                 + "cleans up when I roll the volume back";
            design.DesignCommand.Execute(null);
            design.TweakText = "tighter low end, less reverb";
            design.TweakCommand.Execute(null);
        }

        // a user tone is the active one, and the live chain has drifted from it
        rig.Browser.SelectedRail = rig.Browser.Rail.First(r => r.Kind == PresetFilterKind.All);
        rig.Browser.ApplyCard(rig.Browser.Cards.First(c => c.Id == "u3"));
        if (catalogue.FirstOrDefault(m => m.Id == "chorus") is { } drift) rig.AddGear(drift);
        if (rail == "user")
            rig.Browser.SelectedRail = rig.Browser.Rail.First(r => r.Kind == PresetFilterKind.User);
        rig.Browser.SearchText = search;

        var model = new AmpFxRigViewModel(rig);
        var view = new AmpFxRigView { DataContext = model };
        var window = new Window { Width = width, Height = height, Content = view };
        if (host == DesignerHost.RigLab)
            window.DataTemplates.Add(new FuncDataTemplate<ToneDesignCoreViewModel>((_, _) => new ToneDesignCorePanel()));
        window.Show();
        try
        {
            rig.IsBrowserOpen = enterMode;
            Settle(window);
            body(view, model, rig, window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Lays out, runs what the layout posted (the designer's follow-the-
    /// conversation scroll is a Background post), and lays out again.</summary>
    private static void Settle(Window window)
    {
        for (var round = 0; round < 3; round++)
        {
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        window.UpdateLayout();
    }

    private static Control Find(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"'{name}' is not in the tree");

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new PngBitmapEncoderOptions());
    }

    // ══ the capture dump ═════════════════════════════════════════════════════

    /// <summary>
    ///     Lays the Tones mode out with an answered design and writes
    ///     <c>tones-&lt;host&gt;-&lt;w&gt;x&lt;h&gt;.png</c> when asked. Always
    ///     asserts the mode and the designer measured non-zero.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080, "dotcharter", DesignerState.Answered, "all", "")]
    [InlineData(1280, 800, "dotcharter", DesignerState.Answered, "all", "")]
    [InlineData(1920, 1080, "dotcharter", DesignerState.Fresh, "all", "")]
    [InlineData(1920, 1080, "dotcharter", DesignerState.Closed, "all", "")]
    [InlineData(1280, 800, "dotcharter", DesignerState.Closed, "all", "")]
    [InlineData(1920, 1080, "dotcharter", DesignerState.Closed, "user", "")]
    [InlineData(1920, 1080, "dotcharter", DesignerState.Closed, "all", "zzz")]
    [InlineData(1920, 1080, "riglab", DesignerState.Answered, "all", "")]
    [InlineData(1280, 800, "riglab", DesignerState.Answered, "all", "")]
    [InlineData(1280, 800, "riglab", DesignerState.Fresh, "user", "")]
    public async Task TheTonesMode_LaysOut_AndIsDumpedWhenAsked(
        double width, double height, string host, DesignerState state, string rail, string search)
    {
        await HeadlessSessionFixture.Dispatch(() => WithTones((view, _, _, window) =>
        {
            Settle(window);
            var root = Find(view, "RigTonesMode");
            Assert.True(root.Bounds is { Width: > 1, Height: > 1 }, $"the Tones mode laid out as {root.Bounds}");
            var grid = Find(view, search.Length > 0 ? "RigBrowserEmpty" : "RigBrowserCards");
            Assert.True(grid.Bounds is { Width: > 1, Height: > 1 }, $"{grid.Name} laid out as {grid.Bounds}");
            if (state != DesignerState.Closed)
            {
                var designer = Find(view, "ToneDesignRoot");
                Assert.True(designer.Bounds is { Width: > 1, Height: > 1 },
                    $"the designer laid out as {designer.Bounds}");
                output.WriteLine($"{host} {width}x{height} {state}: designer {designer.Bounds.Size}");
                // The composer's box is a message box, not a one-line field: its
                // floor is a STYLE because a template attribute lost to the input's
                // own MinHeight=0 and the box came out 18 px tall.
                var box = (TextBox)Find(view, state == DesignerState.Answered ? "ToneDesignTweak" : "ToneDesignPrompt");
                Assert.True(box.IsEffectivelyVisible && box.Bounds.Height >= 48,
                    $"the composer's box laid out as {box.Bounds} (MinHeight {box.MinHeight})");
            }

            var variant = rail == "all" ? "" : "-" + rail;
            if (search.Length > 0) variant += "-search";
            Capture(window, $"tones-{host}-{state.ToString().ToLowerInvariant()}{variant}-{width:F0}x{height:F0}.png");
        }, width, height, host == "riglab" ? DesignerHost.RigLab : DesignerHost.DotCharter, state, rail, search),
            CancellationToken.None);
    }

    // ══ the structural guards ════════════════════════════════════════════════

    /// <summary>The region of <paramref name="v" /> that reaches the screen: its
    /// box intersected with every clipping ancestor's, in window coordinates.</summary>
    private static Rect Painted(Visual v, Visual root)
    {
        Rect InRoot(Visual x, Rect local) => new(x.TranslatePoint(local.TopLeft, root)!.Value, local.Size);

        var rect = InRoot(v, new Rect(v.Bounds.Size));
        foreach (var a in v.GetVisualAncestors())
        {
            if (a.ClipToBounds) rect = rect.Intersect(InRoot(a, new Rect(a.Bounds.Size)));
            if (a.Clip is { } g) rect = rect.Intersect(InRoot(a, g.Bounds));
            if (ReferenceEquals(a, root)) break;
        }

        return rect;
    }

    /// <summary>
    ///     A design that has been refined is taller than the thread, and the
    ///     thread FOLLOWS it: the answer card's actions (Hear it, Save as tone,
    ///     Discard) are on screen, whole, without the player scrolling. Before the
    ///     follow the second answer landed below the fold and the panel looked as
    ///     if nothing had happened. Proven non-vacuous by returning early from
    ///     <c>ToneDesignCorePanel.FollowConversation</c>: the actions then sat
    ///     below the viewport at both sizes.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 800)]
    public async Task ANewAnswer_IsScrolledIntoView_WithItsActions(double width, double height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithTones((view, _, _, window) =>
        {
            var scroll = (ScrollViewer)Find(view, "ToneDesignScroll");
            var actions = Find(view, "ToneDesignActions");
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 1,
                $"the thread fits its viewport ({scroll.Extent.Height:F0} of {scroll.Viewport.Height:F0}) — "
                + "this case is not exercising the follow");

            var viewport = Painted(scroll, window);
            var shown = Painted(actions, window);
            output.WriteLine($"{width}x{height}: offset {scroll.Offset.Y:F0}, actions {shown}, viewport {viewport}");
            Assert.True(scroll.Offset.Y > 1, "the thread did not scroll to the new answer");
            Assert.True(Math.Abs(shown.Height - actions.Bounds.Height) < 1 && shown.Bottom <= viewport.Bottom + 1,
                $"the answer's actions are cut: painted {shown} of {actions.Bounds.Height:F0} px, viewport ends at "
                + $"{viewport.Bottom:F0}");
        }, width, height), CancellationToken.None);
    }

    /// <summary>
    ///     With the designer open on the smallest window the library still shows
    ///     a WHOLE card: the column gives way (<c>RigTonesSizing</c>) instead of
    ///     the grid. At the token width the one card at 1280×800 was cut by the
    ///     grid's right edge. Proven non-vacuous by removing the code-behind's
    ///     width fit: the card then painted 58 px short.
    /// </summary>
    [Fact]
    public async Task WithTheDesignerOpen_TheGridStillShowsAWholeCard_At1280()
    {
        await HeadlessSessionFixture.Dispatch(() => WithTones((view, _, _, window) =>
        {
            var card = Find(view, "RigBrowserCards").GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("FirePresetCard"));
            var designer = Find(view, "RigToneDesignPanel");
            var painted = Painted(card, window);
            output.WriteLine($"card {card.Bounds.Size} painted {painted.Size}; designer {designer.Bounds.Width:F0}");
            Assert.True(Math.Abs(painted.Width - card.Bounds.Width) < 1,
                $"the only card column is cut: {painted.Width:F0} of {card.Bounds.Width:F0} px on screen");
            Assert.True(designer.Bounds.Width >= RigTonesSizing.MinDesignerWidth - 0.5,
                $"the designer shrank to {designer.Bounds.Width:F0} px");
        }, 1280, 800), CancellationToken.None);
    }

    /// <summary>
    ///     The composer's box does not move while the line beside the button
    ///     changes: nothing, a long progress line, and a run in flight with Cancel
    ///     up all leave the box where it was. Proven non-vacuous by dropping the
    ///     status line's MaxLines: the long line then wrapped to four and pushed
    ///     the box up.
    /// </summary>
    [Fact]
    public async Task TheComposersBox_DoesNotMove_WhileTheStatusChanges()
    {
        await HeadlessSessionFixture.Dispatch(() => WithTones((view, _, rig, window) =>
        {
            var design = (ToneDesignCoreViewModel)rig.Browser.ToneDesign!;
            var box = Find(view, "ToneDesignPrompt");
            double Top()
            {
                Settle(window);
                return box.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            }

            design.Description = "";
            var idle = Top();
            design.Status = "Downloading a capture from TONE3000: Blackface Deluxe Reverb — Vibrato Channel, "
                            + "Normal Input, Bright Switch Engaged, Recorded Through a Celestion Alnico Blue…";
            var busy = Top();
            design.IsRunning = true;
            var running = Top();
            design.IsRunning = false;

            output.WriteLine($"box top: idle {idle:F1}, long status {busy:F1}, running {running:F1}");
            Assert.True(Math.Abs(idle - busy) < 0.5, $"a long status moved the box {busy - idle:F1} px");
            Assert.True(Math.Abs(idle - running) < 0.5, $"Cancel appearing moved the box {running - idle:F1} px");
        }, 1920, 1080, state: DesignerState.Fresh), CancellationToken.None);
    }

    /// <summary>
    ///     The head does not move when the selection does: picking the player's
    ///     own tone brings its menu (Overwrite, Delete), and the search box, Save
    ///     as new and the designer toggle stay exactly where they were (the menu
    ///     grows leftwards into the spacer). And the search box starts on the
    ///     card grid's left edge. Proven non-vacuous by putting the actions in the
    ///     spacer column, left-aligned: Save then moved 185 px.
    /// </summary>
    [Fact]
    public async Task TheHead_DoesNotMove_WhenAUserToneIsSelected()
    {
        await HeadlessSessionFixture.Dispatch(() => WithTones((view, _, rig, window) =>
        {
            var watched = new[]
            {
                "RigBrowserSearch", "RigSpilloverToggle", "RigBrowserSave", "RigToneDesignToggle", "RigBrowserClose"
            };
            Dictionary<string, double> Xs()
            {
                Settle(window);
                return watched.ToDictionary(n => n, n => Find(view, n).TranslatePoint(new Point(0, 0), window)!.Value.X);
            }

            rig.Browser.ApplyCard(rig.Browser.Cards.First(c => !c.IsUser));
            var factory = Xs();
            Assert.False(Find(view, "RigBrowserToneMenu").IsVisible);

            rig.Browser.ApplyCard(rig.Browser.Cards.First(c => c.IsUser));
            var user = Xs();
            Assert.True(Find(view, "RigBrowserToneMenu").IsEffectivelyVisible, "a user tone offers no Overwrite / Delete");

            foreach (var n in watched)
                Assert.True(Math.Abs(factory[n] - user[n]) < 0.5, $"{n} moved {user[n] - factory[n]:F1} px");

            var searchLeft = Find(view, "RigBrowserSearch").GetVisualAncestors().OfType<Border>()
                .First(b => b.Classes.Contains("FireField")).TranslatePoint(new Point(0, 0), window)!.Value.X;
            var gridLeft = Find(view, "RigTonesScroll").TranslatePoint(new Point(0, 0), window)!.Value.X;
            Assert.True(Math.Abs(searchLeft - gridLeft) < 1, $"the search box starts at x={searchLeft:F0}, the grid at x={gridLeft:F0}");
        }, 1920, 1080, state: DesignerState.Closed), CancellationToken.None);
    }

    /// <summary>
    ///     The card of the tone on the rig says it has been EDITED since, and
    ///     only that card; every one of the player's own tones says MINE. Proven
    ///     non-vacuous by binding the Edited tag to <c>IsEdited</c> alone: every
    ///     card then wore it.
    /// </summary>
    [Fact]
    public async Task OnlyTheActiveCard_SaysEdited_AndTheUsersOwnSayMine()
    {
        await HeadlessSessionFixture.Dispatch(() => WithTones((view, model, rig, window) =>
        {
            Assert.True(model.IsEdited, "the fixture's chain did not drift from the applied tone");
            var cards = Find(view, "RigBrowserCards").GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("FirePresetCard")).ToList();
            Assert.Equal(4, cards.Count);

            foreach (var card in cards)
            {
                var vm = (PresetCardViewModel)card.DataContext!;
                var tags = card.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("FireTag") && b.IsEffectivelyVisible)
                    .Select(b => b.GetVisualDescendants().OfType<TextBlock>().First().Text)
                    .ToList();
                output.WriteLine($"{vm.Name}: [{string.Join(", ", tags)}]");
                Assert.Contains(LocalizationService.Instance["PresetBrowser_BadgeUser"], tags);
                Assert.Equal(vm.IsActive, tags.Contains(LocalizationService.Instance["Rig_Edited"]));
                Assert.True(card.Bounds is { Width: > 1, Height: > 1 });
            }

            Assert.Single(cards, c => ((PresetCardViewModel)c.DataContext!).IsActive);
        }, 1920, 1080, state: DesignerState.Closed, rail: "user"), CancellationToken.None);
    }

    /// <summary>
    ///     The prewarm reaches the card grid while the mode is CLOSED, in the
    ///     order the shell lists it: the Tones VIEW, then its drawer. The view is
    ///     a UserControl whose template is not applied until it is measured, so
    ///     the drawer is not even in the visual tree before the first step — the
    ///     second step alone realizes nothing, and the first click on the tone
    ///     pill would lay out a hundred cards. Proven non-vacuous by skipping the
    ///     first step: the drawer then realized zero cards.
    /// </summary>
    [Fact]
    public async Task ThePrewarm_RealizesTheCardGrid_WhileTheModeIsClosed()
    {
        await HeadlessSessionFixture.Dispatch(() => WithTones((view, model, rig, window) =>
        {
            Assert.False(model.IsTonesMode);

            var mode = view.FindControl<RigTonesView>("RigTonesMode")!;
            Assert.False(mode.IsVisible);
            var listed = DotCharter.UI.Views.Shell.HiddenSubtreePrewarmer.HiddenNamedDescendants(
                view, PlayModeView.NestedScreenBodyNames.Where(n => n.StartsWith("Rig", StringComparison.Ordinal)));
            Assert.Equal(["RigTonesMode", "RigToneBrowser", "RigToneDesignPanel"], listed.Select(c => c.Name));

            // As the drain does it: each part, then the card grid's remaining
            // chunks a turn at a time (#253).
            foreach (var part in listed)
            {
                DotCharter.UI.Views.Shell.HiddenSubtreePrewarmer.Realize(part, window.Bounds.Size);
                while (DotCharter.UI.Views.Shell.HiddenSubtreePrewarmer.RealizeNextChunk(part, window.Bounds.Size)) { }
            }

            var cards = mode.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("FirePresetCard"));
            output.WriteLine($"prewarm realized {cards} tone cards with the mode closed");
            Assert.True(cards > 50, $"the prewarm realized {cards} tone cards — the grid is cold on first open");
        }, 1920, 1080, state: DesignerState.Closed, enterMode: false), CancellationToken.None);
    }

    /// <summary>
    ///     Everything in the head is on screen at the smallest window with the
    ///     most in it: a user tone selected (its menu up) and the designer open.
    ///     In German the head once pushed the designer toggle half out and the ✕
    ///     off the panel entirely — Overwrite and Delete are a menu since. Run it
    ///     under <c>DOTCHARTER_HEADLESS_CULTURE=de-DE</c> too; it is the culture
    ///     that found it.
    /// </summary>
    [Fact]
    public async Task TheHead_FitsTheSmallestWindow_WithEverythingInIt()
    {
        await HeadlessSessionFixture.Dispatch(() => WithTones((view, _, rig, window) =>
        {
            rig.Browser.ApplyCard(rig.Browser.Cards.First(c => c.IsUser));
            Settle(window);

            var panel = Painted(Find(view, "RigToneBrowser"), window);
            foreach (var name in new[]
                     {
                         "RigBrowserSearch", "RigSpilloverToggle", "RigBrowserToneMenu", "RigBrowserSave",
                         "RigToneDesignToggle", "RigBrowserClose"
                     })
            {
                var c = Find(view, name);
                var shown = Painted(c, window);
                output.WriteLine($"{name}: {c.Bounds.Size} painted {shown}");
                Assert.True(c.IsEffectivelyVisible, $"{name} is not up");
                Assert.True(Math.Abs(shown.Width - c.Bounds.Width) < 1 && shown.Right <= panel.Right + 0.5,
                    $"{name} is cut by the head: {shown.Width:F0} of {c.Bounds.Width:F0} px painted, "
                    + $"right edge {shown.Right:F0} of the panel's {panel.Right:F0} ({HeadlessSessionFixture.SweptCulture})");
            }
        }, 1280, 800, state: DesignerState.Answered), CancellationToken.None);
    }
}

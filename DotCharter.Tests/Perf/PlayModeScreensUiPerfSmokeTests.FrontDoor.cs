// Play Mode's front door: the launch/start screen in every chip state and the profiles panel.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Perf;

public sealed partial class PlayModeScreensUiPerfSmokeTests
{
    /// <summary>
    ///     THE LAUNCH SCREEN, COLLAPSED — the app's very first surface, in the
    ///     state it boots into.
    ///     <para>
    ///         It needs no navigation command: <c>CurrentScreen</c> STARTS at
    ///         <c>Start</c>, which is exactly why the view's attach has to be
    ///         what fills its figures. A screen whose Fire styling failed still
    ///         ARRANGES — it just has no size — so every named region is checked
    ///         for real bounds, and the full-height frame (a body that stretches
    ///         over a footer pinned to the floor) is checked geometrically: the
    ///         420 px centred stack this replaced measured perfectly well.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task LaunchScreen_isAFullHeightFrame_WithEveryChipInAKnownState()
    {
        await WithView((view, vm) =>
        {
            Assert.True(vm.IsStartScreen, "the view model must boot on the launch screen");

            // The snapshot the FIRST FRAME renders is the all-unknown one: the
            // warm-up is posted at Background priority, deliberately, because
            // this screen is the app's boot surface.
            Assert.Same(LaunchFacts.Empty, vm.Launch);

            // …and then it really lands. Draining the Background queue is what
            // the first frame does for real; without it every assertion below
            // would be measuring the empty snapshot.
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            view.UpdateLayout();
            Assert.NotSame(LaunchFacts.Empty, vm.Launch);

            foreach (var name in new[]
                     {
                         "StartScreenRoot", "LaunchBodyScroll", "LaunchColumn", "LaunchStartButton",
                         "LaunchWhoPanel", "LaunchPlayingAsRow", "LaunchStatusChips", "LaunchLibraryChip",
                         "LaunchInputChip", "LaunchFooter", "LaunchKeyHints"
                     })
            {
                AssertLaidOut(view, name);
            }

            // No ground painted over the animated backdrop.
            AssertLetsTheBackdropThrough(view, "StartScreenRoot");

            // FULL HEIGHT: the footer sits on the floor of the screen root and
            // the scrolling body takes everything above it. The surface this
            // replaced was a centred island with no floor at all.
            var root = Find(view, "StartScreenRoot");
            var footer = Find(view, "LaunchFooter");
            var body = Find(view, "LaunchBodyScroll");
            var footerBottom = footer.TranslatePoint(new Point(0, footer.Bounds.Height), root);
            Assert.True(footerBottom.HasValue);
            Assert.True(Math.Abs(footerBottom!.Value.Y - root.Bounds.Height) <= 1,
                $"the footer ends at y={footerBottom.Value.Y:F0} in a {root.Bounds.Height:F0} px root — " +
                "it is not pinned to the floor");
            Assert.True(body.Bounds.Height > root.Bounds.Height * 0.5,
                $"the body took {body.Bounds.Height:F0} of {root.Bounds.Height:F0} px — the frame is not " +
                "full height");

            // The scroll region opens at its TOP and never pulls itself around
            // when focus moves into the profiles panel.
            var scroll = (ScrollViewer)body;
            Assert.False(scroll.BringIntoViewOnFocusChange);
            Assert.Equal(0, scroll.Offset.Y);

            // START is the ONE primary action, it is chamfered (a real Clip
            // geometry, not a CornerRadius), and it is live.
            var start = (Button)Find(view, "LaunchStartButton");
            Assert.True(start.Command?.CanExecute(null) == true, "START carries no live command");
            var cut = start.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Cut");
            Assert.True(cut?.Clip != null, "the primary action lost its chamfer clip");

            // ── The two chips, each in a state the app can actually prove ──
            // This harness builds no audio engine and scans no library, so the
            // honest answers here are "no device" and "not scanned" — and the
            // chips must SAY so rather than print a plausible number. That is
            // the whole point of the screen: it can express "I do not know".
            Assert.False(vm.Launch.IsInputConnected);
            Assert.False(vm.Launch.HasLatency);
            Assert.False(vm.Launch.IsLibraryReady);
            Assert.False(vm.Launch.HasLibraryCount);
            Assert.NotEqual("", vm.Launch.LibraryStateLabel);
            Assert.NotEqual("", vm.Launch.InputLabel);

            // Every visible figure inside a chip has real text behind it — a
            // chip whose only TextBlocks are blank is the "renders wordless"
            // dead panel this grammar exists to remove.
            foreach (var chip in new[] { "LaunchLibraryChip", "LaunchInputChip" })
            {
                var texts = Find(view, chip).GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsVisible).ToList();
                Assert.NotEmpty(texts);
                Assert.Contains(texts, t => !string.IsNullOrWhiteSpace(t.Text));
                Assert.All(texts.Where(t => !string.IsNullOrWhiteSpace(t.Text)),
                    t => Assert.True(t.Bounds.Height > 1, $"'{chip}' text laid out as {t.Bounds}"));
            }

            // Both panels are collapsed on entry, and the segmented pair says so.
            Assert.False(vm.IsLaunchProfilesOpen);
            Assert.False(vm.IsGuestEntryOpen);
            Assert.False(Find(view, "LaunchProfilesPanel").IsVisible);
            Assert.False(Find(view, "LaunchGuestPanel").IsVisible);

            // Three key hints, all with real text — the footer is the reason the
            // shortcuts exist, so a blank one is a broken promise.
            var hints = Find(view, "LaunchKeyHints").GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList();
            // TWO: "ESC QUIT" was removed along with the behaviour it described
            // — Esc no longer closes the application.
            Assert.Equal(2, hints.Count);
            Assert.All(hints, h => Assert.True(h.Bounds.Width > 1, $"a key hint laid out as {h.Bounds}"));
        });
    }

    /// <summary>
    ///     THE LAUNCH SCREEN, PROFILES EXPANDED. The panel is the half that
    ///     carries real state — the profile list, the four actions, the
    ///     left-handed flag and the add field — so it gets its own layout pass.
    ///     <para>
    ///         The rows bind the host window's <c>StatusBarViewModel</c> and this
    ///         harness hosts the view in a plain <c>Window</c>, so the list is
    ///         legitimately empty here (the documented "unresolved host renders
    ///         nothing instead of throwing" behaviour). What IS asserted is that
    ///         the panel realizes, measures, and that every action inside it is a
    ///         real control rather than a drawn one.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1920, 1000)]
    [InlineData(1280, 720)]
    public async Task LaunchScreen_profilesPanelExpandsAndMeasures(double width, double height)
    {
        await WithView((view, vm) =>
        {
            Assert.True(vm.IsStartScreen);
            vm.ToggleLaunchProfilesCommand.Execute(null);
            view.UpdateLayout();

            Assert.True(vm.IsLaunchProfilesOpen);
            foreach (var name in new[]
                     {
                         "StartScreenRoot", "LaunchWhoPanel", "LaunchProfilesPanel", "LaunchProfileActions",
                         "LaunchLeftyBlock", "LaunchLeftyCheck", "LaunchNewProfileRow", "LaunchAddProfileButton"
                     })
            {
                AssertLaidOut(view, name);
            }

            // The expanded panel really grew the surface it hangs off.
            var panel = Find(view, "LaunchProfilesPanel");
            AssertContains(Find(view, "LaunchWhoPanel"), panel, "the profiles panel");

            // Rename · Picture… · Export… · Import… are four REAL Buttons, not
            // four labels — a drawn row looks identical and does nothing.
            //
            // Their Commands are NOT asserted here and cannot be: every one of
            // them binds the host window's StatusBarViewModel, and this harness
            // hosts the view in a plain Window, so that path resolves to null. The
            // wiring itself is pinned in the DEFAULT gate by
            // `UI/LaunchScreenTests.EveryProfileActionIsARealCommand`, which
            // checks each bound path against the real view-model type.
            var actions = Find(view, "LaunchProfileActions").GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(4, actions.Count);
            Assert.All(actions, b =>
                Assert.True(b.Bounds is { Width: > 1, Height: > 1 },
                    $"a profile action laid out as {b.Bounds}"));

            // The left-handed row carries its one-line consequence, not just the
            // checkbox — that sentence is the whole reason the option is legible.
            var lefty = Find(view, "LaunchLeftyBlock");
            var hint = lefty.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("FireRowDesc"));
            Assert.True(hint != null, "the left-handed option lost its consequence line");
            Assert.False(string.IsNullOrWhiteSpace(hint!.Text));
            Assert.True(hint.Bounds.Height > 1, $"the consequence line laid out as {hint.Bounds}");

            // NEW PROFILE is a real field + a real Add button.
            var field = Find(view, "LaunchNewProfileRow").GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(field.Bounds is { Width: > 1, Height: > 1 }, $"the name field laid out as {field.Bounds}");
            AssertLaidOut(view, "LaunchAddProfileButton");
            var add = Find(view, "LaunchAddProfileButton");
            Assert.True(field.TranslatePoint(new Point(field.Bounds.Width, 0), view)!.Value.X
                <= add.TranslatePoint(default, view)!.Value.X,
                "The Add button overlaps the new profile name field.");

            // The pair is mutually exclusive: opening Guest closes Profiles, so
            // two answers to "who is playing" can never stack.
            vm.ToggleGuestEntryCommand.Execute(null);
            view.UpdateLayout();
            Assert.False(vm.IsLaunchProfilesOpen);
            Assert.True(vm.IsGuestEntryOpen);
            Assert.False(Find(view, "LaunchProfilesPanel").IsVisible);
            AssertLaidOut(view, "LaunchGuestPanel");
            var guestField = Find(view, "LaunchGuestPanel").GetVisualDescendants().OfType<TextBox>().Single();
            var guestStart = Find(view, "LaunchGuestStartButton");
            AssertLaidOut(view, "LaunchGuestStartButton");
            Assert.True(guestField.TranslatePoint(new Point(guestField.Bounds.Width, 0), view)!.Value.X
                <= guestStart.TranslatePoint(default, view)!.Value.X,
                "Calibrate and play overlaps the guest name field.");

            // …and Esc closes the open panel instead of falling through to the
            // quit branch, which is what makes "ESC QUIT" safe while typing.
            Assert.True(vm.TryCloseOverlay());
            Assert.False(vm.IsGuestEntryOpen);
            Assert.False(vm.TryCloseOverlay());
        }, width: width, height: height);
    }

    [Fact]
    public async Task StartScreen_laysOut()
    {
        await WithView((view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.Start;
            view.UpdateLayout();
            AssertLaidOut(view, "StartScreenRoot");
            // Was `StartProfilePanel`, a name the 0.48 launch-screen rebuild
            // retired; the panel is `LaunchWhoPanel` now and this case had been
            // failing on the missing name ever since.
            AssertLaidOut(view, "LaunchWhoPanel");
        });
    }
}

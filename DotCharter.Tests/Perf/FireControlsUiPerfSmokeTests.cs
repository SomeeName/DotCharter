using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Construction + attach + layout for the four shared Fire controls, plus
///     the three things only a REAL tree can prove: that every token they draw
///     with actually resolves, that a 170 px pointer drag really moves a knob
///     from minimum to maximum, and that the drag ghost and the drop marker
///     really land in the <c>AdornerLayer</c>.
///     <para>
///         A control whose resources failed still ARRANGES — it just paints
///         nothing — which is the shape every silent fault in this repo takes,
///         so these assert measured bounds AND resolved colour.
///     </para>
///     Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///     session, which cannot share a testhost with the gate and is excluded from the
///     default gate.
/// </summary>
[Collection("HeadlessUI")]
public sealed class FireControlsUiPerfSmokeTests
{
    private const double HostWidth = 640;
    private const double HostHeight = 400;

    // ── Construction + attach + layout ───────────────────────────────────────

    [Fact]
    public async Task TheKnob_BuildsAttachesAndLaysOutWithBothTextLines()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var knob = new FireKnob
            {
                Label = "Low cut",
                ValueText = "480 Hz",
                Minimum = 0,
                Maximum = 1,
                Value = 0.62
            };
            FireUnitState.SetCategory(knob, FireUnitCategory.Drive);

            WithLaidOut(knob, () =>
            {
                AssertMeasured(knob, "the Fire knob");
                Assert.Equal(FireKnobGeometry.CellWidth, knob.Bounds.Width, 1);

                // Both mono lines rendered something. A knob whose caption and
                // readout are blank measures exactly the same as one that works.
                // (The third TextBlock is the unit slot, which a compact knob
                // never fills — it stays empty here and is asserted in
                // TheHardwareKnob_PrintsItsUnitBesideThePlate.)
                var all = knob.GetVisualDescendants().OfType<TextBlock>().ToList();
                Assert.Equal(3, all.Count);
                var lines = all.Where(l => !string.IsNullOrEmpty(l.Text)).ToList();
                Assert.Equal(2, lines.Count);
                foreach (var line in lines)
                {
                    AssertMeasured(line, $"the knob's '{line.Text}' line");
                    Assert.False(string.IsNullOrWhiteSpace(line.Text));
                    // "Monospace means machine data": the mono token resolved,
                    // and the tracking is really on.
                    Assert.Contains("JetBrains Mono", line.FontFamily.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.True(line.LetterSpacing > 0,
                        $"the knob's '{line.Text}' line is not letter-spaced");
                }

                Assert.Contains(lines, l => l.Text == "LOW CUT");
                Assert.Contains(lines, l => l.Text == "480 Hz");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The tone-rig design prints a unit ONCE, beside the plate: a hardware
    ///     knob given <c>Unit="Hz"</c> lays the suffix out to the right of its
    ///     readout box, on the readout's line, in a UI-font line of its own —
    ///     and a bare (Dense) dial, which has no plate, prints nothing. Without
    ///     this a knob captioned "Rate" would carry no unit anywhere on the face.
    /// </summary>
    [Fact]
    public async Task TheHardwareKnob_PrintsItsUnitBesideThePlate()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var knob = new FireKnob
            {
                Label = "Rate", ValueText = "0.8", Unit = "Hz",
                Minimum = 0, Maximum = 1, Value = 0.5, HardwareStyle = true
            };
            FireUnitState.SetCategory(knob, FireUnitCategory.Modulation);

            WithLaidOut(knob, () =>
            {
                AssertMeasured(knob, "the hardware knob");
                Assert.True(knob.Bounds.Width >= 83.5 && knob.Bounds.Height >= 131.5,
                    $"the design's 84×132 hardware cell, measured {knob.Bounds}");
                Assert.Equal("Hz", knob.UnitText);

                var texts = knob.GetVisualDescendants().OfType<TextBlock>().ToList();
                var unit = Assert.Single(texts, t => t.Text == "Hz");
                var readout = Assert.Single(texts, t => t.Text == "0.8");
                AssertMeasured(unit, "the knob's unit");
                Assert.True(unit.Bounds.X > readout.Bounds.X + readout.Bounds.Width / 2,
                    $"the unit {unit.Bounds} should sit right of the plate {readout.Bounds}");
                // Centred in the readout's 18 px line, so compare centres.
                Assert.True(Math.Abs(unit.Bounds.Center.Y - readout.Bounds.Center.Y) < 3,
                    $"the unit {unit.Bounds} should share the readout's line {readout.Bounds}");
                Assert.DoesNotContain("JetBrains Mono", unit.FontFamily.Name, StringComparison.OrdinalIgnoreCase);

                // A bare dial has no plate for a unit to sit beside.
                knob.Dense = true;
                Assert.Equal(string.Empty, knob.UnitText);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The focus ring is for KEYBOARD focus. A click focuses the knob too
    ///     (the wheel and the keys must work after one), but drawing the pale
    ///     rectangle round a knob someone just turned is the "weird white
    ///     border" the owner reported (2026-09-08). Programmatic focus keeps the
    ///     ring, which is what the keyboard-reachability test above relies on.
    /// </summary>
    [Fact]
    public async Task TheFocusRing_DrawsForKeyboardFocus_NotForAClick()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var knob = new FireKnob { Minimum = 0, Maximum = 1, Value = 0.5 };
            // A second focusable beside it: focus has to LEAVE between the
            // gestures, because refocusing an already-focused element raises
            // no GotFocus and so cannot change how the ring is drawn.
            var elsewhere = new Button { Content = "elsewhere" };
            var panel = new StackPanel { Children = { knob, elsewhere } };
            WithLaidOut(panel, () =>
            {
                Assert.True(knob.Focus(NavigationMethod.Pointer));
                Assert.True(knob.IsFocused);
                Assert.False(knob.ShowsFocusRing, "a click must not draw the focus ring");

                Assert.True(elsewhere.Focus());
                Assert.False(knob.IsFocused);
                Assert.True(knob.Focus(NavigationMethod.Tab));
                Assert.True(knob.ShowsFocusRing, "Tab focus must draw the ring");

                Assert.True(elsewhere.Focus());
                Assert.True(knob.Focus());
                Assert.True(knob.ShowsFocusRing, "programmatic focus keeps the ring");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A slider carrying <c>FireReset.Default</c> resets to it; one without
    ///     keeps its value — the gesture is armed by the binding, so a stock
    ///     slider elsewhere in the app gains nothing.
    /// </summary>
    [Fact]
    public async Task ASliderWithADefault_ResetsToIt()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var armed = new Slider { Minimum = -12, Maximum = 12, Value = 7 };
            FireReset.SetDefault(armed, 0);
            var bare = new Slider { Minimum = 0, Maximum = 1, Value = 0.8 };

            Assert.True(FireReset.TryReset(armed));
            Assert.Equal(0, armed.Value);

            Assert.False(FireReset.TryReset(bare));
            Assert.Equal(0.8, bare.Value, 3);

            // A default outside the range lands on the nearer end, never outside it.
            FireReset.SetDefault(armed, 40);
            Assert.True(FireReset.TryReset(armed));
            Assert.Equal(12, armed.Value);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheKnobsArcColour_IsItsCategorysFixedIdentityFromTheDictionary()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // A missing token renders as the "something is wrong" grey, with no
            // warning and no exception — the whole reason the control carries
            // no per-category fallback.
            (FireUnitCategory Category, string Hex)[] expected =
            [
                (FireUnitCategory.Dynamics, "#FF8FA3BD"),
                (FireUnitCategory.Amps, "#FFF0A54A"),
                (FireUnitCategory.Cabinets, "#FFC98F5E"),
                (FireUnitCategory.Drive, "#FFE05A5A"),
                (FireUnitCategory.Eq, "#FF4ADE80"),
                (FireUnitCategory.Modulation, "#FFE879B9"),
                (FireUnitCategory.DelayReverb, "#FF3EC9C0"),
                (FireUnitCategory.UtilityRouting, "#FFA78BFA")
            ];

            var knob = new FireKnob();
            WithLaidOut(knob, () =>
            {
                foreach (var (category, hex) in expected)
                {
                    FireUnitState.SetCategory(knob, category);
                    Assert.Equal(hex, FireUnitState.AccentOf(knob).ToString(),
                        ignoreCase: true);
                }

                // …and a bypassed unit drops its identity entirely. Keeping it
                // is half of the "bypass applied partially" bug.
                FireUnitState.SetCategory(knob, FireUnitCategory.Drive);
                FireUnitState.SetIsBypassed(knob, true);
                Assert.NotEqual("#FFE05A5A", FireUnitState.AccentOf(knob).ToString());
                Assert.NotEqual(Colors.Gray, FireUnitState.AccentOf(knob));
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheKnobsFocusRingIsVisible()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var knob = new FireKnob();
            WithLaidOut(knob, () =>
            {
                var before = knob.RepaintRequests;
                Assert.True(knob.Focusable);
                Assert.True(knob.Focus(), "the knob refused focus — it is not keyboard reachable");
                Assert.True(knob.IsFocused);
                Assert.True(knob.RepaintRequests > before,
                    "focusing the knob did not repaint it, so the focus ring can never appear");

                // The ring's colour is `FireAccentPale` FROM THE DICTIONARY, and
                // the dictionary agrees with the theme. Asserted against the
                // theme's own table rather than against a literal: the literal
                // was `#DDD6FE`, Fire's violet, and it went stale the moment
                // the app had a different theme - which is the type-ladder rot
                // (`Infrastructure/FireTypeLadder`) in its colour form. This
                // way a broken dictionary still fails, and a re-toned theme
                // does not.
                Assert.Equal(ThemePresets.Default.Colors[ThemeTokens.FireAccentPale],
                    FireUnitState.ColorFrom(knob, "FireAccentPale"));
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ADragOf170Pixels_TakesTheKnobFromMinimumToMaximum()
    {
        // The contract, driven through REAL pointer input rather than through
        // the pure helper: vertical travel, 170 px is the whole range, and the
        // pointer is captured so the drag survives leaving the control.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var knob = new FireKnob { Minimum = 0, Maximum = 1, Value = 0 };

            // Parked well inside a tall window so 170 px of travel in EITHER
            // direction still lands on real window coordinates.
            var canvas = new Canvas { Children = { knob } };
            Canvas.SetLeft(knob, 120);
            Canvas.SetTop(knob, 400);

            var window = new Window { Width = 800, Height = 760, Content = canvas };
            window.Show();
            try
            {
                window.UpdateLayout();
                var origin = knob.TranslatePoint(
                    new Point(knob.Bounds.Width / 2, knob.Bounds.Height / 2), window)!.Value;

                window.MouseDown(origin, MouseButton.Left);
                window.MouseMove(origin - new Vector(0, 85));
                Assert.Equal(0.5, knob.Value, 3);

                window.MouseMove(origin - new Vector(0, 170));
                Assert.Equal(1.0, knob.Value, 3);

                // Past the end it clamps rather than wrapping.
                window.MouseMove(origin - new Vector(0, 380));
                Assert.Equal(1.0, knob.Value, 3);

                // Dragging back DOWN lowers it, from the same anchor.
                window.MouseMove(origin + new Vector(0, 85));
                Assert.Equal(0.0, knob.Value, 3);

                window.MouseUp(origin, MouseButton.Left);

                // After release the pointer is no longer captured, so moving
                // over the knob does nothing.
                var settled = knob.Value;
                window.MouseMove(origin - new Vector(0, 170));
                Assert.Equal(settled, knob.Value, 6);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AFocusedKnob_IsDrivenEndToEndFromTheKeyboard()
    {
        // The pointer half is proved above; this is the same contract driven
        // through REAL key input on a REAL focused control — tab-reachable,
        // arrow/page/home/end/reset, and an automation face that reads and
        // writes the value. A knob that can only be dragged is a rig nobody can
        // operate without a mouse.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var knob = new FireKnob
            {
                Label = "Drive", Minimum = 0, Maximum = 10, Step = 0.5, DefaultValue = 5, Value = 5
            };

            // A sibling to tab to, so "tab-reachable" means something.
            var sibling = new Button { Content = "after" };
            var host = new StackPanel { Children = { knob, sibling } };
            var window = new Window { Width = 400, Height = 300, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();

                Assert.True(knob.Focus(), "the knob refused focus — it is not keyboard reachable");
                Assert.True(knob.IsFocused);

                window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
                Assert.Equal(5.5, knob.Value, 6);

                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Assert.Equal(4.5, knob.Value, 6);

                // A page is TEN steps, not one — the whole reason this case exists.
                window.KeyPressQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
                Assert.Equal(9.5, knob.Value, 6);

                window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
                Assert.Equal(10.0, knob.Value, 6);
                window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
                Assert.Equal(0.0, knob.Value, 6);

                // …and back to the catalogue default without a pointer.
                window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
                Assert.Equal(5.0, knob.Value, 6);

                // Tab is NOT the knob's: focus really leaves it.
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Assert.False(knob.IsFocused, "Tab was swallowed by the knob — focus is trapped on it");
                Assert.Equal(5.0, knob.Value, 6);

                // The automation face: a real Slider peer over IRangeValueProvider,
                // named from the caption, whose SetValue lands on the same snap.
                var peer = ControlAutomationPeer.CreatePeerForElement(knob);
                Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
                Assert.Equal("Drive", peer.GetName());

                var range = Assert.IsAssignableFrom<IRangeValueProvider>(peer);
                Assert.Equal(0, range.Minimum);
                Assert.Equal(10, range.Maximum);
                Assert.Equal(5.0, range.Value, 6);
                Assert.Equal(0.5, range.SmallChange, 6);
                Assert.Equal(5.0, range.LargeChange, 6);
                Assert.False(range.IsReadOnly);

                range.SetValue(7.3);
                Assert.Equal(7.5, knob.Value, 6);  // snapped onto the 0.5 grid
                range.SetValue(99.0);
                Assert.Equal(10.0, knob.Value, 6); // and clamped
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheMeter_BuildsAttachesAndLaysOutAtItsDeclaredRowSize()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var meter = new FireLevelMeter { LevelDb = -4.0 };
            WithLaidOut(meter, () =>
            {
                AssertMeasured(meter, "the Fire level meter");
                Assert.Equal(
                    FireLevelScale.RowWidth(FireLevelScale.DefaultSegmentCount,
                        FireLevelScale.DefaultBarWidth, FireLevelScale.DefaultBarGap),
                    meter.Bounds.Width, 1);
                Assert.Equal(FireLevelScale.DefaultMaxBarHeight, meter.Bounds.Height, 1);

                // Every colour it can paint a bar in resolves.
                foreach (var key in new[] { "FireSuccess", "FireWarning", "FireDanger", "FireSurfaceTrack" })
                    Assert.NotEqual(Colors.Gray, FireUnitState.ColorFrom(meter, key));
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheLiveControls_FollowAThemeSwitchWhileAttached_AndReleaseItOnDetach()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            FireDrawnControl[] following = [new FireKnob(), new FireLevelMeter(), new FireTunerMeter()];
            var still = new FireProgress();
            var panel = new StackPanel();
            foreach (var control in following) panel.Children.Add(control);
            panel.Children.Add(still);

            var window = new Window { Width = HostWidth, Height = HostHeight, Content = panel };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.All(following, c => Assert.True(c.IsFollowingThemeVariant, c.GetType().Name));
                Assert.False(still.IsFollowingThemeVariant);

                var before = following.Select(c => c.RepaintRequests).ToArray();
                var stillBefore = still.RepaintRequests;
                window.RequestedThemeVariant =
                    window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light
                        ? Avalonia.Styling.ThemeVariant.Dark
                        : Avalonia.Styling.ThemeVariant.Light;
                for (var i = 0; i < following.Length; i++)
                {
                    Assert.True(following[i].RepaintRequests > before[i],
                        $"{following[i].GetType().Name} did not repaint on a theme switch");
                }

                Assert.Equal(stillBefore, still.RepaintRequests);

                panel.Children.Clear();
                Assert.All(following, c => Assert.False(c.IsFollowingThemeVariant, c.GetType().Name));
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheChipStrip_BuildsAttachesAndLaysOutAtItsDeclaredSize()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var strip = new FireChipStrip { Initials = new[] { "L", "R", "B", "V" } };
            WithLaidOut(strip, () =>
            {
                AssertMeasured(strip, "the Fire chip strip");
                Assert.Equal(
                    FireChipStripGeometry.RowWidth(4,
                        FireChipStripGeometry.DefaultChipSize, FireChipStripGeometry.DefaultGap),
                    strip.Bounds.Width, 1);
                Assert.Equal(FireChipStripGeometry.DefaultChipSize, strip.Bounds.Height, 1);

                // Both identities' border + ink resolve from the real dictionary.
                foreach (var key in new[]
                         { "FireBorderStrong", "FireTextMuted", "FireAccentBorderFaint", "FireAccentBright" })
                {
                    Assert.NotEqual(Colors.Gray, FireUnitState.ColorFrom(strip, key));
                }
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheProgressBar_BuildsAttachesAndLaysOutInBothForms()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var continuous = new FireProgress { Value = 0.62 };
            var segmented = new FireProgress { Segments = 5, Value = 0.4 };
            var host = new StackPanel { Width = 240, Children = { continuous, segmented } };

            WithLaidOut(host, () =>
            {
                AssertMeasured(continuous, "the continuous Fire progress bar");
                AssertMeasured(segmented, "the segmented Fire progress bar");

                Assert.Equal(FireProgressGeometry.TrackHeight, continuous.Bounds.Height, 1);
                Assert.Equal(FireProgressGeometry.SegmentHeight, segmented.Bounds.Height, 1);
                Assert.Equal(2, segmented.FilledSegments);

                // The violet gradient fill is a real gradient from the
                // dictionary, not a flat accent — that is the half a class skin
                // over ProgressBar cannot reach. Asked of the CONTROL, so it
                // proves what the bar will paint with, not merely that a
                // gradient token exists somewhere.
                Assert.IsAssignableFrom<IGradientBrush>(continuous.FillBrush());

                // …and a semantic tone is deliberately FLAT.
                continuous.Tone = FireProgressTone.Warning;
                Assert.Null(continuous.FillBrush());
                // …in the theme's warning hue, read off the theme rather than
                // pinned as a literal (see the knob's ring above).
                Assert.Equal(ThemePresets.Default.Colors[ThemeTokens.FireWarning],
                    FireUnitState.ColorFrom(continuous, "FireWarning"));
            });
        }, CancellationToken.None);
    }

    // ── Drag ghost + drop marker ─────────────────────────────────────────────

    [Fact]
    public async Task TheDragGhostIsFullSize_AndLivesInTheAdornerLayer()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, board, cards) = Board();
            try
            {
                var layer = AdornerLayer.GetAdornerLayer(cards[1]);
                Assert.NotNull(layer);
                var before = layer!.Children.Count;

                using var session = FireDragSession.Begin(cards[1], new Point(10, 10), payload: "od");
                Assert.NotNull(session);

                // Two adorners went in: the marker (under) and the ghost.
                Assert.Equal(before + 2, layer.Children.Count);

                // FULL SIZE — never a shrunken thumbnail and never an OS drag
                // image. The scale/rotation is a RenderTransform on top.
                Assert.Equal(cards[1].Bounds.Width, session!.Ghost.Width, 1);
                Assert.Equal(cards[1].Bounds.Height, session.Ghost.Height, 1);
                Assert.Equal("od", session.Payload);

                // The spec's numbers, spelled out — comparing against the
                // constants would pass at any scale or angle.
                var transform = Assert.IsType<TransformGroup>(session.Ghost.RenderTransform);
                Assert.Contains(transform.Children, t =>
                    t is ScaleTransform s && Math.Abs(s.ScaleX - 0.96) < 1e-9 &&
                    Math.Abs(s.ScaleY - 0.96) < 1e-9);
                Assert.Contains(transform.Children, t =>
                    t is RotateTransform r && Math.Abs(r.Angle - -1.6) < 1e-9);

                // It follows the cursor, keeping the grab offset.
                session.MoveGhost(new Point(60, 40));
                Assert.Equal(new Point(50, 30), session.GhostOrigin);

                session.Dispose();
                Assert.Equal(before, layer.Children.Count);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }

            _ = board;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheDropMarkerNamesALaneAndASlot_AndMeasuresWhereItSays()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, board, cards) = Board();
            try
            {
                using var session = FireDragSession.Begin(cards[0], new Point(5, 5));
                Assert.NotNull(session);

                var lane = new FireDragLane("chain.main", board,
                    cards.Select(c => c.Bounds).ToList());

                // Over the LAST card's right half ⇒ the end slot of THAT lane.
                var last = cards[^1].Bounds;
                var slot = session!.Target(lane, new Point(last.Right - 2, last.Center.Y));
                Assert.Equal("chain.main", slot.LaneId);
                Assert.Equal(cards.Count, slot.Index);
                Assert.True(slot.IsValid);
                Assert.Equal(slot, session.Slot);

                window.UpdateLayout();
                AssertMeasured(session.Marker, "the Fire drop marker");
                Assert.Equal(6.0, session.Marker.Bounds.Width, 1);
                Assert.NotEqual(Colors.Gray, FireUnitState.ColorFrom(session.Marker, "FireAccent"));

                // Re-targeting the SAME slot does no work — a drag ticks at
                // pointer rate over slots tens of pixels wide.
                var updates = session.SlotUpdates;
                session.Target(lane, new Point(last.Right - 1, last.Center.Y));
                Assert.Equal(updates, session.SlotUpdates);

                // Moving to another slot does.
                var first = cards[0].Bounds;
                var earlier = session.Target(lane, new Point(first.X + 1, first.Center.Y));
                Assert.Equal(0, earlier.Index);
                Assert.True(session.SlotUpdates > updates);

                // Leaving every lane clears the marker but keeps the ghost.
                session.ClearTarget();
                Assert.False(session.Slot.IsValid);
                Assert.False(session.Marker.IsVisible);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ADragFromAnUnattachedControl_StartsNothingRatherThanThrowing()
    {
        await HeadlessSessionFixture.Dispatch(() =>
            Assert.Null(FireDragSession.Begin(new Border(), new Point(0, 0))),
            CancellationToken.None);
    }

    // ---- harness ----------------------------------------------------------

    /// <summary>A three-card horizontal rig lane, laid out for real.</summary>
    private static (Window Window, Control Board, List<Control> Cards) Board()
    {
        var cards = Enumerable.Range(0, 3)
            .Select(Control (_) => new Border { Width = 80, Height = 60 })
            .ToList();

        var board = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        foreach (var card in cards) board.Children.Add(card);

        var window = new Window { Width = HostWidth, Height = HostHeight, Content = board };
        window.Show();
        window.UpdateLayout();
        return (window, board, cards);
    }

    private static void WithLaidOut(Control view, Action assert)
    {
        var host = new Border
        {
            Child = view,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var window = new Window { Width = HostWidth, Height = HostHeight, Content = host };
        window.Show();
        try
        {
            window.UpdateLayout();
            assert();
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static void AssertMeasured(Control control, string label) =>
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"{label} laid out as {control.Bounds} — it rendered nothing");
}

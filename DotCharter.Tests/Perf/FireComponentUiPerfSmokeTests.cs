using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Builds each SHARED Fire component for real — attaches it to a window,
///     shows it, runs a layout pass — and asserts the subtree measured non-zero
///     and its brushes resolved.
///     <para>
///         This is the one shape of fault the source lints cannot see. A class
///         that matches nothing, a <c>{DynamicResource}</c> key that resolves to
///         nothing, a <c>ControlTheme</c> that never applies and a
///         <c>Padding</c> handed an <c>x:Double</c> all build perfectly clean;
///         the first three then render a control that STILL ARRANGES and simply
///         has no size or no fill, and the fourth throws only once the control
///         attaches. Measuring is what tells them apart.
///     </para>
///     <para>
///         Each sample is built STANDING ALONE, with no screen's own styles
///         behind it — which is the point: these are the components a later
///         phase assembles screens from, so each has to work on its own. The
///         samples are the compositions the design system documents, which makes
///         this file the executable form of "how do I use this".
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///         session (the app's Styles are what carry Fire.axaml), which hangs the
///         testhost at teardown and is excluded from the default gate. The
///         platform-free halves are <c>UI/FireTokenValueTests</c>,
///         <c>UI/FireStyleClassTests</c> and
///         <c>UI/Converters/FireChamferClipConverterTests</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class FireComponentUiPerfSmokeTests
{
    private const double W = 900;
    private const double H = 600;

    /// <summary>Every §5 component this agent owns, by name.</summary>
    public static TheoryData<string> ComponentNames =>
    [
        "SectionHeader", "NavRow", "PrimaryButton", "GhostButton",
        "SegmentedControl", "StatusChip", "DataListRow", "Overlay", "EmptyState"
    ];

    private static Control Build(string name) => name switch
    {
        "SectionHeader" => SectionHeader(),
        "NavRow" => NavRows(),
        "PrimaryButton" => PrimaryButtons(),
        "GhostButton" => GhostButtons(),
        "SegmentedControl" => Segments(),
        "StatusChip" => StatusChips(),
        "DataListRow" => DataList(),
        "Overlay" => Overlay(),
        "EmptyState" => EmptyState(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown Fire component")
    };

    // ── 1. every component builds, attaches, lays out and has a size ────────

    [Theory]
    [MemberData(nameof(ComponentNames))]
    public async Task FireComponent_buildsAttachesAndLaysOut(string name)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var root = Build(name);
            WithLaidOut(root, () =>
            {
                AssertMeasured(root, name);

                // Every VISIBLE descendant carrying a Fire class must have a
                // size. A class that matched nothing leaves the element in the
                // tree, laid out at 0 — which is exactly what this catches. A
                // part a theme hides on purpose (Graphite's kicker rule) is
                // not laid out at all, and is not a fault.
                foreach (var control in root.GetSelfAndVisualDescendants().OfType<Control>()
                             .Where(c => c.IsEffectivelyVisible)
                             .Where(c => c.Classes.Any(cls => cls.StartsWith("Fire", StringComparison.Ordinal))))
                {
                    AssertMeasured(control, $"{name} → {string.Join('.', control.Classes)}");
                }

                AssertBrushesResolved(root, name);
            });
        }, CancellationToken.None);
    }

    // ── 2. the contracts each component is most likely to lose ──────────────

    /// <summary>
    ///     A disabled lit action is neutral, and its label follows: the label
    ///     sets its own on-accent ink, so without its own disabled rule it drew
    ///     dark ink on the dark neutral fill ("Load chart" in the library).
    /// </summary>
    [Fact]
    public async Task ADisabledLitAction_KeepsItsLabelReadable()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var label = new TextBlock { Classes = { "FireCtaLabel" }, Text = "Load chart" };
            var button = new Button { Classes = { "FireCta" }, Content = label, Width = 240, IsEnabled = false }.Themed();
            WithLaidOut(button, () =>
            {
                var dim = (IBrush)Application.Current!.FindResource("FireTextDim")!;
                var ink = (IBrush)Application.Current!.FindResource("FireTextOnAccent")!;
                Assert.NotEqual(ink, label.Foreground);
                Assert.Equal(dim, label.Foreground);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     THE primary-button contract under Graphite: "an 8 px rounded
    ///     rectangle (no chamfer, no glow, no hover lift)". The template still
    ///     carries the chamfer machinery so a theme can bring the cut back, which
    ///     is why the SQUARE clip is asserted from the rendered geometry rather
    ///     than assumed: the converter once read a 0 cut as unset and drew 16 px.
    /// </summary>
    [Theory]
    [InlineData("FirePrimary")]
    [InlineData("FireCta")]
    public async Task TheLitActions_AreGraphiteFlat_RoundedAndUnlit(string cls)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var button = new Button { Classes = { cls }, Content = "START", Width = 240 }.Themed();
            WithLaidOut(button, () =>
            {
                AssertMeasured(button, cls);

                var cut = button.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Name == "Cut");
                Assert.True(cut != null,
                    $"the {cls} template has no 'Cut' border — the ControlTheme did not apply");
                // No chamfer: the clip, where there is one, keeps every corner.
                if (cut!.Clip is { } clip)
                {
                    Assert.True(clip.FillContains(new Point(cut.Bounds.Width - 2, 2)),
                        $"the {cls} clip {clip.Bounds} cuts its top-right corner: the chamfer is back");
                    Assert.True(clip.FillContains(new Point(2, cut.Bounds.Height - 2)),
                        $"the {cls} clip {clip.Bounds} cuts its bottom-left corner: the chamfer is back");
                }

                Assert.Equal(new CornerRadius(8), cut.CornerRadius);

                // …and no glow: any shadow left on the template is fully transparent.
                foreach (var border in button.GetVisualDescendants().OfType<Border>())
                {
                    foreach (var shadow in border.BoxShadow)
                        Assert.True(shadow.Color.A == 0, $"the {cls} button still glows: {shadow}");
                }
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The segmented control is built ONCE: a <c>ToggleButton</c> and a
    ///     <c>RadioButton</c> wearing the same class must resolve to the SAME
    ///     ControlTheme, and a checked item must actually change colour. A
    ///     segment that looks right and switches nothing is the fault the
    ///     practice queue shipped with.
    /// </summary>
    [Fact]
    public async Task TheSegmentedControl_IsOneThemeForBothSpellings_AndTheActiveItemIsLit()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var on = new ToggleButton { Classes = { "FireSegmentItem" }, IsChecked = true, Content = "ON" }.Themed();
            var off = new ToggleButton { Classes = { "FireSegmentItem" }, Content = "OFF" }.Themed();
            var radio = new RadioButton { Classes = { "FireSegmentItem" }, IsChecked = true, Content = "R" };

            var strip = new StackPanel { Orientation = Orientation.Horizontal, Children = { on, off, radio } };
            var frame = new Border { Classes = { "FireSegment" }, Child = strip }.Themed();

            WithLaidOut(frame, () =>
            {
                AssertMeasured(frame, "FireSegment");
                foreach (var item in new Control[] { on, off, radio }) AssertMeasured(item, "a segment");

                Assert.True(on.Theme != null, "the ToggleButton segment has no ControlTheme — the class did nothing");
                Assert.Same(on.Theme, radio.Theme);

                var lit = Fill(on);
                Assert.True(lit != null, "the active segment has no fill — nothing marks which one is on");
                Assert.NotEqual(Fill(off), lit);

                // Active = the violet 28 % tint, from the token — not a literal.
                var expected = (ISolidColorBrush)Application.Current!.FindResource("FireAccentTintStrong")!;
                Assert.Equal(expected.Color, ((ISolidColorBrush)lit!).Color);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A data list's value columns are FIXED width, so the numbers line up
    ///     down the list. Auto-sizing gives every row an individually correct
    ///     column and the list a staircase — the failure looks like nothing at
    ///     all until you see two rows together.
    /// </summary>
    [Fact]
    public async Task DataListRows_LineTheirValueColumnsUp()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var root = DataList();
            WithLaidOut(root, () =>
            {
                var values = root.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Classes.Contains("FireValueCell"))
                    .ToList();
                Assert.Equal(6, values.Count);

                // Machine data: mono, right-aligned. Both come from the class,
                // so this is also what proves the class matched at all — the
                // edge check below would pass on an unstyled TextBlock, because
                // a stretched one fills its fixed column either way.
                var mono = (FontFamily)Application.Current!.FindResource("FireFontMono")!;
                foreach (var cell in values)
                {
                    Assert.Equal(TextAlignment.Right, cell.TextAlignment);
                    Assert.Equal(mono, cell.FontFamily);
                }

                // Right edges, in the root's space. Three rows × two columns:
                // each column's right edge must be identical down the list.
                var edges = values
                    .Select(v => Math.Round(v.TranslatePoint(new Point(v.Bounds.Width, 0), root)!.Value.X, 2))
                    .Distinct()
                    .ToList();

                Assert.True(edges.Count == 2,
                    "the value columns do not line up down the list — they came out at " +
                    string.Join(", ", edges.Order()) + ". Fixed widths are the point; auto-sizing gives " +
                    "every row an individually correct column and the list a staircase.");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     THE overlay contract: the scrim is TRANSLUCENT BUT DEEP, and the card
    ///     always carries an explicit close control — Esc is not a substitute on
    ///     a controller.
    ///
    ///     <para>
    ///         <b>This test asserted the opposite until 0.57, and had been failing
    ///         ever since <c>aa5e587b</c> reversed the rule — unseen, because this
    ///         suite is outside the default gate.</b> The owner's instruction was
    ///         "all modals should stay modals not own screen": an opaque scrim
    ///         covers the surface the dialog opened over completely, which made
    ///         every dialog read as navigating to a new screen. The two roles had
    ///         been sharing one token and they are opposites — `FireOverlayFill`
    ///         is the OPAQUE ground a SCREEN stands on (still guarded, in
    ///         <c>FireTokenValueTests</c>), `FireScrimFill` is what a MODAL lays
    ///         over the thing it is about.
    ///     </para>
    ///     <para>
    ///         So the band is asserted in BOTH directions. Fully opaque is the
    ///         regression the owner reported; too thin reintroduces the
    ///         readability problem that made someone reach for the opaque one in
    ///         the first place. The translucency must come from the BRUSH, never
    ///         from <c>Opacity</c> — one place to get the alpha right.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheOverlayScrim_IsTranslucentButDeep_AndTheCardAlwaysCarriesAWayOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var root = Overlay();
            WithLaidOut(root, () =>
            {
                var scrim = root.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("FireOverlayScrim"));
                AssertMeasured(scrim, "FireOverlayScrim");

                var fill = Assert.IsAssignableFrom<ISolidColorBrush>(scrim.Background);
                Assert.True(fill.Color.A < 0xFF,
                    $"the overlay scrim is fully opaque (alpha 0x{fill.Color.A:X2}). A MODAL scrim is " +
                    "translucent — an opaque one covers the surface the dialog is about and makes every " +
                    "dialog read as its own screen, which the owner reported in as many words. The opaque " +
                    "token is FireOverlayFill, and it is for SCREEN backgrounds.");
                Assert.True(fill.Color.A >= 0xC0,
                    $"the overlay scrim is only alpha 0x{fill.Color.A:X2}. It has to stay deep enough that " +
                    "body text over it is readable — that is why someone reached for a fully opaque scrim " +
                    "in the first place.");
                Assert.Equal(1.0, scrim.Opacity);

                var close = root.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => b.Classes.Contains("FireOverlayClose"));
                Assert.True(close != null,
                    "the overlay card has no explicit close control — a modal layer with no visible way " +
                    "out is a trap for keyboard and controller users");
                AssertMeasured(close!, "FireOverlayClose");

                // Overflow is contained, never spilled.
                var card = root.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("FireOverlayCard"));
                Assert.True(card.ClipToBounds,
                    "the overlay card does not clip — content that outgrows it paints over the scrim");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     An empty state is never a lone icon in a big box: mono kicker, ONE
    ///     concrete sentence, and the action that fixes it. The banner form —
    ///     for a CONFIGURATION problem — keeps the screen and sits above it.
    /// </summary>
    [Fact]
    public async Task TheEmptyState_SaysWhatIsMissingAndOffersTheFix()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var root = EmptyState();
            WithLaidOut(root, () =>
            {
                var empty = root.GetVisualDescendants().OfType<StackPanel>()
                    .First(p => p.Classes.Contains("FireEmptyState"));
                AssertMeasured(empty, "FireEmptyState");

                Assert.Contains(empty.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Classes.Contains("FireEmptyKicker") && !string.IsNullOrWhiteSpace(t.Text));
                Assert.Contains(empty.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Classes.Contains("FireEmptyBody") && (t.Text?.Length ?? 0) > 20);
                Assert.Contains(empty.GetVisualDescendants().OfType<Button>(), b => b.IsVisible);

                var banner = root.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("FireEmptyBanner"));
                AssertMeasured(banner, "FireEmptyBanner");
                Assert.NotNull(banner.Background);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Every interactive Fire control offers hover, focus AND disabled. The
    ///     focus half is the one that rots: it has no hover to make it obvious
    ///     and no designer looks for it, so it is asserted structurally — a
    ///     button-shaped control with no <c>FocusAdorner</c> falls back to the
    ///     app-wide ring, which is the wrong colour and the wrong offset here.
    /// </summary>
    [Theory]
    [InlineData("FirePrimary")]
    [InlineData("FireCta")]
    [InlineData("FireGhost")]
    [InlineData("FireQuiet")]
    [InlineData("FireBack")]
    [InlineData("FireIconAction")]
    [InlineData("FireOverlayClose")]
    [InlineData("FireListRow")]
    [InlineData("FireNavRow")]
    public async Task EveryInteractiveControl_DeclaresItsOwnFocusVisual(string cls)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var button = new Button { Classes = { cls }, Content = "X" }.Themed();
            WithLaidOut(button, () =>
            {
                AssertMeasured(button, cls);
                Assert.True(button.FocusAdorner != null,
                    $"Button.{cls} declares no FocusAdorner. This menu is keyboard AND controller " +
                    "navigable — a control you can reach with Tab and cannot see is a defect.");
            });
        }, CancellationToken.None);
    }

    /// <summary>The nav row slides 3 px right on hover (the spec's
    /// <c>translateX(3px)</c>), and it has an indicator bar AT REST.</summary>
    [Fact]
    public async Task TheNavRow_HasAnIndicatorAtRest_AndSomethingToSlideWith()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var row = new ToggleButton { Classes = { "FireNavRow" }, Content = "Song library", Width = 240 }.Themed();
            WithLaidOut(row, () =>
            {
                AssertMeasured(row, "FireNavRow");

                var bar = row.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Bar");
                Assert.True(bar != null, "the nav row template has no indicator bar — the theme did not apply");
                Assert.True(bar!.Background is ISolidColorBrush { Color.A: > 0 },
                    "the nav row's indicator is invisible at rest. The spec draws it violet-DIM on every " +
                    "row; without it an unselected rail has no left edge and the selected row's bar reads " +
                    "as decoration rather than as a position in a list.");
                Assert.Equal(3, bar.Bounds.Width);

                // The slide is a RenderTransform, present at rest as identity so
                // the hovered value has a defined base.
                Assert.True(row.RenderTransform != null, "the nav row declares no RenderTransform to slide with");
            });
        }, CancellationToken.None);
    }

    // ── the samples ────────────────────────────────────────────────────────

    private static Control SectionHeader() => new StackPanel
    {
        Classes = { "FireSectionHeader" },
        HorizontalAlignment = HorizontalAlignment.Left,
        Children =
        {
            new StackPanel
            {
                Classes = { "FireKicker" },
                Children =
                {
                    new Border { Classes = { "FireKickerRule" } }.Themed(),
                    new TextBlock { Classes = { "FireEyebrow" }, Text = "WEAK SPOTS" }.Themed()
                }
            },
            new TextBlock { Classes = { "FireTitle" }, Text = "Worth drilling" }.Themed()
        }
    };

    private static Control NavRows()
    {
        var selected = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var labels = new StackPanel
        {
            Children =
            {
                new TextBlock { Classes = { "FireRowTitle" }, Text = "Song library" }.Themed(),
                new TextBlock { Classes = { "FireRowDesc" }, Text = "2,160 songs" }.Themed()
            }
        };
        var count = new TextBlock { Classes = { "FireRowCount" }, Text = "2160" }.Themed();
        Grid.SetColumn(count, 1);
        selected.Children.Add(labels);
        selected.Children.Add(count);

        return new StackPanel
        {
            Width = 262,
            Children =
            {
                new ToggleButton { Classes = { "FireNavRow" }, IsChecked = true, Content = selected }.Themed(),
                new Button
                {
                    Classes = { "FireNavRow" },
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Classes = { "FireRowTitle" }, Text = "Practice" }.Themed(),
                            new TextBlock { Classes = { "FireRowDesc" }, Text = "Today: 2 of 5 done" }.Themed()
                        }
                    }
                }.Themed(),
                new ToggleButton { Classes = { "FireNavRow" }, IsEnabled = false, Content = "Charter" }.Themed()
            }
        };
    }

    private static Control PrimaryButtons() => new StackPanel
    {
        Spacing = 16,
        Width = 320,
        Children =
        {
            new Button { Classes = { "FirePrimary" }, Content = "START SESSION" }.Themed(),
            new Button
            {
                Classes = { "FireCta" },
                Content = new TextBlock { Classes = { "FireCtaLabel" }, Text = "PLAY NOW" }
            }.Themed(),
            new Button { Classes = { "FirePrimary" }, Content = "DISABLED", IsEnabled = false }.Themed()
        }
    };

    private static Control GhostButtons() => new StackPanel
    {
        Spacing = 12,
        Width = 320,
        Children =
        {
            new Button { Classes = { "FireGhost" }, Content = "Song library" }.Themed(),
            new Button { Classes = { "FireGhost", "Compact" }, Content = "Section trainer" }.Themed(),
            new Button { Classes = { "FireGhost", "Accent" }, Content = "RESUME SESSION" }.Themed(),
            new Button { Classes = { "FireGhost" }, Content = "Unavailable", IsEnabled = false }.Themed(),
            new Button { Classes = { "FireQuiet" }, Content = "Reset audio to defaults" }.Themed(),
            new Button
            {
                Classes = { "FireBack" }, Content = "← Back",
                HorizontalAlignment = HorizontalAlignment.Left
            }.Themed()
        }
    };

    /// <summary>The FRAME. Items are added by the caller, because the three
    /// strips below deliberately use three different item spellings.</summary>
    private static Border Strip() => new Border
    {
        Classes = { "FireSegment" },
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 }
    }.Themed();

    private static Control Segments()
    {
        var mono = Strip();
        ((StackPanel)mono.Child!).Children.Add(
            new ToggleButton { Classes = { "FireSegmentItem" }, IsChecked = true, Content = "30 DAYS" }.Themed());
        ((StackPanel)mono.Child).Children.Add(
            new ToggleButton { Classes = { "FireSegmentItem" }, Content = "90 DAYS" }.Themed());
        ((StackPanel)mono.Child).Children.Add(
            new ToggleButton { Classes = { "FireSegmentItem" }, IsEnabled = false, Content = "ALL" }.Themed());

        var radios = Strip();
        ((StackPanel)radios.Child!).Children.Add(
            new RadioButton { Classes = { "FireSegmentItem" }, IsChecked = true, Content = "OVERVIEW" });
        ((StackPanel)radios.Child).Children.Add(
            new RadioButton { Classes = { "FireSegmentItem" }, Content = "HISTORY" });

        var tabs = Strip();
        ((StackPanel)tabs.Child!).Children.Add(
            new ToggleButton { Classes = { "FireSegmentItem", "Tab" }, IsChecked = true, Content = "TRENDS" }.Themed());
        ((StackPanel)tabs.Child).Children.Add(
            new ToggleButton { Classes = { "FireSegmentItem", "Tab" }, Content = "CALENDAR" }.Themed());

        return new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { mono, radios, tabs }
        };
    }

    private static Border Chip(string state, string text) => new Border
    {
        Classes = { "FireStatusPill", state },
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Border { Classes = { "FireDot", state } }.Themed(),
                new TextBlock { Classes = { "FireStatusText", state }, Text = text }.Themed()
            }
        }
    }.Themed();

    private static Control StatusChips() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Left,
        Children =
        {
            Chip("Success", "GUITAR CONNECTED"),
            Chip("Warn", "14 TO FIX"),
            Chip("Danger", "OFFLINE")
        }
    };

    /// <summary>The row grid, with the value columns at FIXED widths — the
    /// contract this component exists to hold.</summary>
    private static Grid RowGrid(string title, string accuracy, string score)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,100,110") };

        var name = new TextBlock { Classes = { "FireRowTitle" }, Text = title }.Themed();
        var acc = new TextBlock { Classes = { "FireValueCell" }, Text = accuracy }.Themed();
        var sc = new TextBlock { Classes = { "FireValueCell" }, Text = score }.Themed();
        Grid.SetColumn(acc, 1);
        Grid.SetColumn(sc, 2);

        grid.Children.Add(name);
        grid.Children.Add(acc);
        grid.Children.Add(sc);
        return grid;
    }

    private static Control DataList()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,100,110"), Margin = new Thickness(22, 0) };
        var song = new TextBlock { Classes = { "FireColumnHead", "Lead" }, Text = "SONG" }.Themed();
        var acc = new TextBlock { Classes = { "FireColumnHead" }, Text = "ACCURACY" }.Themed();
        var score = new TextBlock { Classes = { "FireColumnHead" }, Text = "SCORE" }.Themed();
        Grid.SetColumn(acc, 1);
        Grid.SetColumn(score, 2);
        head.Children.Add(song);
        head.Children.Add(acc);
        head.Children.Add(score);

        return new StackPanel
        {
            Width = 620,
            Children =
            {
                head,
                new Border { Classes = { "FireListRow" }, Child = RowGrid("Kryptonite", "94.2%", "412,900") }.Themed(),
                new Border
                {
                    Classes = { "FireListRow", "selected" },
                    Child = RowGrid("Loser", "88.0%", "391,410")
                }.Themed(),
                new Button
                {
                    Classes = { "FireListRow" },
                    Content = RowGrid("Here Without You", "71.5%", "204,660")
                }.Themed()
            }
        };
    }

    private static Control Overlay()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = new TextBlock { Classes = { "FireScreenTitle" }, Text = "Practice planner" }.Themed();
        var close = new Button { Classes = { "FireOverlayClose" }, Content = "✕" }.Themed();
        Grid.SetColumn(close, 1);
        head.Children.Add(title);
        head.Children.Add(close);

        var body = new TextBlock { Classes = { "FireBody" }, Text = "Shape tomorrow’s session." }.Themed();
        var stack = new StackPanel { Spacing = 16, Children = { head, body } };

        return new Panel
        {
            Children =
            {
                new Border { Classes = { "FireOverlayScrim" } }.Themed(),
                new Border
                {
                    Classes = { "FireOverlayCard" }, Width = 520, Padding = new Thickness(26), Child = stack
                }.Themed()
            }
        };
    }

    private static Control EmptyState() => new StackPanel
    {
        Spacing = 20,
        Children =
        {
            new Border
            {
                Classes = { "FireEmptyBanner" },
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Classes = { "FireEmptyKicker" },
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Text = "NO SERVER"
                        }.Themed(),
                        new TextBlock
                        {
                            Classes = { "FireEmptyBody" },
                            TextAlignment = TextAlignment.Left,
                            Text = "Point DotCharter at a server address to see this room’s records."
                        }.Themed()
                    }
                }
            }.Themed(),
            new StackPanel
            {
                Classes = { "FireEmptyState" },
                Children =
                {
                    new TextBlock { Classes = { "FireEmptyKicker" }, Text = "NO RUNS YET" }.Themed(),
                    new TextBlock
                    {
                        Classes = { "FireEmptyBody" },
                        Text = "Play a song and its runs show up here with per-section accuracy."
                    }.Themed(),
                    new Button
                    {
                        Classes = { "FireGhost" }, Content = "Open the song library",
                        HorizontalAlignment = HorizontalAlignment.Center
                    }.Themed()
                }
            }
        }
    };

    // ── 3. the split component layer resolves, part by part ────────────────

    /// <summary>
    ///     <c>Styles/Fire.axaml</c> is a CONTAINER of <c>&lt;StyleInclude&gt;</c>s
    ///     over <c>Styles/Fire/</c>. This builds a control for every
    ///     single-class selector each part declares and asserts the property
    ///     that part names resolved to THAT token — not to a fallback, not to
    ///     null.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Everything here is DERIVED from the files: the part list comes
    ///         from the container's own includes, the probes from each part's
    ///         markup, and the expected value from the app's live resources. A
    ///         part nobody merges, a part whose styles stopped matching, and a
    ///         token that stopped resolving all fail — and a new part with no
    ///         hand-written case is covered the day it lands.
    ///     </para>
    ///     <para>
    ///         The failure it guards has no runtime signal at all. A style that
    ///         stops resolving does not throw and does not log; the control
    ///         keeps its default brush and STILL ARRANGES, so every bounds
    ///         assertion in this repo sails straight through an unstyled app.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task EveryPartOfTheSplitComponentLayer_ResolvesTheTokensItNames()
    {
        var fireDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "Fire");
        var container = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "Fire.axaml");

        // Merge order, straight off the container's include list.
        var parts = StyleFileText.FilesOf(container).Skip(1).ToList();
        Assert.True(parts.Count >= 2,
            "Fire.axaml merges nothing — either the split was undone or the include scan is broken");

        // The FOLDER and the include list must agree. A part file nobody merges
        // compiles, ships, and styles absolutely nothing.
        Assert.Equal(
            UiSourceTree.GetFiles(fireDir, "*.axaml").Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToList(),
            parts.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList());

        // LAST WINS, exactly the way Avalonia walks the collection — so a class
        // restated by a later part is probed against the value it really gets.
        var probes = new Dictionary<(string El, string Cls, string Property), (string Part, string Token, string? Theme)>();
        foreach (var part in parts)
        {
            foreach (var (el, cls, property, token, theme) in TokenSetters(UiSourceTree.ReadAllText(part)))
                probes[(el, cls, property)] = (Path.GetFileName(part)!, token, theme);
        }

        foreach (var part in parts.Select(Path.GetFileName))
        {
            Assert.True(probes.Values.Any(v => v.Part == part),
                $"{part} contributed no probe — this test would pass over it having looked at nothing");
        }

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var stack = new StackPanel();
            var window = new Window { Width = W, Height = H, Content = stack };
            window.Show();
            try
            {
                var cases = new List<(Control Control, string Cls, string Property, string Part, string Token)>();
                foreach (var ((el, cls, property), (part, token, theme)) in probes)
                {
                    Control control = el switch
                    {
                        "Border" => new Border { Width = 24, Height = 12 },
                        "Button" => new Button { Content = "Fire" },
                        "ToggleButton" => new ToggleButton { Content = "Fire" },
                        _ => new TextBlock { Text = "Fire" }
                    };
                    control.Classes.Add(cls);
                    // a family's rules are its ControlTheme's (#240): the probe wears it
                    if (theme is not null) control.Theme = (ControlTheme)Application.Current!.FindResource(theme)!;
                    stack.Children.Add(control);
                    cases.Add((control, cls, property, part, token));
                }

                window.UpdateLayout();

                foreach (var (control, cls, property, part, token) in cases)
                {
                    Assert.True(control.TryFindResource(token, out var expected),
                        $"{part}: {cls} names {{DynamicResource {token}}}, which the app cannot resolve at all");

                    var actual = property switch
                    {
                        "Background" => control switch
                        {
                            Border b => b.Background,
                            TextBlock t => t.Background,
                            TemplatedControl c => c.Background,
                            _ => null
                        },
                        "Foreground" => control switch
                        {
                            TextBlock t => t.Foreground,
                            TemplatedControl c => c.Foreground,
                            _ => null
                        },
                        _ => control switch
                        {
                            Border br => br.BorderBrush,
                            TemplatedControl c => c.BorderBrush,
                            _ => null
                        }
                    };

                    Assert.True(actual is not null,
                        $"{part}: {control.GetType().Name}.{cls} has a null {property}. Either the selector no " +
                        "longer matches (a split that dropped or reordered it) or the token resolved to " +
                        "nothing — neither throws, neither logs, and the control still arranges.");

                    Assert.True(ReferenceEquals(expected, actual) || Describe(expected) == Describe(actual),
                        $"{part}: {control.GetType().Name}.{cls}'s {property} resolved to {Describe(actual)} " +
                        $"but {token} is {Describe(expected)} — the style is being beaten by something else, " +
                        "or it resolved against a scope that does not carry the Fire tokens and cached the " +
                        "fallback.");
                }
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>Every <c>&lt;Style Selector="Border.FireX"&gt;</c> /
    /// <c>TextBlock.FireX</c> / <c>Button.FireX</c> in this markup whose body sets a brush from a Fire
    /// token — and every <c>^.FireX</c> inside a Button, Border, TextBlock or ToggleButton ControlTheme, which is where
    /// those families live since #240 (with the theme's key, for the probe to wear).
    /// Single-class selectors only: those are the ones a probe can wear without
    /// inventing a state, a parent or a template.</summary>
    private static IEnumerable<(string El, string Cls, string Property, string Token, string? Theme)> TokenSetters(string markup)
    {
        var themes = Regex.Matches(markup,
            """<ControlTheme x:Key="(?<key>[A-Za-z0-9]+)" TargetType="(?<target>Button|Border|TextBlock|ToggleButton)"[^>]*>(?<body>.*?)</ControlTheme>""",
            RegexOptions.Singleline);
        var outside = themes.Aggregate(markup, (m, theme) => m.Replace(theme.Value, ""));
        var sources = themes.Select(t => (Body: t.Groups["body"].Value, Theme: (string?)t.Groups["key"].Value,
                Target: t.Groups["target"].Value))
            .Prepend((Body: outside, Theme: null, Target: ""));
        foreach (var (body, theme, target) in sources)
        {
            foreach (Match style in Regex.Matches(
                         body,
                         """<Style Selector="(?<el>Border|TextBlock|Button|\^)\.(?<cls>[A-Za-z0-9]+)">(?<body>.*?)</Style>""",
                         RegexOptions.Singleline))
            {
                var el = style.Groups["el"].Value;
                if ((el == "^") != (theme is not null)) continue;
                var setter = Regex.Match(
                    style.Groups["body"].Value,
                    """<Setter Property="(?<p>Background|Foreground|BorderBrush)" Value="\{DynamicResource (?<k>Fire[A-Za-z0-9]+)\}" />""");
                if (setter.Success)
                {
                    yield return (el == "^" ? target : el, style.Groups["cls"].Value,
                        setter.Groups["p"].Value, setter.Groups["k"].Value, theme);
                }
            }
        }
    }

    private static string Describe(object? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color.ToString(),
        IGradientBrush gradient => string.Join(
            ',', gradient.GradientStops.Select(s => $"{s.Offset:0.###}:{s.Color}")),
        null => "null",
        _ => brush.GetType().Name
    };

    // ── helpers ────────────────────────────────────────────────────────────

    private static void WithLaidOut(Control view, Action assert)
    {
        var window = new Window { Width = W, Height = H, Content = view };
        window.Show();
        try
        {
            view.Measure(new Size(W, H));
            view.Arrange(new Rect(0, 0, W, H));
            view.UpdateLayout();
            assert();
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static void AssertMeasured(Control control, string what) =>
        Assert.True(control.Bounds.Width > 0.5 && control.Bounds.Height > 0.5,
            $"{what} laid out as {control.Bounds} — a control whose styles failed still arranges, it just " +
            "has no size, and that is the shape every silent XAML fault in this repo takes");

    /// <summary>
    ///     A <c>{DynamicResource}</c> that resolves to nothing is SILENT: the
    ///     property keeps its default, so a typo'd brush key renders as a hole
    ///     rather than throwing. Any Fire-classed Border meant to be a surface
    ///     must therefore have a brush.
    /// </summary>
    private static void AssertBrushesResolved(Control root, string what)
    {
        string[] mustBeFilled =
        [
            "FirePanel", "FireCard", "FireHeroPanel", "FireSegment", "FireStatusPill",
            "FireOverlayScrim", "FireOverlayCard", "FireEmptyBanner", "FireDot", "FireKickerRule"
        ];

        foreach (var border in root.GetSelfAndVisualDescendants().OfType<Border>())
        {
            foreach (var cls in border.Classes)
            {
                if (!mustBeFilled.Contains(cls, StringComparer.Ordinal)) continue;
                Assert.True(border.Background != null,
                    $"{what} → Border.{cls} has a null Background. Its brush token resolved to nothing, which " +
                    "Avalonia does not report — the surface renders as a hole.");
            }
        }
    }

    private static IBrush? Fill(ToggleButton item) =>
        item.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Fill")?.Background;
}

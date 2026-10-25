using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.Controls;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Builds the inspector views for real, then ATTACHES them to a window and
///     runs a layout pass.
///     <para>
///         Both halves are load-bearing, and each caught a separate shipped bug.
///         Construction alone catches a XAML fault — `ViewLocator` swallows one
///         into a "Not Found: … (TargetInvocationException)" placeholder, so a
///         broken view is indistinguishable from a missing one. But attaching is
///         what raises <c>OnAttachedToLogicalTree</c>, and that is where a
///         <c>Button</c> calls <c>Command.CanExecute</c> — a
///         <c>RelayCommand&lt;int&gt;</c> fed a XAML <c>CommandParameter="1"</c>
///         string throws there and takes the whole app down during layout.
///     </para>
///     <para>
///         Named `*UiPerfSmokeTests` because it needs the real Avalonia app
///         session, which cannot share a testhost with the gate and is excluded from the
///         default gate. Run it by hand after touching any inspector view.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class InspectorConstructionUiPerfSmokeTests
{
    /// <summary>Construction only. The outer pane hosts tabs whose controls need
    /// a real platform, so a headless layout pass reports "Invalid size returned
    /// for Measure" — a limitation of the harness, not of the view. Construction
    /// is still the half that catches a XAML fault.</summary>
    [Fact]
    public async Task InspectorView_builds() => await HeadlessSessionFixture.Dispatch(() => { _ = new InspectorView(); }, CancellationToken.None);

    /// <summary>
    ///     The TAB STRIP, on the Fire layer since 0.50 — eight
    ///     <c>ToggleButton.FireSegmentItem</c>s inside a
    ///     <c>Border.TabStrip</c> rail, i.e. the SHARED segmented control
    ///     rather than the local underline-and-pill template it carried since
    ///     0.16.
    ///     <para>
    ///         Three things this proves that the markup cannot. The strip is
    ///         laid out inside <see cref="AttachAndLayoutStrip" />, which hides
    ///         the tab BODIES first — eight tabs measured at once is not what
    ///         this case is about — so what is measured here is exactly the
    ///         chrome this change touched.
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             Every header MEASURED. A `Classes="FireSegmentItem"` that
    ///             resolved to nothing still arranges; it just renders as bare
    ///             grey text (the exact way the song library's FILTERS pill
    ///             shipped), so existence is not the claim — size is.
    ///         </item>
    ///         <item>
    ///             The shared <c>FireSegmentItemToggle</c> <c>ControlTheme</c>
    ///             really applied, checked by the <c>Fill</c> Border its
    ///             template declares. A <c>Theme</c> that does not resolve
    ///             leaves a fully functional ToggleButton wearing Fluent's look.
    ///         </item>
    ///         <item>
    ///             The active segment LIGHTS. Selection is the one thing a tab
    ///             strip has to say, and it is expressed by the theme's
    ///             <c>:checked</c> rule alone now that the local underline is
    ///             gone — so if that rule stops matching the strip shows no
    ///             current tab at all and nothing else fails.
    ///         </item>
    ///     </list>
    /// </summary>
    [Fact]
    public async Task InspectorTabStrip_isTheSharedFireSegment_andEveryHeaderLightsWhenActive()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new InspectorView();
            AttachAndLayoutStrip(view, () =>
            {
                var strip = view.GetVisualDescendants().OfType<Border>()
                    .Single(b => b.Classes.Contains("TabStrip"));
                Assert.True(strip.Bounds is { Width: > 1, Height: > 1 },
                    $"the tab strip laid out as {strip.Bounds} — it rendered nothing");

                var headers = strip.GetVisualDescendants().OfType<ToggleButton>()
                    .Where(t => t.Classes.Contains("FireSegmentItem") && !OverflowTabStrip.GetIsOverflowButton(t))
                    .ToList();

                // All NINE (the strip's "More" button wears the same segment but is no tab, #573) are laid out here. In the real shell the Riff
                // Library and Chord shapes headers are collapsed until the host supplies their
                // sub-view-model, but that is an `IsVisible` BINDING and a null
                // DataContext produces no value at all — so the property keeps
                // its default and the widest header in the strip is measured
                // too, which is the case that decides where the strip wraps.
                Assert.Equal(9, headers.Count);
                var visible = headers.Where(h => h.IsVisible).ToList();
                Assert.Equal(9, visible.Count);

                foreach (var header in visible)
                {
                    Assert.True(header.Bounds is { Width: > 1, Height: > 1 },
                        $"tab header '{header.Tag}' laid out as {header.Bounds} — the FireSegmentItem " +
                        "class matched nothing and it rendered as bare text");

                    // The shared theme's template root. Its absence means the
                    // `Theme` setter did not resolve and Fluent's own chrome is
                    // on screen instead.
                    Assert.True(
                        header.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "Fill"),
                        $"tab header '{header.Tag}' has no 'Fill' border — it is not wearing " +
                        "FireSegmentItemToggle, so the strip is not the shared segmented control");
                }

                // …and selection really shows. IsChecked is a OneWay binding on
                // a null DataContext here, so it produces no value and a local
                // write is what the real view model's true would do.
                var first = visible[0];
                var fill = first.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Fill");
                var idleFill = fill.Background?.ToString();

                first.IsChecked = true;
                view.UpdateLayout();

                Assert.NotEqual(idleFill, fill.Background?.ToString());
                Assert.NotNull(fill.Background);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The 0.50 conversion re-pointed <c>InspectorStyles.axaml</c> at the
    ///     Fire ladders, and this is what proves the chrome ARRIVED. The
    ///     expectations are LITERAL ("Atkinson Hyperlegible" / 15, "JetBrains Mono" /
    ///     9.5 + 2.3 tracking, radius 12) rather than read back out of the same
    ///     token the style reads — deriving them would move both sides together
    ///     and pass straight through the regression, which is the trap the
    ///     settings conversion's own guard names.
    ///     <para>
    ///         It has to run on a view HOSTED INSIDE <c>InspectorView</c>: that
    ///         is where the one <c>StyleInclude</c> lives, so a tab built
    ///         standalone matches none of these classes and every assertion
    ///         below would be measuring Avalonia's defaults.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheInspectorChrome_ReallyWearsTheFireTypeLadder()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new InspectorView();
            AttachAndLayoutStrip(view, () =>
            {
                // #601: the pinned row's summary is the navigation row's type — the
                // UI face at FireSizeXs, bold — on the Issues tab, whose row carries
                // the count and its verbs.
                var issues = view.GetVisualDescendants().OfType<IssuesInspectorView>().Single();
                issues.IsVisible = true;
                view.UpdateLayout();
                var title = issues.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Classes.Contains("HeaderTitle"));
                Assert.Contains("Atkinson Hyperlegible", title.FontFamily.ToString(), StringComparison.Ordinal);
                Assert.Equal(FireTypeLadder.Size("FireSizeXs"), title.FontSize);
                var row = issues.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("InspectorHeader"));
                Assert.True(row.Bounds.Height >= 27.5, $"the pinned row laid out {row.Bounds.Height:0.#} px tall, under its 28 px floor");
                issues.IsVisible = false;

                // …and a sectioned tab wears the same icon-only rail as the Sound tab:
                // 38 px of 30 px segment items, the open one lit, its name on the
                // tooltip and the accessible name, no caption.
                var song = view.GetVisualDescendants().OfType<SongInspectorView>().Single();
                song.IsVisible = true;
                view.UpdateLayout();
                var rail = song.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SongRail");
                Assert.Equal(38, rail.Bounds.Width, 0.5);
                var entries = rail.GetVisualDescendants().OfType<ToggleButton>().ToList();
                Assert.Equal(3, entries.Count);
                Assert.All(entries, e => Assert.True(e.Classes.Contains("FireSegmentItem") && e.Bounds is { Width: 30, Height: 30 },
                    $"{e.Name} laid out as {e.Bounds}"));
                Assert.Single(entries, e => e.IsChecked == true);
                Assert.All(entries, e => Assert.Equal(Avalonia.Automation.AutomationProperties.GetName(e), ToolTip.GetTip(e) as string));
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Roots <see cref="InspectorView" /> and lays out its CHROME.
    ///     <para>
    ///         The tab bodies are hidden first, on purpose: the Mixer tab's
    ///         <c>MasonryPanel</c> throws "Invalid size returned for Measure"
    ///         under the headless platform — verified against the original
    ///         markup, so it is the harness and not this change — and
    ///         <c>Window.Show()</c> IS the initial layout pass, so there is no
    ///         attach-without-measure to fall back to. Every body is
    ///         <c>IsVisible</c>-bound to a view model that is null here, which
    ///         leaves them all at the default TRUE; writing the property is
    ///         therefore not fighting a live binding.
    ///     </para>
    /// </summary>
    private static void AttachAndLayoutStrip(InspectorView view, Action assert)
    {
        var bodies = view.GetLogicalDescendants().OfType<UserControl>().ToList();
        foreach (var body in bodies) body.IsVisible = false;

        var window = new Window { Width = 420, Height = 900, Content = view };
        window.Show();
        try
        {
            view.Measure(new Avalonia.Size(420, 900));
            view.Arrange(new Avalonia.Rect(0, 0, 420, 900));
            assert();
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task SoundInspectorView_builds_and_attaches()
    {
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayout(new SoundInspectorView()),
            CancellationToken.None);
    }

    /// <summary>
    ///     The single-note card's SOUNDING-PITCH row is dressed and takes a size.
    ///     <para>
    ///         It is the fault class this file exists for: the row asks for
    ///         <c>FireFontMono</c> and <c>AccentColor</c> by
    ///         <c>DynamicResource</c>, and a subtree whose resources resolved
    ///         against nothing still ARRANGES — it just has no size. With a null
    ///         DataContext the binding produces no value at all, so the text is
    ///         written locally (the same idiom the tab strip's guard uses); what
    ///         is proven is that the row is in the laid-out tree and measures.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheSoundTabsPitchRow_LaysOutNonZero()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new SoundInspectorView();
            var readout = view.GetLogicalDescendants().OfType<TextBlock>()
                .Single(t => t.Name == "NotePitchReadout");

            var window = new Window { Width = 420, Height = 900, Content = view };
            window.Show();
            try
            {
                readout.Text = "A#2";
                view.Measure(new Avalonia.Size(420, 900));
                view.Arrange(new Avalonia.Rect(0, 0, 420, 900));
                // Since #118 the readout sits in the card's title row at its
                // DESIRED width (it was a stretched star column), so the text
                // written above has to reach it through a real layout pass: a
                // direct Measure on the root skips the invalidated child.
                window.UpdateLayout();

                Assert.True(readout.Bounds is { Width: > 1, Height: > 1 },
                    $"the sounding-pitch readout laid out as {readout.Bounds} — it rendered nothing");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The lyrics tab gained the auto-sync button and its progress bar, which
    ///     added two <c>{localization:Loc}</c> references and an
    ///     <c>IsVisible</c> binding inside the raw-editor stack. Attaching is the
    ///     half that matters here: the button calls <c>CanExecute</c> on
    ///     <c>AutoSyncCommand</c> during layout, and a null DataContext is
    ///     exactly the state that has to survive it.
    /// </summary>
    [Fact]
    public async Task LyricsInspectorView_builds_and_attaches()
    {
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayout(new LyricsInspectorView()),
            CancellationToken.None);
    }

    /// <summary>
    ///     The Mixer and Song tabs, rebuilt in 0.42.2 onto the shared inspector
    ///     chrome — which replaced nine <c>Expander</c>s with the
    ///     <c>Disclosure</c> idiom (a <c>ToggleButton</c> plus an
    ///     <c>IsVisible</c>-bound <c>InspectorCard</c>).
    ///     <para>
    ///         Attaching is the half that matters. The disclosure bodies are bound
    ///         to <c>{Binding #XDisclosure.IsChecked}</c>, an ELEMENT-NAME binding
    ///         that only resolves once the tree is rooted — an unresolvable name
    ///         is silent, and the section simply never appears. And every card
    ///         carries <c>Padding</c>/<c>Margin</c> from the token scale, where
    ///         handing a <c>Space*</c> (an <c>x:Double</c>) to a
    ///         <c>Thickness</c> throws during layout on a build that is perfectly
    ///         clean.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     Annotations, rebuilt in 0.42.2 — it was the one tab with no
    ///     <c>InspectorHeader</c> at all, and its empty state was a hand-copy of
    ///     <c>StackPanel.InspectorEmpty</c>'s setters.
    ///     <para>
    ///         Attaching is what matters: the composer's Add button calls
    ///         <c>CanExecute</c> during layout with a null DataContext, which is
    ///         exactly the state that has to survive it.
    ///     </para>
    ///     <para>
    ///         What this does NOT prove: that the shared classes resolved. The
    ///         view is built standalone here, while the app hosts it inside
    ///         <c>InspectorView</c>, which is what carries the one
    ///         <c>StyleInclude</c> — so out here every class matches no selector
    ///         and silently styles nothing, and an unstyled <c>Border</c> still
    ///         measures. The class names are checked as markup by
    ///         <c>InspectorDisclosureWiringTests</c>; that the parent supplies
    ///         them is the same arrangement Sound, Song, Lyrics, Issues and
    ///         Phrases have always used.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task NotesInspectorView_builds_and_attaches()
    {
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayoutMeasuredSection(new NotesInspectorView(), "NotesComposer"),
            CancellationToken.None);
    }

    /// <summary>Song takes the full pass — construction, attach, layout, and the
    /// assertion that its open section (the rail's first, #601) measured non-zero.</summary>
    [Fact]
    public async Task SongInspectorView_builds_and_attaches()
    {
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayoutMeasuredSection(new SongInspectorView(), "SongKeyBody"),
            CancellationToken.None);
    }

    /// <summary>
    ///     THE MIXER ROW (0.51, rebuilt from <c>Chart Editor.dc.html</c>).
    ///     <para>
    ///         Two things the markup cannot say. First, the RAILS really line
    ///         up: every channel in the tab — the levels bus, a stem, a playback
    ///         effect, an EQ filter — wears <c>TextBlock.MixerLabel</c> and
    ///         <c>TextBlock.MixerValue</c>, and the whole point of putting those
    ///         two widths in the style rather than on each Grid is that the
    ///         faders then start and end on one line. A cell that lost its class
    ///         still measures; it just sits somewhere else, which is the exact
    ///         shape of the drift this asserts against.
    ///     </para>
    ///     <para>
    ///         Second, the FADER really shrank. Fluent's slider template
    ///         measures 44 px inside a three-row Grid, so the pane scrolled
    ///         nearly twice as far as the design does; the fix is a
    ///         <c>Height</c>, and a <c>MinHeight</c> silently does nothing. The
    ///         literal 24 is deliberate — reading it back out of the same style
    ///         would move both sides together.
    ///     </para>
    ///     <para>
    ///         THE EQ SECTION IS COLLAPSED FIRST, through its own disclosure.
    ///         Its curve is a <c>Polyline</c> with <c>Stretch="Fill"</c>, and a
    ///         filled shape with no points has empty geometry bounds — the fill
    ///         scale divides by zero and the panel above it reports "Invalid
    ///         size returned for Measure". That is the harness (there is no
    ///         transport data here), not the view, and it is why this tab was
    ///         construction-only until 0.51; the product's own collapse is used
    ///         rather than writing <c>IsVisible</c>, which would fight the
    ///         element-name binding that owns it.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task MixerInspectorView_buildsAndAttaches_andItsRailsLineUp()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new MixerInspectorView();
            var window = new Window { Width = 420, Height = 900, Content = view };
            window.Show();
            var labels = new List<TextBlock>();
            var values = new List<TextBlock>();
            try
            {
                // #601: one section at a time behind the rail; each is opened as its
                // entry's click does, and its rows join the alignment check. The EQ
                // section is left closed (its curve has no data under the harness).
                foreach (var section in new[] { "MixerLevelsCard", "MixerPlaybackFxBody", "MixerMidiBody" })
                {
                    view.Rail.Choose(section);
                    view.Measure(new Avalonia.Size(420, 900));
                    view.Arrange(new Avalonia.Rect(0, 0, 420, 900));
                    window.UpdateLayout();
                    Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"the Mixer laid out as {view.Bounds}");
                    labels.AddRange(view.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.Classes.Contains("MixerLabel") && t.IsEffectivelyVisible && t.Bounds.Width > 0));
                    values.AddRange(view.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.Classes.Contains("MixerValue") && t.IsEffectivelyVisible && t.Bounds.Width > 0));
                    if (section == "MixerMidiBody") AssertMidiSection(view);
                }
            }
            finally
            {
                window.Close();
            }

            // LEVELS (4) + PLAYBACK FX (3) + MIDI (1: Offset; the GM Instrument row
            // went with the wavetable, #138) with the EQ collapsed and no project
            // loaded; the stem rows only exist once a separation has.
            Assert.True(labels.Count >= 8, $"only {labels.Count} mixer labels laid out");
            Assert.True(values.Count >= 7, $"only {values.Count} mixer values laid out");

            var labelLeft = labels.Select(l => Math.Round(l.TranslatePoint(default, view)!.Value.X)).ToList();
            var valueRight = values
                .Select(v => Math.Round(v.TranslatePoint(new Point(v.Bounds.Width, 0), view)!.Value.X))
                .ToList();

            Assert.True(labelLeft.Distinct().Count() == 1,
                "the mixer's label rail is ragged — every row must share one left edge, at " +
                string.Join(" / ", labelLeft.Distinct().Order()));
            Assert.True(valueRight.Distinct().Count() == 1,
                "the mixer's value rail is ragged — every row must share one right edge, at " +
                string.Join(" / ", valueRight.Distinct().Order()));

            // Only the faders that really laid out — a slider inside a hidden
            // section is still `IsVisible` itself and arranges to nothing. The count
            // floor is what keeps this from passing vacuously.
            var faders = view.GetVisualDescendants().OfType<Slider>()
                .Where(s => s.Classes.Contains("MixerSlider") && s.Bounds.Height > 0)
                .ToList();
            Assert.True(faders.Count >= 7, $"only {faders.Count} mixer faders laid out");
            foreach (var fader in faders)
                Assert.Equal(24d, fader.Bounds.Height);
        }, CancellationToken.None);
    }

    private static void AssertMidiSection(MixerInspectorView view)
    {
        {

            // ABI v38: the chart synth + instrument route toggles in the MIDI card.
            foreach (var name in new[] { "ChartSynthToggle", "InstrumentThroughRigToggle" })
            {
                var toggle = view.GetVisualDescendants().OfType<CheckBox>().SingleOrDefault(c => c.Name == name);
                Assert.NotNull(toggle);
                Assert.True(toggle!.Bounds.Width > 1 && toggle.Bounds.Height > 1,
                    $"{name} laid out as {toggle.Bounds} — it is in the tree but its style or template failed");
            }

            // #164: the Synth tone switch, both halves laid out.
            foreach (var name in new[] { "ChartSynthToneSongTones", "ChartSynthToneOwnRig" })
            {
                var half = view.GetVisualDescendants().OfType<RadioButton>().SingleOrDefault(c => c.Name == name);
                Assert.NotNull(half);
                Assert.True(half!.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {half.Bounds}");
            }

            // #160: the playing style is hosted in the same card.
            var style = view.GetVisualDescendants().OfType<ChartSynthPlayStyleView>().SingleOrDefault();
            Assert.NotNull(style);
            Assert.True(style!.Bounds is { Width: > 1, Height: > 1 }, $"the playing style laid out as {style.Bounds}");
        }
    }

    /// <summary>
    ///     THE FEHLER TAB (0.51, rebuilt from the same mockup).
    ///     <para>
    ///         It carries the three-column row — a mono code chip, the
    ///         ellipsised message, a right-aligned mono location — and, in the
    ///         footer, Audio lint over Export readiness. Attaching is what
    ///         matters: every row's command resolves through
    ///         <c>$parent[UserControl]</c> and its location cell is a
    ///         <c>MultiBinding</c> over the project's beat map, neither of which
    ///         exists until the tree is rooted.
    ///     </para>
    ///     <para>
    ///         It also pins the ONE thing the selection idiom rests on: the row
    ///         is a Button, and a Button in this app keeps focus after a click,
    ///         so <c>:focus-within</c> is what marks the issue you are on. If
    ///         that pseudo-class stopped matching, the accent wash and the 3 px
    ///         spine would simply never appear and nothing else would fail.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task IssuesInspectorView_buildsAndAttaches_andTheFocusedRowLights()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var host = new InspectorView();
            AttachAndLayoutStrip(host, () =>
            {
                var issues = host.GetVisualDescendants().OfType<IssuesInspectorView>().Single();
                issues.IsVisible = true;
                host.UpdateLayout();

                Assert.True(issues.Bounds is { Width: > 1, Height: > 1 },
                    $"the FEHLER tab laid out as {issues.Bounds} — it rendered nothing");

                // The footer's two labelled rows, which are the tab's own
                // report rather than a per-element finding.
                var footerTitles = issues.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Classes.Contains("FooterRowTitle"))
                    .ToList();
                Assert.Equal(2, footerTitles.Count);
                foreach (var t in footerTitles)
                {
                    Assert.True(t.Bounds is { Width: > 1, Height: > 1 },
                        $"a footer row title laid out as {t.Bounds}");
                }

                // The export verdict is THREE gated TextBlocks — dim /
                // green / danger — and which one shows is a data question, so
                // it is pinned as markup by
                // `UI/IssuesTabVerdictTests` rather than measured here with no
                // project loaded. What matters at runtime is that all three
                // really carry the class the style dresses.
                Assert.Equal(3, issues.GetVisualDescendants().OfType<TextBlock>()
                    .Count(t => t.Classes.Contains("FooterVerdict")));

                // #952: Prepare for Rocksmith left the export row (every export applies DLC
                // Builder's steps to its copy; the chart's context menu puts them in the project).
                Assert.DoesNotContain(issues.GetVisualDescendants().OfType<Button>(), b => b.Name == "PrepareForRocksmithButton");

                // The selection idiom. A row is built here by hand because the
                // list is empty with no project: what is being pinned is that
                // the `:focus-within` rule reaches the spine at all.
                var row = new Button { Classes = { "IssueRow" }, Content = new Border { Classes = { "IssueSpine" } } };
                var probe = new StackPanel { Children = { row } };
                issues.Content = probe;
                host.UpdateLayout();

                var spine = row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("IssueSpine"));
                var idle = spine.Background?.ToString();

                row.Focus();
                host.UpdateLayout();

                Assert.True(row.IsFocused, "the issue row would not take focus — the selection mark is unreachable");
                Assert.NotEqual(idle, spine.Background?.ToString());
                Assert.NotNull(spine.Background);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Phrases, polished in 0.42.2: its hand-tinted warning line became
    ///     <c>Border.FieldIssue.Warn</c>, and its empty state gained the leading
    ///     <c>PathIcon</c> the shared class styles. Attach matters — the row
    ///     buttons bind commands through <c>$parent[UserControl]</c>, which only
    ///     resolves once the tree is rooted.
    /// </summary>
    [Fact]
    public async Task PhrasesInspectorView_builds_and_attaches()
    {
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayout(new PhrasesInspectorView()),
            CancellationToken.None);
    }

    /// <summary>The Amp &amp; FX rig is the other big hand-written XAML surface,
    /// and it is the app's ONE tone rig since v0.43 — the retired rack that used
    /// to stand beside it is gone, so everything it proved has to be proved
    /// here.</summary>
    [Fact]
    public async Task AmpFxRigView_builds()
    {
        await HeadlessSessionFixture.Dispatch(
            () => { _ = new DotCharter.Tone.UI.Views.PlayMode.AmpFxRigView(); }, CancellationToken.None);
    }

    /// <summary>
    ///     Constructing the view is NOT enough for the rig: the board's unit
    ///     card, the signal chip, the gear row and the module editor's faces are
    ///     all ITEM TEMPLATES, and a template is not built until an item exists.
    ///     A layout pass over an unbound screen realizes the chrome, measures it,
    ///     passes, and never touches the four templates where an unresolvable
    ///     resource or a <c>Space*</c>-in-<c>Padding</c> actually hides.
    ///     <para>
    ///         So this hands it a real chain — one module per skin — and lays the
    ///         screen out for real. A card that failed to render still ARRANGES;
    ///         it just has no size, which is the shape every silent XAML fault on
    ///         this surface has taken.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task AmpFxRig_realizesEveryUnitCardAndEveryRegion()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var dsp = new FakeDspChainService(EverySkinCatalog());
            var rig = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), LocalizationService.Instance);
            foreach (var type in rig.AvailableModules.ToList()) rig.AddGear(type);
            // The dock only builds a face for a SELECTED unit, and the face is
            // where three of the four item templates live.
            rig.SelectNodeCommand.Execute(rig.Nodes[0]);

            var vm = new AmpFxRigViewModel(rig);
            var view = new DotCharter.Tone.UI.Views.PlayMode.AmpFxRigView { DataContext = vm };
            var window = new Window { Width = 1720, Height = 1000, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();

                // header · signal strip · board · module editor dock · gear
                // panel · footer.
                foreach (var name in new[]
                         {
                             "RigHeader", "RigSignalStrip", "RigBoard", "RigDock",
                             "RigDockBody", "RigGearPanel", "RigFooter"
                         })
                {
                    var region = view.GetVisualDescendants().OfType<Control>()
                        .SingleOrDefault(c => c.Name == name);
                    Assert.True(region is not null, $"region '{name}' is not in the visual tree at all");
                    Assert.True(region!.Bounds is { Width: > 1, Height: > 1 },
                        $"region '{name}' laid out as {region.Bounds} — it rendered nothing");
                }

                // Every unit on the board drew SOMETHING — one CARD per node,
                // each with real size. The board renders SEGMENTS since the
                // lane fold, so the containers of `RigBoardItems` are runs, not
                // cards; the cards are the `FireUnitCard` borders inside them.
                // The live junction pair draws as the split region's frame
                // rather than as cards, so it is excluded — but only the pair
                // the region is actually keyed on (a stray/bypassed junction
                // still renders as an ordinary card).
                var board = view.GetVisualDescendants().OfType<ItemsControl>()
                    .Single(c => c.Name == "RigBoardItems");
                var cards = board.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("FireUnitCard")).ToList();
                var expected = rig.HasSplitRegion ? rig.Nodes.Count - 2 : rig.Nodes.Count;
                Assert.Equal(expected, cards.Count);
                foreach (var card in cards)
                {
                    Assert.True(card.Bounds is { Width: > 1, Height: > 1 },
                        $"a unit card laid out as {card.Bounds} — it rendered nothing");
                }

                // EVERY control on a card is visible without scrolling, up to
                // the tallest honest card (the NAM amp: a file strip + two knob
                // rows of its nine params). At Height=380 the second row's
                // value readouts were half-cut by the ACTIVE foot and nothing
                // failed — the ScrollViewer swallowed them (live-audit finding,
                // 2026-08-11). Content taller than that may scroll; nine knobs
                // may not.
                foreach (var card in cards.Where(c => !c.Classes.Contains("Compact")))
                {
                    var body = card.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                    if (body is null) continue; // a junction-skin card has no body scroller
                    var knobCount = body.GetVisualDescendants().OfType<FireKnob>().Count();
                    if (knobCount > 9) continue;

                    // The HEADROOM matters: headless, the file strip's
                    // library dropdown is collapsed (no capture library in the
                    // fixture), so a bare extent<=viewport check passed at the
                    // broken height by a single pixel while the real install —
                    // where that ~36 px row shows — clipped. Demand the row's
                    // worth of slack on any card that carries a file strip.
                    var headroom = body.GetVisualDescendants()
                        .OfType<Border>().Any(b => b.Classes.Contains("FireLoadStrip"))
                        ? 36.0
                        : 0.5;
                    // DesiredSize, not Extent: the scroller stretches its
                    // content presenter to the viewport, so Extent==Viewport
                    // whether the content fits or exactly fills — only the
                    // content's own desired height says what it NEEDS.
                    var needed = ((Control)body.Content!).DesiredSize.Height;
                    Assert.True(needed + headroom <= body.Viewport.Height + 1,
                        $"a {knobCount}-knob card clips its controls: body needs " +
                        $"{needed:0.#} (+{headroom:0.#} library-row headroom) vs viewport " +
                        $"{body.Viewport.Height:0.#} — the card height no longer holds the tallest honest card");
                }

                // The gear panel is collection-backed; an EMPTY ItemsControl
                // measures perfectly well, so size alone would miss a broken
                // ItemsSource.
                var gear = view.GetVisualDescendants().OfType<ItemsControl>()
                    .Single(c => c.Name == "RigGearGroups");
                Assert.NotEmpty(gear.GetRealizedContainers());
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The header's A/B compare buttons reach their commands through
    ///     <c>Rig.…</c> — a hop off the screen's view model onto the chain's.
    ///     <para>
    ///         A wrong path there does not crash and does not fail the build: the
    ///         binding resolves to null, the button renders disabled and blank,
    ///         and the feature is silently absent. Same class of failure as the
    ///         four silent XAML faults this file already guards, so it gets the
    ///         same treatment — realize the header and assert a button actually
    ///         holds the command.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task AmpFxRigHeader_BindsTheCompareCommandsThroughTheChainViewModel()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), LocalizationService.Instance);
            var view = new DotCharter.Tone.UI.Views.PlayMode.AmpFxRigView
            {
                DataContext = new AmpFxRigViewModel(rig)
            };
            var window = new Window { Width = 1600, Height = 1000, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();

                var commands = view.GetVisualDescendants().OfType<Button>()
                    .Select(b => b.Command)
                    .Where(c => c is not null)
                    .ToList();

                // Since the design's top bar (2026-09-08) SWITCH lives in the
                // cog's MenuFlyout (RigHeaderMenu → RigCompareButton), which is
                // not in the visual tree while closed — read the flyout's items.
                var cog = view.FindControl<Button>("RigHeaderMenu")!;
                var menu = Assert.IsType<MenuFlyout>(cog.Flyout);
                // A closed flyout's items have no DataContext yet; open it once
                // so the bindings resolve, then read what they bound.
                menu.ShowAt(cog);
                window.UpdateLayout();
                commands.AddRange(menu.Items.OfType<MenuItem>().Select(m => m.Command).Where(c => c is not null));
                menu.Hide();

                Assert.Contains(rig.ToggleCompareCommand, commands);
                Assert.Contains(rig.SeedCompareCommand, commands);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>One module per <see cref="PedalSkin"/>, with the metadata each
    /// specialized face needs (the treadle's <c>position</c>, the neural amp's
    /// <c>modelPath</c> string param, a labelled param so the selector surfaces
    /// render too).</summary>
    private static IReadOnlyList<DspModuleType> EverySkinCatalog() =>
    [
        new("overdrive", "Overdrive", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new("amp", "Amp", [
            new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f),
            new DspParamInfo("voicing", "Voicing", 0f, 1f, 0f) { ValueLabels = ["Clean", "Crunch"] }
        ]),
        // The real namamp face: seven amp controls plus the two level trims, and
        // the capture path the faceplate's model plate is built from.
        new("namamp", "Neural Amp (NAM)", [
            new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f),
            new DspParamInfo("bass", "Bass", 0f, 1f, 0.5f),
            new DspParamInfo("mid", "Mid", 0f, 1f, 0.5f),
            new DspParamInfo("treble", "Treble", 0f, 1f, 0.5f),
            new DspParamInfo("presence", "Presence", 0f, 1f, 0.5f),
            new DspParamInfo("resonance", "Resonance", 0f, 1f, 0.5f),
            new DspParamInfo("master", "Master", 0f, 1f, 0.5f),
            new DspParamInfo("inputDb", "Input (dB)", -24f, 24f, 0f),
            new DspParamInfo("outputDb", "Output (dB)", -24f, 24f, 0f)
        ]) { StringParams = [new DspStringParamInfo("modelPath", "NAM Model", "*.nam;*.aidax")] },
        new("cab", "Cab", [
            new DspParamInfo("level", "Level", 0f, 1f, 0.8f),
            new DspParamInfo("model", "Model", 0f, 1f, 0f) { ValueLabels = ["1x12", "4x12"] }
        ]),
        new("graphiceq", "Graphic EQ", [new DspParamInfo("band1", "100", -12f, 12f, 0f)]),
        new("wah", "Wah", [
            new DspParamInfo("position", "Position", 0f, 1f, 0.5f),
            new DspParamInfo("mode", "Mode", 0f, 1f, 0f) { ValueLabels = ["Cry", "Vocal"] }
        ]),
        new("splitter", "Splitter", [new DspParamInfo("enabledBranches", "Branches", 2f, 3f, 2f)]),
        new("merger", "Merger", [new DspParamInfo("mode", "Mode", 0f, 1f, 0f) { ValueLabels = ["Average", "Sum"] }])
    ];

    /// <summary>A sectioned tab (#601): its open section measured non-zero — a view whose styles
    /// failed still arranges, it just has no size.</summary>
    private static void AttachAndLayoutMeasuredSection(Control view, string section)
    {
        AttachAndLayout(view);
        Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{view.GetType().Name} laid out as {view.Bounds} — it rendered nothing.");
        var body = view.GetLogicalDescendants().OfType<Control>().Single(c => c.Name == section);
        Assert.True(body.IsVisible && body.Bounds is { Width: > 1, Height: > 1 },
            $"{view.GetType().Name}'s {section} laid out as {body.Bounds} (visible {body.IsVisible}).");
    }

    private static void AttachAndLayout(Control view)
    {
        var window = new Window { Width = 420, Height = 900, Content = view };
        // Show() is what roots the tree and raises OnAttachedToLogicalTree. The
        // layout pass is driven through the VIEW, not the window: measuring a
        // headless Window directly reports "Invalid size returned for Measure".
        window.Show();
        try
        {
            view.Measure(new Avalonia.Size(420, 900));
            view.Arrange(new Avalonia.Rect(0, 0, 420, 900));
        }
        finally
        {
            window.Close();
        }
    }
}

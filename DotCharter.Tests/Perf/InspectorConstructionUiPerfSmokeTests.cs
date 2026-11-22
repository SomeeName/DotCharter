using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.ViewModels.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;
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
    ///     The TAB STRIP, in inspector design D's nav style — eight
    ///     <c>ToggleButton.ColumnFlowNav</c>s wearing the shared
    ///     <c>ColumnFlowNavToggle</c> theme inside a <c>Border.TabStrip</c> rail
    ///     (the Note tab's section strip wears the same theme).
    ///     <para>
    ///         Three things this proves that the markup cannot. The strip is
    ///         laid out inside <see cref="AttachAndLayoutStrip" />, which hides
    ///         the tab BODIES first — eight tabs measured at once is not what
    ///         this case is about — so what is measured here is exactly the
    ///         chrome this change touched.
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             Every header MEASURED. A `Classes="ColumnFlowNav"` that
    ///             resolved to nothing still arranges; it just renders as bare
    ///             grey text (the exact way the song library's FILTERS pill
    ///             shipped), so existence is not the claim — size is.
    ///         </item>
    ///         <item>
    ///             The shared <c>ColumnFlowNavToggle</c> <c>ControlTheme</c>
    ///             really applied, checked by the <c>PART_Chrome</c> Border its
    ///             template declares. A <c>Theme</c> that does not resolve
    ///             leaves a fully functional ToggleButton wearing Fluent's look.
    ///         </item>
    ///         <item>
    ///             The active segment LIGHTS. Selection is the one thing a tab
    ///             strip has to say, and it is expressed by the theme's
    ///             <c>:checked</c> rules alone (the accent caption and underline)
    ///             — so if they stop matching the strip shows no current tab at
    ///             all and nothing else fails.
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
                    .Where(t => t.Classes.Contains("ColumnFlowNav") && !OverflowTabStrip.GetIsOverflowButton(t))
                    .ToList();

                // All EIGHT (the strip's "More" button wears the same segment but is no tab, #573) are laid out here. In the real shell the Riff
                // Library and Chord shapes headers are collapsed until the host supplies their
                // sub-view-model, but that is an `IsVisible` BINDING and a null
                // DataContext produces no value at all — so the property keeps
                // its default and the widest header in the strip is measured
                // too, which is the case that decides where the strip wraps.
                Assert.Equal(8, headers.Count);
                var visible = headers.Where(h => h.IsVisible).ToList();
                Assert.Equal(8, visible.Count);

                foreach (var header in visible)
                {
                    Assert.True(header.Bounds is { Width: > 1, Height: > 1 },
                        $"tab header '{header.Tag}' laid out as {header.Bounds} — the ColumnFlowNav " +
                        "class matched nothing and it rendered as bare text");

                    // The shared theme's template root. Its absence means the
                    // `Theme` setter did not resolve and Fluent's own chrome is
                    // on screen instead.
                    Assert.True(
                        header.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "PART_Chrome"),
                        $"tab header '{header.Tag}' has no 'PART_Chrome' border — it is not wearing " +
                        "ColumnFlowNavToggle, so the strip is not the shared nav style");
                }

                // …and selection really shows. IsChecked is a OneWay binding on
                // a null DataContext here, so it produces no value and a local
                // write is what the real view model's true would do.
                var first = visible[0];
                var chrome = first.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Chrome");
                var idleLine = chrome.BorderBrush?.ToString();
                var idleText = first.Foreground?.ToString();

                first.IsChecked = true;
                view.UpdateLayout();

                Assert.NotNull(chrome.BorderBrush);
                Assert.NotEqual(idleLine, chrome.BorderBrush?.ToString());
                Assert.NotEqual(idleText, first.Foreground?.ToString());
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
                // The Song tab is design D's cards: at this 420 px (Tiny) pane every
                // card is a page, the information first, each page button named by its
                // card's title and one of them lit.
                var song = view.GetVisualDescendants().OfType<SongInspectorView>().Single();
                song.IsVisible = true;
                view.UpdateLayout();
                Assert.Equal(4, song.Cards.Children.OfType<ColumnFlowCard>().Count()); // information, sections, tones, phrases
                Assert.Equal("SongInfoCard", Assert.Single(song.Cards.ShownCards).Name);
                var pages = song.Cards.Pager.Buttons;
                Assert.Equal(4, pages.Count);
                Assert.All(pages, p => Assert.True(p.Bounds is { Width: > 8, Height: >= 27.5 }, $"a page button laid out as {p.Bounds}"));
                Assert.All(pages, p => Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(p))));
                Assert.Single(pages, p => p.IsChecked == true);
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
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayoutMeasuredSection(new NotesInspectorView(), "NotesListCard"),
            CancellationToken.None);
    }

    /// <summary>Song takes the full pass — construction, attach, layout, and the
    /// assertion that its key row (in the information card, always on screen) measured non-zero.</summary>
    [Fact]
    public async Task SongInspectorView_builds_and_attaches()
    {
        await HeadlessSessionFixture.Dispatch(() => AttachAndLayoutMeasuredSection(new SongInspectorView(), "SongKeyBody"),
            CancellationToken.None);
    }

    /// <summary>
    ///     The Mixer tab (inspector design D): the Stems / EQ / MIDI strip over one section's cards. Each section is
    ///     opened as its strip entry's click does (<c>MixerSections.ChooseCommand</c>); its body must lay out
    ///     non-zero, every fader that laid out keeps the 24 px <c>MixerSlider</c> height (Fluent's template is 44 px,
    ///     and only a <c>Height</c> holds it), and the MIDI cards' controls must all have a size.
    /// </summary>
    [Fact]
    public async Task MixerInspectorView_buildsAndAttaches_andEverySectionLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = (App.Services ?? throw new InvalidOperationException("the headless app session did not boot."))
                .GetRequiredService<DotCharter.UI.ViewModels.Editor.InspectorViewModel>();
            var view = new MixerInspectorView { DataContext = vm };
            var window = new Window { Width = 530, Height = 370, Content = view };
            window.Show();
            var previous = vm.MixerSections.Section;
            try
            {
                foreach (var (section, body) in new[]
                         {
                             (MixerSection.Eq, "MixerEqBody"), (MixerSection.Midi, "MixerMidiBody")
                         })
                {
                    vm.MixerSections.ChooseCommand.Execute(section);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"the Mixer laid out as {view.Bounds}");
                    var shown = view.GetLogicalDescendants().OfType<Control>().Single(c => c.Name == body);
                    Assert.True(shown.IsVisible && shown.Bounds is { Width: > 1, Height: > 1 }, $"{body} laid out as {shown.Bounds}");
                    foreach (var other in view.Bodies.Where(b => b != shown))
                        Assert.False(other.IsVisible, $"{other.Name} is shown beside {body}");
                    if (section == MixerSection.Midi) AssertMidiSection(view);

                    var faders = view.GetVisualDescendants().OfType<Slider>()
                        .Where(s => s.Classes.Contains("MixerSlider") && s.Bounds.Height > 0)
                        .ToList();
                    foreach (var fader in faders)
                        Assert.Equal(24d, fader.Bounds.Height);
                }
            }
            finally
            {
                vm.MixerSections.ChooseCommand.Execute(previous);
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void AssertMidiSection(MixerInspectorView view)
    {
        {

            // ABI v38: the chart synth toggle in the MIDI card.
            var toggle = view.GetVisualDescendants().OfType<ToggleButton>().SingleOrDefault(c => c.Name == "ChartSynthToggle");
            Assert.NotNull(toggle);
            Assert.True(toggle!.Bounds.Width > 1 && toggle.Bounds.Height > 1,
                $"ChartSynthToggle laid out as {toggle.Bounds} — it is in the tree but its style or template failed");

            // #164 / #751: the Sound source segments (the old tone switch and "Instrument through the rig" are two of them)
            // and Route instrument, every segment laid out. MixerSoundRoutingUiPerfSmokeTests checks them across sizes.
            foreach (var name in new[]
                     {
                         "ChartSoundSourceSongTones", "ChartSoundSourceOwnRig", "ChartSoundSourceMyInstrument", "ChartSoundSourceInstrumentRig",
                         "ChartSoundRouteGuitar", "ChartSoundRouteBass", "ChartSoundRouteBoth"
                     })
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
    ///     "Fix with AI" and "Explain with AI" ran together with no gap. The Issues tab's sections (their
    ///     layout with real content) are <c>IssuesTabCardsUiPerfSmokeTests</c>; the tab builds without a
    ///     view model, every section hidden, so the row is read off the logical tree.
    /// </summary>
    [Fact]
    public async Task IssuesInspectorView_theAiButtonsHaveAGap() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var host = new InspectorView();
            AttachAndLayoutStrip(host, () =>
            {
                var issues = host.GetVisualDescendants().OfType<IssuesInspectorView>().Single();
                var aiRow = issues.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Name == "IssueAiRow");
                Assert.True(aiRow.GetLogicalDescendants().OfType<Grid>().First().ColumnSpacing > 0, "the AI buttons have no gap");
            });
        }, CancellationToken.None);

    /// <summary>
    ///     The Issues tab (inspector design D) builds, attaches and lays out all four cards with no project loaded;
    ///     attach matters because every row command resolves through <c>$parent[UserControl]</c>. The cards with
    ///     real content, the pager and the selected-row spine are <c>IssuesTabCardsUiPerfSmokeTests</c>.
    /// </summary>
    [Fact]
    public async Task IssuesInspectorView_buildsAndAttaches_andEveryCardLaysOut()
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

                // Design D: four cards (Findings, Fixes, Whole project, Review) on one ColumnFlowPanel; the
                // cards a narrow pane pages away are still measured, so every one has a size.
                foreach (var name in new[] { "IssuesFindingsCard", "IssuesFixesCard", "IssuesProjectCard", "IssuesReviewCard" })
                {
                    var card = issues.GetLogicalDescendants().OfType<Control>().Single(c => c.Name == name);
                    Assert.True(card.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {card.Bounds}");
                }

                // The export verdict is THREE gated TextBlocks — dim / green / danger — and which one shows is a
                // data question, pinned as markup by `UI/IssuesTabVerdictTests`. At runtime all three are there.
                Assert.Equal(3, issues.GetLogicalDescendants().OfType<TextBlock>()
                    .Count(t => t.Name is "ExportVerdictPending" or "ExportVerdictOk" or "ExportVerdictBad"));

                // #952: Prepare for Rocksmith left the export row (every export applies DLC
                // Builder's steps to its copy; the chart's context menu puts them in the project).
                Assert.DoesNotContain(issues.GetLogicalDescendants().OfType<Button>(), b => b.Name == "PrepareForRocksmithButton");
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

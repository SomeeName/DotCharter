using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Settings;
using DotCharter.UI.Views.Settings;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Builds every settings tab for real, ATTACHES it to a window and runs a
///     layout pass — then asserts the row grammar actually produced rows.
///     <para>
///         A restructured settings tab is the exact shape of change that builds
///         clean and fails at runtime: the AXAML compiler proves the markup
///         parses, but says nothing about a style selector that matches nothing,
///         a <c>{DynamicResource}</c> geometry that does not resolve, or a row
///         whose control column collapsed to zero. Those only show up once
///         something measures.
///     </para>
///     <para>
///         The old tabs are laid out with NO DataContext on purpose. Every binding
///         then produces no value and each property keeps its default, which is
///         the harshest layout case (empty labels, unset visibility) and needs
///         no service graph. The two migrated tabs bind their Presentation FAKES
///         (the same variants the gallery renders), because a contract-typed
///         view with no context has nothing to lay out. What is under test is
///         the LAYOUT, not the data.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///         session, which cannot share a testhost with the gate and is excluded from the
///         default gate. Run it by hand after touching any settings view.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SettingsConstructionUiPerfSmokeTests
{
    private const double TabWidth = 960;
    private const double TabHeight = 1400;

    /// <summary>Half the point of the rework: on any tab, every row's control
    /// ends on ONE right edge. Measured rather than reviewed by eye, because a
    /// single row that forgets the shared `*,300` columns is invisible in a
    /// diff and obvious in a screenshot.</summary>
    private const double EdgeTolerance = 1.0;

    private static readonly string[] AllTabs =
    [
        nameof(GeneralSettingsView),
        nameof(EditorSettingsView),
        nameof(PlaySettingsView),
        nameof(GraphicsSettingsView),
        nameof(AudioSettingsView),
        nameof(FilesSettingsView),
        nameof(AppearanceSettingsView),
        nameof(KeybindsSettingsView),
        nameof(StemModelsSettingsView),
        nameof(LyricModelsSettingsView),
        nameof(UpdatesSettingsView),
        nameof(Tone3000SettingsView),
        nameof(AiSettingsView)
    ];

    public static TheoryData<string> TabNames => [.. AllTabs];

    private static Control CreateTab(string name) => name switch
    {
        nameof(GeneralSettingsView) => new GeneralSettingsView { DataContext = FakeGeneralSettingsScreen.Default() },
        nameof(EditorSettingsView) => new EditorSettingsView { DataContext = FakeEditorSettingsScreen.Default() },
        nameof(PlaySettingsView) => new PlaySettingsView { DataContext = FakePlaySettingsScreen.Full() },
        nameof(FilesSettingsView) => new FilesSettingsView { DataContext = FakeFilesSettingsScreen.LongText() },
        nameof(GraphicsSettingsView) => new GraphicsSettingsView { DataContext = FakeGraphicsSettingsScreen.Full() },
        nameof(AudioSettingsView) => new AudioSettingsView { DataContext = FakeAudioSettingsScreen.Full() },
        nameof(AppearanceSettingsView) => new AppearanceSettingsView { DataContext = FakeAppearanceSettingsScreen.Full() },
        nameof(KeybindsSettingsView) => new KeybindsSettingsView { DataContext = FakeKeybindsSettingsScreen.Full() },
        nameof(StemModelsSettingsView) => new StemModelsSettingsView { DataContext = FakeStemModelsSettingsScreen.Full() },
        nameof(LyricModelsSettingsView) => new LyricModelsSettingsView { DataContext = FakeLyricModelsSettingsScreen.Full() },
        nameof(UpdatesSettingsView) => new UpdatesSettingsView { DataContext = FakeUpdatesSettingsScreen.UpdateAvailable() },
        nameof(Tone3000SettingsView) => new Tone3000SettingsView { DataContext = FakeTone3000SettingsScreen.SignedIn() },
        nameof(AiSettingsView) => new AiSettingsView { DataContext = FakeAiSettingsScreen.OpenAiCompatible() },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown settings tab")
    };

    private sealed class NoSetup : DotCharter.Services.Infrastructure.Shell.IFirstRunSetupPrompt
    {
        public void Open() { }
    }

    /// <summary>
    ///     The General tab's two combo boxes show what the config SAYS, and attaching the view
    ///     writes nothing back: a charter's "Chart editor" start and an English UI must survive
    ///     the tab being opened, or the next Save of any other row switches them silently.
    /// </summary>
    [Fact]
    public async Task GeneralTab_ShowsThePersistedChoices_AndOpeningItChangesNothing()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var config = new FakeConfigurationService();
            config.AppConfig.StartupScreen = StartupScreen.Editor;
            config.AppConfig.LanguageCulture = "en-US";
            var vm = new DotCharter.Presentation.Screens.Settings.GeneralSettingsViewModel(
                config, DotCharter.Localization.LocalizationService.Instance, new NoSetup());
            vm.Load();
            var language = vm.SelectedLanguageIndex;
            Assert.Equal(1, vm.SelectedStartupScreenIndex);
            Assert.True(language > 0, "en-US must not be the 'System language' row");

            var tab = new GeneralSettingsView { DataContext = vm };
            WithLaidOutTab(tab, () =>
            {
                // The settings shell reloads every tab each time the page opens, with the view attached.
                vm.Load();
                tab.UpdateLayout();
                Assert.Equal(1, vm.SelectedStartupScreenIndex);
                Assert.Equal(language, vm.SelectedLanguageIndex);
                Assert.Empty(vm.UnsavedSettings);
                var combos = tab.GetLogicalDescendants().OfType<ComboBox>().ToList();
                Assert.Contains(combos, c => c.SelectedIndex == 1);
                Assert.Contains(combos, c => c.SelectedIndex == language);
            });
        }, CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(TabNames))]
    public async Task SettingsTab_builds_attaches_and_lays_out(string tabName)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tab = CreateTab(tabName);
            WithLaidOutTab(tab, () =>
            {
                Assert.True(tab.Bounds.Width > 1 && tab.Bounds.Height > 1,
                    $"{tabName} laid out as {tab.Bounds} — it rendered nothing");

                var groups = VisibleGroups(tab);
                Assert.True(groups.Count > 0, $"{tabName} produced no Border.SettingsGroup");
                foreach (var group in groups)
                {
                    Assert.True(group.Bounds.Height > 1,
                        $"a SettingsGroup on {tabName} laid out {group.Bounds} — its content measured nothing");
                }

                // Group badges pull their glyph through {DynamicResource IconX}
                // (never StaticResource — the tabs are also constructed without
                // an Application). DynamicResource fails SILENTLY, so a typo'd
                // key is an empty badge nobody notices; this is the only place
                // it shows up.
                var badges = tab.GetLogicalDescendants().OfType<PathIcon>()
                    .Where(p => p.Classes.Contains("GroupIcon"))
                    .ToList();
                // Graphite hides the badges (the section is a title, not a card),
                // but any a tab still declares must resolve, for the day they return.
                foreach (var badge in badges)
                    Assert.NotNull(badge.Data);
            });
        }, CancellationToken.None);
    }

    /// <summary>Every visible row must reach the shared minimum height and end on
    /// the group's right edge. Catches a row that kept an old `200,*` column set
    /// (its control would stop short) or lost the FormRow style entirely (it
    /// would collapse under 30 px).</summary>
    [Theory]
    [MemberData(nameof(TabNames))]
    public async Task SettingsRows_ShareOneRhythmAndOneRightEdge(string tabName)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tab = CreateTab(tabName);
            WithLaidOutTab(tab, () =>
            {
                var rows = tab.GetLogicalDescendants().OfType<Grid>()
                    .Where(g => g.Classes.Contains("FormRow"))
                    .Where(g => g.IsEffectivelyVisible)
                    .ToList();

                Assert.True(rows.Count > 0, $"{tabName} produced no Grid.FormRow");

                foreach (var row in rows)
                {
                    Assert.True(row.Bounds.Height >= 30,
                        $"a row on {tabName} laid out {row.Bounds.Height:0.#} px tall — below the shared FormRow rhythm");

                    var control = row.Children.FirstOrDefault(c => Grid.GetColumn(c) == 1 && c.IsEffectivelyVisible);
                    if (control == null) continue; // full-width row (checkbox clusters)

                    var gap = row.Bounds.Width - (control.Bounds.X + control.Bounds.Width);
                    Assert.True(Math.Abs(gap) <= EdgeTolerance,
                        $"a row on {tabName} left {gap:0.#} px between its control and the row's right edge — " +
                        "the tab's controls no longer share one right edge");
                }

                // …and the column itself is one width across the whole tab.
                // Right-alignment alone would pass the check above even from a
                // row that kept the old `200,*` split, so measure the column.
                // Keybind rows are excluded on purpose: they live in a
                // UniformGrid two-up and carry their own narrower chord column.
                var controlColumnWidths = rows
                    .Where(r => r.ColumnDefinitions.Count == 2 && r.Parent is not UniformGrid)
                    .Select(r => Math.Round(r.ColumnDefinitions[1].ActualWidth))
                    .Distinct()
                    .ToList();
                Assert.True(controlColumnWidths.Count <= 1,
                    $"{tabName} lays its rows out over {controlColumnWidths.Count} different control-column widths " +
                    $"({string.Join(", ", controlColumnWidths)}) — they must all share one");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Nothing a row puts in its CONTROL COLUMN is ARRANGED past the row's
    ///     right edge. That column is a fixed 300 px (184 on the keybind rows)
    ///     and the shell caps the whole content strip at 1040 px, so a control
    ///     here can never be given more room by a wider window — which is what
    ///     makes "it looked fine when I swept the tab" no evidence at all.
    ///     <para>
    ///         The classic offender is a horizontal <c>StackPanel</c> of buttons
    ///         or a label-plus-value pair: it arranges each child at that child's
    ///         own desired width starting from x=0 and clips NOTHING, while its
    ///         own <c>DesiredSize</c> is clamped to the slot it was given. So the
    ///         panel reports a perfectly correct rectangle and draws outside it,
    ///         over the group card's padding or the cell next door — see
    ///         <c>docs/agents/ui/layout-traps.md</c>. Invisible in English and
    ///         routine in German, which runs ~40 % longer. The Updates tab's
    ///         "Check for updates" row shipped it: with a pending update its two
    ///         buttons needed 327 px of a 320 px column in ENGLISH.
    ///     </para>
    ///     <para>
    ///         ARRANGED position is the right question, not desired width.
    ///         Desired width is clamped to whatever constraint the control was
    ///         measured under, so after a normal layout pass a row that is
    ///         slicing its content reports the two numbers as equal — the first
    ///         draft asserted exactly that and passed against a deliberately
    ///         87-character readout. Measuring against INFINITY instead is
    ///         equally wrong in the other direction: it flags a <c>WrapPanel</c>,
    ///         which is the fix the layout guide prescribes, because an
    ///         unconstrained wrap panel puts everything on one line.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task NothingInAControlColumn_IsArrangedPastTheRowsRightEdge()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var offenders = new List<string>();
            var checkedPanels = 0;

            // One Fact over all eight tabs rather than a Theory per tab: two of
            // them legitimately put a single control in every control column
            // (Keybinds' capture boxes, StemModels' rows), so a per-tab
            // "something was measured" guard would need an exemption list for
            // tabs that cannot have this fault at all. The vacuity check belongs
            // at the level where it is really true — across the page.
            foreach (var tabName in AllTabs)
            {
                var tab = CreateTab(tabName);
                WithLaidOutTab(tab, () =>
                {
                    foreach (var row in tab.GetLogicalDescendants().OfType<Grid>()
                                 .Where(g => g.Classes.Contains("FormRow"))
                                 .Where(g => g.IsEffectivelyVisible)
                                 .Where(g => g.ColumnDefinitions.Count == 2))
                    {
                        if (row.Bounds.Width <= 0) continue;
                        var control = row.Children
                            .FirstOrDefault(c => Grid.GetColumn(c) == 1 && c.IsEffectivelyVisible);
                        // A single control cannot overhang its own cell — it is
                        // arranged INTO it. Only a panel lays its children out
                        // itself and declines to clip them.
                        if (control is not Panel panel) continue;

                        checkedPanels++;
                        foreach (var child in panel.Children.Where(c => c.IsEffectivelyVisible))
                        {
                            // The child's own right edge, in the ROW's
                            // coordinates — that is where the group card ends.
                            var edge = child.TranslatePoint(new Point(child.Bounds.Width, 0), row);
                            if (edge is not { } point || point.X <= row.Bounds.Width + EdgeTolerance) continue;
                            offenders.Add(
                                $"{tabName}: '{LabelOf(row)}' arranges a {child.GetType().Name} out to " +
                                $"{point.X:0.#} px in a {row.Bounds.Width:0.#} px row");
                        }
                    }
                });
            }

            Assert.True(checkedPanels >= 10,
                $"only {checkedPanels} control-column panels were measured across all eight tabs — " +
                "this check has gone vacuous");
            Assert.True(offenders.Count == 0,
                $"settings rows draw outside their control column (UI culture: " +
                $"{HeadlessSessionFixture.SweptCulture}): " + string.Join("; ", offenders) +
                ". The column is fixed, so a wider window cannot rescue it — a horizontal StackPanel " +
                "here must become a WrapPanel (ItemSpacing/LineSpacing) or a real Grid slot.");
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The Codex CLI's two model presets, over the REAL view model (not a fake),
    ///     so the whole path — provider kind, the search partial's visibility flag,
    ///     the binding, the buttons — is the thing measured. For Codex the panel and
    ///     both buttons must measure non-zero and stay inside the model row; for
    ///     Claude Code the panel must be gone, which is what makes the Codex half
    ///     more than "the panel exists".
    /// </summary>
    [Theory]
    [InlineData(DotCharter.Tone.Ai.AiProviderKind.CodexCli, true)]
    [InlineData(DotCharter.Tone.Ai.AiProviderKind.ClaudeCli, false)]
    public async Task AiTab_ShowsTheCodexModelPresets_ForCodexAlone(DotCharter.Tone.Ai.AiProviderKind kind, bool shown)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = DotCharter.Tests.Presentation.Settings.AiTabRig.Create(
                new FakeConfigurationService(), new DotCharter.Tone.Ai.InMemoryAiSecretStore());
            vm.ProviderKind = kind;
            var tab = new AiSettingsView { DataContext = vm };
            WithLaidOutTab(tab, () =>
            {
                var presets = tab.GetLogicalDescendants().OfType<WrapPanel>().Single(p => p.Name == "AiCodexPresets");
                Assert.Equal(shown, presets.IsEffectivelyVisible);
                if (!shown) return;

                Assert.True(presets.Bounds.Width > 1 && presets.Bounds.Height > 1,
                    $"the Codex presets laid out as {presets.Bounds} — they rendered nothing");
                var row = presets.GetLogicalAncestors().OfType<Grid>().First(g => g.Classes.Contains("FormRow"));
                var buttons = presets.Children.OfType<Button>().ToList();
                Assert.Equal(2, buttons.Count);
                foreach (var button in buttons)
                {
                    Assert.True(button.Bounds.Width > 1 && button.Bounds.Height > 1,
                        $"a Codex preset button laid out as {button.Bounds}");
                    Assert.False(string.IsNullOrWhiteSpace(button.Content as string), "a Codex preset button has no label");
                    var edge = button.TranslatePoint(new Point(button.Bounds.Width, 0), row);
                    Assert.True(edge is { } point && point.X <= row.Bounds.Width + EdgeTolerance,
                        $"a Codex preset button is arranged out to {edge} in a {row.Bounds.Width:0.#} px row");
                }

                buttons[0].Command!.Execute(null);
                Assert.Equal("gpt-6-sol", vm.ModelId);
                buttons[1].Command!.Execute(null);
                Assert.Equal("gpt-6-luna", vm.ModelId);

                // Codex's own note, and the path row in Codex's words.
                var note = tab.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "AiCodexCliNote");
                Assert.True(note.IsEffectivelyVisible && note.Bounds.Width > 1 && note.Bounds.Height > 1,
                    $"the Codex CLI note laid out as {note.Bounds} (visible={note.IsEffectivelyVisible})");
                var pathBox = tab.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "AiCliPathBox");
                Assert.True(pathBox.IsEffectivelyVisible, "the Codex path row is hidden");
                Assert.Equal(vm.CliPathLabel, Avalonia.Automation.AutomationProperties.GetName(pathBox));
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Claude Code's three model presets, over the REAL view model: for Claude
    ///     Code the panel and its Haiku, Opus and Sonnet buttons measure non-zero,
    ///     stay inside the model row and fill in those ids in that order; for Codex
    ///     the panel is gone.
    /// </summary>
    [Theory]
    [InlineData(DotCharter.Tone.Ai.AiProviderKind.ClaudeCli, true)]
    [InlineData(DotCharter.Tone.Ai.AiProviderKind.CodexCli, false)]
    public async Task AiTab_ShowsTheClaudeModelPresets_ForClaudeCodeAlone(DotCharter.Tone.Ai.AiProviderKind kind, bool shown)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = DotCharter.Tests.Presentation.Settings.AiTabRig.Create(
                new FakeConfigurationService(), new DotCharter.Tone.Ai.InMemoryAiSecretStore());
            vm.ProviderKind = kind;
            var tab = new AiSettingsView { DataContext = vm };
            WithLaidOutTab(tab, () =>
            {
                var presets = tab.GetLogicalDescendants().OfType<WrapPanel>().Single(p => p.Name == "AiClaudePresets");
                Assert.Equal(shown, presets.IsEffectivelyVisible);
                if (!shown) return;

                Assert.True(presets.Bounds.Width > 1 && presets.Bounds.Height > 1,
                    $"the Claude presets laid out as {presets.Bounds} — they rendered nothing");
                var row = presets.GetLogicalAncestors().OfType<Grid>().First(g => g.Classes.Contains("FormRow"));
                var buttons = presets.Children.OfType<Button>().ToList();
                Assert.Equal(3, buttons.Count);
                foreach (var button in buttons)
                {
                    Assert.True(button.Bounds.Width > 1 && button.Bounds.Height > 1,
                        $"a Claude preset button laid out as {button.Bounds}");
                    Assert.False(string.IsNullOrWhiteSpace(button.Content as string), "a Claude preset button has no label");
                    var edge = button.TranslatePoint(new Point(button.Bounds.Width, 0), row);
                    Assert.True(edge is { } point && point.X <= row.Bounds.Width + EdgeTolerance,
                        $"a Claude preset button is arranged out to {edge} in a {row.Bounds.Width:0.#} px row");
                }

                buttons[0].Command!.Execute(null);
                Assert.Equal("claude-haiku-5-5", vm.ModelId);
                buttons[1].Command!.Execute(null);
                Assert.Equal("claude-opus-5-5", vm.ModelId);
                buttons[2].Command!.Execute(null);
                Assert.Equal("claude-sonnet-5-5", vm.ModelId);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Settings ▸ AI ▸ Reasoning (#603) over the REAL view model in every shipped culture: the
    ///     row, its picker and its description measure non-zero inside the row, in that culture's
    ///     words; on a Claude model older than the effort parameter the picker is DISABLED and the
    ///     description is the reason, still readable (not dimmed with it).
    /// </summary>
    [Fact]
    public async Task AiTab_TheReasoningRow_LaysOut_AndSaysWhyWhenDisabled_InEveryShippedCulture()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var localization = DotCharter.Localization.LocalizationService.Instance;
            var restore = localization.ConfiguredCulture;
            var reasons = new List<string>();
            try
            {
                foreach (var culture in new[] { "en-US", "de-DE", "ja-JP" })
                {
                    localization.SetConfiguredCulture(culture);
                    var config = new FakeConfigurationService();
                    config.AppConfig.Ai = new DotCharter.Tone.Ai.AiProviderSettings(
                        DotCharter.Tone.Ai.AiProviderKind.Claude, "claude-opus-5-5", null,
                        Effort: DotCharter.Tone.Ai.AiReasoningEffort.High);
                    var vm = new DotCharter.Presentation.Screens.Settings.AiSettingsViewModel(config, localization,
                        new DotCharter.Presentation.Screens.Settings.MatchModelViewModel(localization),
                        new DotCharter.Tone.Ai.InMemoryAiSecretStore(),
                        new DotCharter.Tone.Ai.ToneDesignModelFactory(new DotCharter.Tone.Ai.InMemoryAiSecretStore()));
                    vm.Load();
                    var tab = new AiSettingsView { DataContext = vm };
                    WithLaidOutTab(tab, () =>
                    {
                        var row = tab.GetLogicalDescendants().OfType<Grid>().Single(g => g.Name == "AiEffortRow");
                        var picker = tab.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "AiEffortPicker");
                        var desc = tab.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "AiEffortDesc");
                        foreach (var part in new Control[] { row, picker, desc })
                        {
                            Assert.True(part.IsEffectivelyVisible && part.Bounds is { Width: > 1, Height: > 1 },
                                $"{culture}: {part.Name} laid out as {part.Bounds} (visible={part.IsEffectivelyVisible})");
                        }

                        var edge = picker.TranslatePoint(new Point(picker.Bounds.Width, 0), row);
                        Assert.True(edge is { } point && point.X <= row.Bounds.Width + EdgeTolerance,
                            $"{culture}: the reasoning picker is arranged out to {edge} in a {row.Bounds.Width:0.#} px row");
                        Assert.True(picker.IsEnabled, $"{culture}: the picker is disabled for Claude Opus 5.5");
                        Assert.Equal(localization["Ai_Effort_High"], (picker.SelectedItem as DotCharter.Presentation.Contracts.Settings.IAiEffortOption)?.Label);
                        Assert.Equal(localization["Ai_EffortDesc"], desc.Text);
                        CaptureTab(tab, $"ai-settings-reasoning-{culture}");

                        vm.ModelId = "claude-haiku-4-5";
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                        tab.UpdateLayout();
                        Assert.False(picker.IsEnabled, $"{culture}: the picker stays enabled for a model older than effort");
                        Assert.Equal(localization["Ai_Effort_Unsupported_ClaudeModel"], desc.Text);
                        Assert.True(desc.IsEffectivelyEnabled, $"{culture}: the reason is dimmed with the picker");
                        Assert.True(desc.Bounds is { Width: > 1, Height: > 1 }, $"{culture}: the reason laid out as {desc.Bounds}");
                        CaptureTab(tab, $"ai-settings-reasoning-disabled-{culture}");
                        reasons.Add(desc.Text!);
                    });
                }
            }
            finally
            {
                localization.SetConfiguredCulture(restore);
                System.Globalization.CultureInfo.CurrentUICulture = localization.Culture;
            }

            Assert.Equal(3, reasons.Distinct(StringComparer.Ordinal).Count());
        }, CancellationToken.None);
    }

    /// <summary>The row's own label, so an overflow report names the setting
    /// rather than a coordinate.</summary>
    private static string LabelOf(Grid row) =>
        row.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text))?.Text ?? "(unlabelled row)";

    /// <summary>
    ///     The Fire TYPE LADDER really reached the eight tabs. The palette and
    ///     the default typeface went global before this conversion, so the type
    ///     hierarchy is the whole of what separated a settings tab from a Fire
    ///     screen — and a style that fails to apply is the quietest failure in
    ///     the theme: the text still renders, in the inherited face, at the
    ///     inherited size, with no warning and no exception.
    ///     <para>
    ///         The expectations are LITERALS on purpose. Deriving them from the
    ///         same token the style reads would move both sides together and
    ///         pass through the regression this exists to catch.
    ///     </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TabNames))]
    public async Task SettingsTab_WearsTheGraphiteTypeLadder(string tabName)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tab = CreateTab(tabName);
            WithLaidOutTab(tab, () =>
            {
                // A tab's own intro header (Border.Intro) leads at a larger size on purpose.
                var titles = tab.GetLogicalDescendants().OfType<TextBlock>()
                    .Where(t => t.Classes.Contains("GroupTitle")
                                && !t.GetLogicalAncestors().OfType<Border>().Any(b => b.Classes.Contains("Intro")))
                    .ToList();
                Assert.True(titles.Count > 0, $"{tabName} produced no TextBlock.GroupTitle");
                foreach (var title in titles)
                {
                    Assert.True(title.FontFamily.Name == "Atkinson Hyperlegible",
                        $"a group title on {tabName} renders in '{title.FontFamily.Name}' — Graphite's " +
                        "reading face did not apply, so the tab is still on the old grammar's type");
                    Assert.Equal(FireTypeLadder.Size("FireSizeCardTitle"), title.FontSize);
                }

                foreach (var row in tab.GetLogicalDescendants().OfType<TextBlock>()
                             .Where(t => t.Classes.Contains("SettingTitle")))
                {
                    Assert.True(row.FontFamily.Name == "Atkinson Hyperlegible",
                        $"a row title on {tabName} renders in '{row.FontFamily.Name}'");
                    Assert.Equal(FireTypeLadder.Size("FireSizeBase"), row.FontSize);
                }
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     GRAPHITE (0.62): a group is a SECTION, not a card — it opens with its
    ///     title alone. The Fire mockups' mono kicker and icon badge stay in the
    ///     markup (the search walk and ~40 groups are untouched) but must not
    ///     render: a caps eyebrow over every section is the template look the
    ///     redesign removed.
    /// </summary>
    [Theory]
    [MemberData(nameof(TabNames))]
    public async Task EveryGroup_OpensWithItsTitle_AndNoKickerOrBadge(string tabName)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tab = CreateTab(tabName);
            WithLaidOutTab(tab, () =>
            {
                var groups = VisibleGroups(tab);
                Assert.True(groups.Count > 0, $"{tabName} produced no Border.SettingsGroup");

                foreach (var group in groups)
                {
                    var title = group.GetLogicalDescendants().OfType<TextBlock>()
                        .FirstOrDefault(t => t.Classes.Contains("GroupTitle"));
                    Assert.True(title is { Bounds.Height: > 1 } && !string.IsNullOrWhiteSpace(title.Text),
                        $"a group on {tabName} has no laid-out, non-empty GroupTitle");
                    Assert.All(group.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("GroupKicker")),
                        k => Assert.False(k.IsEffectivelyVisible, $"a group kicker on {tabName} still renders ('{k.Text}')"));
                    Assert.All(group.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("GroupIconBox")),
                        b => Assert.False(b.IsEffectivelyVisible, $"a group icon badge on {tabName} still renders"));
                }
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Every boolean on the page is the SHARED Fire 44 × 24 pill, reached
    ///     through the <c>FireToggleSwitch</c> ControlTheme. A
    ///     <c>TemplatedControl</c> takes its template from a ControlTheme and
    ///     nothing else, and a <c>Theme</c> setter that does not resolve leaves
    ///     Fluent's switch in place — same size, same behaviour, different
    ///     product. Only the laid-out TEMPLATE can tell them apart, so this
    ///     measures the track.
    /// </summary>
    [Fact]
    public async Task EveryBooleanOnTheSettingsPage_IsTheSharedFireSwitch()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var checkedSwitches = 0;

            foreach (var tabName in AllTabs)
            {
                var tab = CreateTab(tabName);
                WithLaidOutTab(tab, () =>
                {
                    // Visible only: a control inside a collapsed Advanced
                    // section never reaches MeasureCore, so it has applied no
                    // template and NO theme could have shown up on it. Asking
                    // about its template would fail whatever the theme said.
                    foreach (var toggle in tab.GetLogicalDescendants().OfType<ToggleSwitch>()
                                 .Where(t => t.IsEffectivelyVisible))
                    {
                        var track = toggle.GetVisualDescendants().OfType<Border>()
                            .FirstOrDefault(b => b.Name == "PART_Track");
                        Assert.True(track is not null,
                            $"a ToggleSwitch on {tabName} has no PART_Track — the FireToggleSwitch theme " +
                            "did not apply and the row still shows Fluent's switch");
                        Assert.Equal(44, track!.Width);
                        Assert.Equal(24, track.Height);
                        checkedSwitches++;
                    }
                });
            }

            Assert.True(checkedSwitches >= 20,
                $"only {checkedSwitches} switches were checked — the settings page has far more than that");
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The Advanced sections are a ToggleButton + an inline IsVisible-bound
    ///     panel, NOT an Expander, precisely so their rows stay in the tree for
    ///     the cross-tab search. This proves both halves at runtime: the rows
    ///     exist while collapsed (search can see them) and they lay out once the
    ///     header is checked (the user can reach them).
    /// </summary>
    [Theory]
    [InlineData(nameof(GeneralSettingsView))]
    [InlineData(nameof(GraphicsSettingsView))]
    [InlineData(nameof(AudioSettingsView))]
    public async Task AdvancedSections_KeepTheirRowsInTheTree_AndLayOutWhenOpened(string tabName)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tab = CreateTab(tabName);
            WithLaidOutTab(tab, () =>
            {
                var header = tab.GetLogicalDescendants().OfType<ToggleButton>()
                    .Single(t => t.Classes.Contains("AdvancedHeader"));

                var allRows = tab.GetLogicalDescendants().OfType<Grid>()
                    .Count(g => g.Classes.Contains("FormRow"));
                var openRows = tab.GetLogicalDescendants().OfType<Grid>()
                    .Count(g => g.Classes.Contains("FormRow") && g.IsEffectivelyVisible);
                Assert.True(allRows > openRows,
                    $"{tabName}'s Advanced section contributed no collapsed rows — is it an Expander again?");

                header.IsChecked = true;
                tab.Measure(new Size(TabWidth, TabHeight));
                tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));

                var nowVisible = tab.GetLogicalDescendants().OfType<Grid>()
                    .Count(g => g.Classes.Contains("FormRow") && g.IsEffectivelyVisible);
                Assert.True(nowVisible > openRows,
                    $"opening {tabName}'s Advanced section revealed no additional rows");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     String order is a reading preference: one picker on Editor (the 2D
    ///     timeline + editor preview) and one on Play (the game's highway), each
    ///     with both orders and sized, and none left on Graphics.
    /// </summary>
    [Theory]
    [InlineData(nameof(EditorSettingsView), "EditorStringOrder")]
    [InlineData(nameof(PlaySettingsView), "PlayStringOrder")]
    public async Task TheStringOrderPicker_LaysOutOnItsTab(string tabName, string pickerName)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tab = CreateTab(tabName);
            WithLaidOutTab(tab, () =>
            {
                var picker = tab.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == pickerName);
                Assert.Equal(2, picker.ItemCount);
                Assert.True(picker.IsEffectivelyVisible && picker.Bounds.Width > 1 && picker.Bounds.Height > 1,
                    $"{pickerName} laid out {picker.Bounds}");
                CaptureTab(tab, $"string-order-{tabName}");
            });
            var graphics = CreateTab(nameof(GraphicsSettingsView));
            WithLaidOutTab(graphics, () =>
            {
                var titles = graphics.GetLogicalDescendants().OfType<TextBlock>()
                    .Where(t => t.Classes.Contains("SettingTitle")).Select(t => t.Text ?? "").ToList();
                Assert.NotEmpty(titles);
                Assert.DoesNotContain(titles, t => t.Contains("Inverted", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("String order", StringComparison.OrdinalIgnoreCase));
                CaptureTab(graphics, "string-order-GraphicsSettingsView");
            });
        }, CancellationToken.None);
    }

    private static List<Border> VisibleGroups(Control tab) =>
        tab.GetLogicalDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("SettingsGroup") && b.IsEffectivelyVisible)
            .ToList();

    /// <summary>Attaching is the point — a control only reaches
    /// <c>OnAttachedToLogicalTree</c>, and therefore its buttons' CanExecute and
    /// its templated children, once it has a rooted parent and is measured. The
    /// layout pass is driven through the VIEW, not the window: measuring a
    /// headless Window directly reports "Invalid size returned for Measure".</summary>
    /// <summary>A review capture of the tab's window (only with DOTCHARTER_UI_SWEEP_DUMP=1).</summary>
    private static void CaptureTab(Control tab, string name)
    {
        var window = (Window)TopLevel.GetTopLevel(tab)!;
        NarrowWidthLayout.Settle(window);
        NarrowWidthLayout.Capture(window, name);
    }

    private static void WithLaidOutTab(Control tab, Action assert)
    {
        var window = new Window { Width = TabWidth, Height = 900, Content = tab };
        window.Show();
        try
        {
            tab.Measure(new Size(TabWidth, TabHeight));
            tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}

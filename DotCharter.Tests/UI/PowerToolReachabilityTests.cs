using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The 0.22 power tools shipped as five tested services with ZERO UI references —
///     built, correct, and reachable from nowhere. Making a command reachable in this
///     app takes five files (see docs/agents/MAP.md, "Adding a menu item / command"),
///     and missing any ONE of them puts the feature straight back into the dark:
///     <list type="number">
///         <item>the <c>MenuItem</c> in its menu's file under <c>Views/Shell/Menus/</c> (read with the bar through <c>MainMenuSource</c>),</item>
///         <item>the palette entry in <c>CommandCatalog.cs</c>,</item>
///         <item>the DI registration in <c>App.axaml.cs</c>,</item>
///         <item>the dialog property in <c>AppDialogService.cs</c> (which also orders Esc),</item>
///         <item>the <c>RegisterLazy</c> in <c>DialogContainerView.axaml.cs</c> — without it a
///               dialog view model can be "open" while its view was never built, so the
///               dialog is invisible,</item>
///     </list>
///     plus the <c>Menu_*</c> key in BOTH resx files. These tests read the REAL source
///     files and fail the moment a hop is dropped.
/// </summary>
public sealed class PowerToolReachabilityTests
{
    /// <summary>Every palette id the power tools must expose.</summary>
    public static TheoryData<string> PaletteIds =>
    [
        "file.importMidi",
        "tools.generateLevels",
        "tools.propagatePhrase",
        "tools.refret",
        "tools.generateHandShapesArrangement",
        "tools.chartDiff",
        "tools.batchLint",
        "edit.patternBuilder",
        "tempo.alignBeatGrid",
        "tempo.alignToRecording",
        "file.exportDcSong"
    ];

    /// <summary>
    ///     A power tool whose dialog moved onto the dialog host (target-architecture.md
    ///     § 3.2) has DIFFERENT hops, and each one is just as able to go dark: the
    ///     menu binds a shell command, the command opens the contract through
    ///     <c>IDialogs</c>, the catalog maps the contract to its view (no catalog
    ///     line ⇒ the open throws), and the dialogs module registers its view model.
    /// </summary>
    [Theory]
    [InlineData("OpenLevelGeneration", "ILevelGenerationScreen", "LevelGenerationViewModel", "Menu_GenerateLevels")]
    [InlineData("OpenBatchLint", "IBatchLintScreen", "BatchLintViewModel", "Menu_BatchLint")]
    [InlineData("OpenChartDiff", "IChartDiffScreen", "ChartDiffViewModel", "Menu_ChartDiff")]
    [InlineData("OpenPatternBuilder", "IPatternBuilderScreen", "PatternBuilderViewModel", "Menu_PatternBuilder")]
    [InlineData("OpenAlignBeatGridDialog", "IAlignBeatGridScreen", "AlignBeatGridViewModel", "Menu_AlignBeatGrid")]
    [InlineData("OpenAlignToRecordingDialog", "IAlignToRecordingScreen", "AlignToRecordingViewModel", "Menu_AlignToRecording")]
    [InlineData("OpenDcSongExport", "IDcSongExportScreen", "DcSongExportViewModel", "Menu_ExportDcSong")]
    public void Each_hosted_dialog_power_tool_is_reachable_end_to_end(string shellVerb, string contract,
        string viewModel, string menuKey)
    {
        var menu = MainMenuSource.ReadAllText();
        Assert.Contains($"Loc {menuKey}", menu);
        Assert.Contains($"{shellVerb}Command", menu);

        var shell = Read("DotCharter.UI", "ViewModels", "Shell", "ShellDialogsViewModel.cs");
        Assert.Contains($"private void {shellVerb}()", shell);
        Assert.Contains($"ShowAsync<{contract},", shell);

        var catalog = Read("DotCharter.UI", "Views", "Shell", "ScreenCatalog.cs");
        Assert.Contains($"Dialog<{contract},", catalog);

        var module = Read("DotCharter.Presentation", "Dialogs", "DialogsModule.cs");
        Assert.Contains($"AddScoped<{contract}, {viewModel}>()", module);

        AssertMenuKeyInBothResx(menuKey);
    }

    /// <summary>
    ///     MIDI import is a hosted dialog behind a LAUNCHER (the picker and the
    ///     off-thread parse come before the dialog), so its chain has one more hop:
    ///     menu → import-menu command → launcher field → launcher opens its request →
    ///     catalog → module → composition root.
    /// </summary>
    [Fact]
    public void The_hosted_midi_import_is_reachable_end_to_end()
    {
        var menu = MainMenuSource.ReadAllText();
        Assert.Contains("Loc Menu_ImportMidi", menu);
        Assert.Contains("ImportMidiCommand", menu);

        var imports = Read("DotCharter.UI", "ViewModels", "Shell", "ImportMenuViewModel.cs");
        Assert.Contains("private void ImportMidi()", imports);
        Assert.Contains("_midiImport", imports);

        var launcher = Read("DotCharter.Presentation", "Screens", "Dialogs", "MidiImportLauncher.cs");
        Assert.Contains("ShowAsync(new MidiImportRequest(", launcher);

        var catalog = Read("DotCharter.UI", "Views", "Shell", "ScreenCatalog.cs");
        Assert.Contains("Dialog<IMidiImportScreen,", catalog);

        var module = Read("DotCharter.Presentation", "Dialogs", "DialogsModule.cs");
        Assert.Contains("AddScoped<IMidiImportScreen, MidiImportViewModel>()", module);

        Assert.Contains("AddSingleton<DotCharter.Presentation.Screens.Dialogs.MidiImportLauncher>()",
            CompositionRootSource());

        AssertMenuKeyInBothResx("Menu_ImportMidi");
    }

    [Fact]
    public void The_dialog_less_phrase_propagation_command_is_reachable_end_to_end()
    {
        var menu = MainMenuSource.ReadAllText();
        Assert.Contains("Loc Menu_PropagatePhrase", menu);
        Assert.Contains("PropagatePhraseCommand", menu);

        // The maintenance verbs are the shell view model's Editing.cs partial.
        var vm = Read("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Editing.cs");
        Assert.Contains("private void PropagatePhrase()", vm);
        Assert.Contains("PropagatePhraseAtPlayhead()", vm);

        AssertMenuKeyInBothResx("Menu_PropagatePhrase");
    }

    [Fact]
    public void The_dialog_less_refret_command_is_reachable_end_to_end()
    {
        var menu = MainMenuSource.ReadAllText();
        Assert.Contains("Loc Menu_Refret", menu);
        Assert.Contains("RefretCommand", menu);

        var vm = Read("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Editing.cs");
        Assert.Contains("private void Refret()", vm);
        Assert.Contains("_chartMaintenance.Refret()", vm);

        // The status message keys the coordinator reports through.
        AssertMenuKeyInBothResx("Menu_Refret");
        AssertMenuKeyInBothResx("Menu_Refret_Tip");
        AssertMenuKeyInBothResx("Status_RefretApplied");
        AssertMenuKeyInBothResx("Status_RefretNoChange");
        AssertMenuKeyInBothResx("Status_RefretFailed");
    }

    /// <summary>
    ///     Generate hand shapes (arrangement) (#478): menu â†’ shell command â†’ the
    ///     maintenance verb, which must DRIVE the planner and commit it — a verb that
    ///     is reachable but plans nothing would pass every hop above it.
    /// </summary>
    [Fact]
    public void The_arrangement_hand_shape_generator_is_reachable_end_to_end()
    {
        var menu = MainMenuSource.ReadAllText();
        Assert.Contains("Loc Menu_GenerateHandShapesArrangement", menu);
        Assert.Contains("GenerateHandShapesForArrangementCommand", menu);

        var vm = Read("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Editing.cs");
        Assert.Contains("private void GenerateHandShapesForArrangement()", vm);
        Assert.Contains("_chartMaintenance.GenerateHandShapesForArrangement()", vm);

        var verb = Read("DotCharter.Services", "Editing", "Maintenance", "ChartMaintenanceService.Generation.cs");
        Assert.Contains("ArrangementHandShapeGenerator.Plan(arrangement)", verb);
        Assert.Contains("ArrangementHandShapeGenerator.BuildActions(arrangement, plan)", verb);
        Assert.Contains("_historyService.Do(", verb);

        // …and the D11 auto-fix drives the same planner.
        var fixer = Read("DotCharter.Services", "Editing", "Fixing", "ArrangementAutoFixService.cs");
        Assert.Contains("CoverChordsOutsideHandShapes(arrangement)", fixer);
        Assert.Contains("ArrangementHandShapeGenerator.ApplyInPlace(", fixer);

        AssertMenuKeyInBothResx("Menu_GenerateHandShapesArrangement");
        AssertMenuKeyInBothResx("Status_HandShapesGenerated");
        AssertMenuKeyInBothResx("Status_HandShapesGeneratedOne");
        AssertMenuKeyInBothResx("Status_HandShapesAllChordsCovered");
        AssertMenuKeyInBothResx("History_GenerateHandShapes");
    }

    /// <summary>
    ///     "Attach audio" is the command that was missing ALL five hops: a project
    ///     created without audio had no way to gain any. <c>LoadAudioFile</c> existed
    ///     on the shell view model but carried no <c>[RelayCommand]</c>, so no
    ///     command was ever generated and nothing referenced it.
    /// </summary>
    [Fact]
    public void The_attach_audio_command_is_reachable_end_to_end()
    {
        var menu = MainMenuSource.ReadAllText();
        Assert.Contains("Loc Menu_AttachAudio", menu);
        Assert.Contains("AttachAudioCommand", menu);

        // Project + audio-file lifecycle is the shell's ProjectMenuViewModel.
        var vm = Read("DotCharter.UI", "ViewModels", "Shell", "ProjectMenuViewModel.cs");
        Assert.Contains("private async Task AttachAudio()", vm);
        Assert.Contains("_projectSession.AttachAudioAsync(path)", vm);

        // The session API the command drives — an audition (LoadAudioFile) would
        // play the file but never write the project's audio reference.
        var session = Read("DotCharter.Services", "Session", "IProjectSessionService.cs");
        Assert.Contains("Task<bool> AttachAudioAsync(string sourcePath);", session);

        AssertMenuKeyInBothResx("Menu_AttachAudio");
        AssertMenuKeyInBothResx("Menu_AttachAudio_Tip");
        AssertMenuKeyInBothResx("Status_AttachedAudio");
        AssertMenuKeyInBothResx("Status_AttachAudioFailed");
        AssertMenuKeyInBothResx("Dialog_ReplaceAudio_Title");
        AssertMenuKeyInBothResx("Dialog_ReplaceAudio_Message");
    }

    [Theory]
    [MemberData(nameof(PaletteIds))]
    public void Each_power_tool_has_a_command_palette_entry(string paletteId)
    {
        var catalog = Read("DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs");
        Assert.Contains($"\"{paletteId}\"", catalog);
    }

    /// <summary>
    ///     The services themselves must be resolvable, or every command above throws
    ///     at composition time instead of at click time.
    /// </summary>
    [Theory]
    [InlineData("ILevelGenerationService, LevelGenerationService")]
    [InlineData("IPhraseIterationPropagationService")]
    [InlineData("IBatchChartLintService")]
    [InlineData("IRefretService, RefretService")]
    public void The_power_tool_services_are_registered_in_the_composition_root(string registration)
    {
        var app = CompositionRootSource();
        Assert.Contains(registration, app);
    }

    private static void AssertMenuKeyInBothResx(string key)
    {
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file);
            var keys = XDocument.Load(path).Root!
                .Elements("data")
                .Select(d => d.Attribute("name")?.Value)
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(keys.Contains(key), $"{file} is missing the '{key}' menu key.");
        }
    }

    /// <summary>
    ///     The composition root is ONE class spread over several partial files
    ///     (<c>App.axaml.cs</c> plus the topical <c>App.*.cs</c> slices), so a lint
    ///     over the registrations must read all of them — anchored on one file it
    ///     silently stops covering whichever slice a registration moved into.
    /// </summary>
    private static string CompositionRootSource() =>
        string.Concat(UiSourceTree
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "App*.cs")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));

    private static string Read(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}

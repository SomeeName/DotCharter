using System.Windows.Input;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.Infrastructure.Features;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.CommandPalette;

/// <summary>
///     Builds the command-palette read-model: one flat list of every
///     discoverable app command (the main-menu commands + the rebindable
///     <c>AppKeybindRegistrar</c> actions that the shell view-model and its
///     children can reach), each carrying a localized title, a category, the
///     live keyboard shortcut from the user's <see cref="KeybindConfig" /> and a
///     run/can-run delegate.
///     <para>
///     Built fresh each time the palette opens so shortcuts and enabled state
///     always reflect the current keybinds and selection. Titles and categories
///     reuse the existing <c>Keybind_*</c> / <c>Keybinds_*</c> / <c>Menu_*</c>
///     resource keys so the palette stays in sync with the rest of the UI and
///     needs no new per-command strings.
///     </para>
/// </summary>
public static class CommandCatalog
{
    public static IReadOnlyList<AppCommand> Build(MainWindowViewModel vm, ILocalizationService l)
    {
        var k = vm.ProjectState.Keybinds;
        var list = new List<AppCommand>();

        // Counted under the RelayCommand's own method name — the id the menu's
        // click hook records for the same command — so a verb run from the
        // palette and from the menu is one bucket in the usage report.
        AppCommand FromCommand(string id, string titleKey, string catKey, ICommand command,
            Func<KeyChord>? shortcut = null) =>
            new(id, l[titleKey], l[catKey],
                () => { if (command.CanExecute(null)) command.Execute(null); },
                shortcut, () => command.CanExecute(null),
                DotCharter.UI.Diagnostics.CommandIdResolver.Resolve(command, null, null));

        AppCommand FromAction(string id, string titleKey, string catKey, Action run,
            Func<KeyChord>? shortcut = null) =>
            new(id, l[titleKey], l[catKey], run, shortcut);

        // === Transport ===
        const string tp = "Keybinds_Transport";
        // Same door as the keybind (AppKeybindRegistrar) — see
        // TransportViewModel.TogglePlayPause for why neither may branch on the
        // view model's own IsPlaying.
        list.Add(FromAction("transport.playPause", "Keybind_PlayPause", tp,
            () => vm.Transport.TogglePlayPauseCommand.Execute(null), () => k.PlayPause));
        list.Add(FromAction("transport.toggleMetronome", "Keybind_ToggleMetronome", tp,
            () => vm.Transport.MetronomeEnabled = !vm.Transport.MetronomeEnabled, () => k.ToggleMetronome));
        list.Add(FromAction("transport.toggleClaps", "Keybind_ToggleClaps", tp,
            () => vm.Transport.ClapsEnabled = !vm.Transport.ClapsEnabled, () => k.ToggleClaps));
        list.Add(FromCommand("transport.toggleChartSynth", "Keybind_ToggleChartSynth", tp,
            vm.Transport.ToggleChartSynthCommand, () => k.ToggleChartSynth));
        // The editor loop (v0.35 stage 3). Clear is palette-only ON PURPOSE — it
        // is the rare half of the loop, and giving it a default chord would cost
        // a fifth row in an already crowded table for no muscle memory. Without
        // this entry it would have no reachable surface at all.
        list.Add(FromCommand("transport.setLoopStart", "Keybind_SetLoopStart", tp,
            vm.Transport.SetLoopStartCommand, () => k.SetLoopStart));
        list.Add(FromCommand("transport.setLoopEnd", "Keybind_SetLoopEnd", tp,
            vm.Transport.SetLoopEndCommand, () => k.SetLoopEnd));
        list.Add(FromCommand("transport.toggleLoop", "Keybind_ToggleLoop", tp,
            vm.Transport.ToggleLoopCommand, () => k.ToggleLoop));
        list.Add(FromCommand("transport.clearLoop", "Keybind_ClearLoop", tp, vm.Transport.ClearLoopCommand));
        list.Add(FromCommand("transport.auditionSelection", "Keybind_AuditionSelection", tp,
            vm.Transport.AuditionSelectionCommand, () => k.AuditionSelection));

        // === File ops ===
        const string fo = "Keybinds_FileOps";
        list.Add(FromCommand("file.newProject", "Keybind_NewProject", fo, vm.Imports.OpenNewProjectCommand,
            () => k.NewProject));
        list.Add(FromCommand("file.openProject", "Keybind_OpenProject", fo, vm.Project.OpenProjectCommand,
            () => k.OpenProject));
        // Open Recent (P1-5): the palette is rebuilt on every open, so the MRU
        // rows are cheap parameterized entries — one per recent project, same
        // item command the File ▸ Open Recent submenu binds.
        foreach (var (recent, index) in vm.Project.RecentProjects.Select(static (r, i) => (r, i)))
        {
            list.Add(new AppCommand($"file.openRecent.{index}",
                l.Format("Palette_OpenRecentFmt", recent.DisplayName), l[fo],
                () => recent.OpenCommand.Execute(null)));
        }

        list.Add(FromCommand("file.saveProject", "Keybind_SaveProject", fo, vm.Project.SaveProjectCommand,
            () => k.SaveProject));
        list.Add(FromCommand("file.saveProjectAs", "Keybind_SaveProjectAs", fo, vm.Project.SaveProjectAsCommand,
            () => k.SaveProjectAs));
        list.Add(FromCommand("file.importGuitarPro", "Menu_ImportGuitarPro", fo, vm.Imports.ImportGuitarProCommand));
        list.Add(FromCommand("file.importEof", "Menu_ImportEof", fo, vm.Imports.ImportEofCommand));
        list.Add(FromCommand("file.importRsXml", "Menu_ImportRsXml", fo, vm.Imports.ImportRsXmlCommand));
        list.Add(FromCommand("file.importMidi", "Menu_ImportMidi", fo, vm.Imports.ImportMidiCommand));
        list.Add(FromCommand("file.exportRocksmith", "Menu_ExportRocksmith", fo, vm.ShellDialogs.OpenRocksmithExportCommand));
        list.Add(FromCommand("file.exportDcSong", "Menu_ExportDcSong", fo, vm.ShellDialogs.OpenDcSongExportCommand));
        list.Add(FromCommand("file.exportGuitarPro", "Menu_ExportGuitarPro", fo, vm.Export.ExportGuitarProCommand));
        list.Add(FromCommand("file.exportEof", "Menu_ExportEof", fo, vm.Export.ExportEditorOnFireCommand));
        list.Add(FromCommand("file.exportSheetPdf", "Menu_ExportSheetPdf", fo, vm.Export.ExportSheetPdfCommand));
        list.Add(FromCommand("file.attachAudio", "Menu_AttachAudio", fo, vm.Project.AttachAudioCommand));
        list.Add(FromCommand("file.attachYoutubeAudio", "Menu_AttachYoutubeAudio", fo, vm.Imports.AttachYoutubeAudioCommand));
        list.Add(FromCommand("file.chartFromAudio", "Menu_ChartFromAudio", fo, vm.ShellDialogs.OpenChartFromAudioCommand));
        list.Add(FromCommand("file.songBrowser", "Menu_SongBrowser", fo, vm.OpenSongBrowserCommand));
        list.Add(FromCommand("file.revealProject", "Menu_RevealProjectInExplorer", fo,
            vm.Project.RevealProjectInExplorerCommand));
        list.Add(FromCommand("file.songMetadata", "Menu_SongMetadata", fo, vm.ShellDialogs.OpenSongMetadataCommand));
        list.Add(FromCommand("file.settings", "Menu_Settings", fo, vm.OpenSettingsCommand));
        // Routed through ExitRequested → MainWindow.Close() so the palette exit
        // hits the SAME unsaved-changes gate as the caption button and Alt+F4.
        list.Add(FromCommand("file.exit", "Menu_Exit", fo, vm.ExitApplicationCommand));

        // === Editing ===
        const string ed = "Keybinds_Editing";
        list.Add(FromCommand("edit.undo", "Keybind_Undo", ed, vm.ChartEditor.UndoCommand, () => k.Undo));
        list.Add(FromCommand("edit.redo", "Keybind_Redo", ed, vm.ChartEditor.RedoCommand, () => k.Redo));
        list.Add(FromCommand("edit.history", "History_Menu", ed, vm.ShellDialogs.OpenHistoryCommand));
        list.Add(FromCommand("edit.cut", "Keybind_Cut", ed, vm.CutCommand, () => k.Cut));
        list.Add(FromCommand("edit.copy", "Keybind_Copy", ed, vm.CopyCommand, () => k.Copy));
        list.Add(FromCommand("edit.paste", "Keybind_Paste", ed, vm.PasteCommand, () => k.Paste));
        list.Add(FromCommand("edit.pasteSpecial", "Menu_PasteSpecial", ed, vm.ShellDialogs.OpenPasteSpecialCommand,
            () => k.PasteSpecial));
        list.Add(FromCommand("edit.selectAll", "Keybind_SelectAll", ed, vm.SelectAllCommand, () => k.SelectAll));
        list.Add(FromCommand("edit.selectLike", "Keybind_SelectLike", ed, vm.SelectLikePatternCommand,
            () => k.SelectLike));
        list.Add(FromCommand("edit.patternBuilder", "Keybind_PatternBuilder", ed,
            vm.ShellDialogs.OpenPatternBuilderCommand, () => k.PatternBuilder));
        list.Add(FromCommand("edit.delete", "Keybind_DeleteSelected", ed, vm.DeleteSelectionCommand, () => k.Delete));
        list.Add(FromCommand("edit.snapSelected", "Keybind_SnapSelected", ed, vm.SnapSelectedCommand,
            () => k.SnapSelected));
        list.Add(FromCommand("edit.snapAll", "Keybind_SnapAll", ed, vm.SnapAllCommand, () => k.SnapAll));
        list.Add(FromCommand("edit.increaseLength", "Keybind_IncreaseLength", ed, vm.IncreaseLengthCommand,
            () => k.IncreaseLength));
        list.Add(FromCommand("edit.decreaseLength", "Keybind_DecreaseLength", ed, vm.DecreaseLengthCommand,
            () => k.DecreaseLength));

        // === View ===
        const string vw = "Keybinds_View";
        // The transport bar's grouped popovers. They reach the palette through
        // the SAME `RequestPopover` the keybinds do, so Ctrl+Shift+P and Alt+M
        // are one path — and the usage statistics count them as the actions they
        // are rather than reporting three rebindable rows as never used.
        list.Add(FromAction("view.openMixPopover", "Keybind_OpenMixPopover", vw,
            () => vm.RequestPopover(TransportPopover.Mix), () => k.OpenMixPopover));
        list.Add(FromAction("view.openToolsPopover", "Keybind_OpenToolsPopover", vw,
            () => vm.RequestPopover(TransportPopover.Tools), () => k.OpenToolsPopover));
        list.Add(FromAction("view.openTracksPopover", "Keybind_OpenTracksPopover", vw,
            () => vm.RequestPopover(TransportPopover.Tracks), () => k.OpenTracksPopover));
        // Every PANEL toggle goes through the view model's own command rather
        // than re-flipping the flag inline. They are one-liners today, so the two
        // spellings happened to agree — but the palette duplicating a shell verb
        // is how the two paths drift, and it left ToggleSheetMusic/StemMixer/
        // StackedTracks reachable from nothing at all
        // (RelayCommandReachabilityTests). Inspector, 3D Preview and Riff Library
        // go through the shell verb too: flipping the flag inline would put
        // their palette uses in a bucket of their own (`view.toggleInspector`)
        // while their neighbours count under the VERB (`ToggleStemMixer`),
        // against the one-verb-one-bucket rule on AppCommand.UsageId.
        // Pinned by CommandUsageBucketTests.
        list.Add(FromCommand("view.toggleInspector", "Keybind_ToggleInspector", vw,
            vm.ToggleInspectorCommand, () => k.ToggleInspector));
        list.Add(FromCommand("view.togglePreview3D", "Keybind_TogglePreview3D", vw,
            vm.TogglePreviewCommand, () => k.TogglePreview3D));
        list.Add(FromCommand("view.toggleSheetMusic", "Menu_PanelSheetMusic", vw, vm.ToggleSheetMusicCommand));
        list.Add(FromCommand("view.toggleAiChat", "Keybind_ToggleAiChat", vw,
            vm.ToggleAiChatCommand, () => k.ToggleAiChat));
        list.Add(FromCommand("view.toggleStemMixer", "Menu_PanelStemMixer", vw, vm.ToggleStemMixerCommand));
        list.Add(FromCommand("view.toggleStackedTracks", "Menu_PanelStackedTracks", vw,
            vm.ToggleStackedTracksCommand));
        list.Add(FromCommand("view.togglePhraseCatalog", "Menu_PanelPhraseCatalog", vw,
            vm.TogglePhraseCatalogCommand, () => k.TogglePhraseCatalog));
        list.Add(FromCommand("view.showChordShapes", "Menu_PanelChordShapes", vw, vm.ShowChordShapesCommand));
        list.Add(FromAction("view.toggleWaveform", "Keybind_ToggleWaveform", vw,
            () => vm.ProjectState.ShowWaveform = !vm.ProjectState.ShowWaveform, () => k.ToggleWaveform));
        list.Add(FromCommand("view.toggleMinimap", "Menu_ShowMinimap", vw, vm.ToggleMinimapCommand));
        // View ▸ Timeline Rows — the five ActiveRibbon flags. Their pill strip
        // above the timeline was deleted (the toggles became View-menu rows),
        // so the menu is their ONLY other door while the palette already
        // carried every neighbouring View toggle. Titles are composed
        // "Timeline Rows · X" from the same keys the menu rows wear, so the
        // two surfaces cannot drift and no new strings are minted. The ribbon
        // is read at CLICK time — it follows the focused track and is null
        // with no chart, in which case the toggle is a no-op like the menu's.
        string RowTitle(string key) => $"{l["Timeline_Rows"]} · {l[key]}";
        void AddRowToggle(string id, string titleKey, Action<RibbonVisibility> flip) =>
            list.Add(new AppCommand(id, RowTitle(titleKey), l[vw], () =>
            {
                var ribbon = vm.ChartEditor.ActiveRibbon;
                if (ribbon != null) flip(ribbon);
            }));
        AddRowToggle("view.toggleRowSections", "Ribbon_Sections",
            ribbon => ribbon.ShowSections = !ribbon.ShowSections);
        AddRowToggle("view.toggleRowPhrases", "Ribbon_Phrases",
            ribbon => ribbon.ShowPhrases = !ribbon.ShowPhrases);
        AddRowToggle("view.toggleRowLyrics", "Ribbon_Lyrics",
            ribbon => ribbon.ShowLyrics = !ribbon.ShowLyrics);
        AddRowToggle("view.toggleRowTones", "Ribbon_Tones",
            ribbon => ribbon.ShowTones = !ribbon.ShowTones);
        AddRowToggle("view.toggleRowMeasureInfo", "Ribbon_MeasureInfo",
            ribbon => ribbon.ShowMeasureMetadata = !ribbon.ShowMeasureMetadata);
        list.Add(FromCommand("view.zoomIn", "Keybind_ZoomIn", vw, vm.ZoomInCommand, () => k.ZoomIn));
        list.Add(FromCommand("view.zoomOut", "Keybind_ZoomOut", vw, vm.ZoomOutCommand, () => k.ZoomOut));
        list.Add(FromCommand("view.followPlayhead", "Menu_FollowPlayhead", vw, vm.ToggleTimelineFollowCommand));
        list.Add(FromCommand("view.floatTimeline", "Menu_FloatTimeline", vw, vm.PopOutTimelineCommand));
        list.Add(FromCommand("view.resetLayout", "Menu_ResetLayout", vw, vm.ResetLayoutCommand));

        // === Tempo map ===
        const string tm = "Menu_TempoMap";
        list.Add(FromCommand("tempo.setDefaultStartSilence", "Menu_SetDefaultStartSilence", tm,
            vm.ShellDialogs.OpenSetDefaultStartSilenceDialogCommand));
        list.Add(FromCommand("tempo.addSilenceAtStart", "Menu_AddSilenceAtStart", tm,
            vm.ShellDialogs.OpenAddSilenceAtStartDialogCommand));
        list.Add(FromCommand("tempo.addMeasuresAtStart", "Menu_AddMeasuresAtStart", tm,
            vm.ShellDialogs.OpenAddMeasuresAtStartDialogCommand));
        list.Add(FromCommand("tempo.addMeasuresAtEnd", "Menu_AddMeasuresAtEnd", tm,
            vm.ShellDialogs.OpenAddMeasuresAtEndDialogCommand));
        list.Add(FromCommand("arrangement.copyStructure", "Menu_CopyStructureToArrangements", tm,
            vm.CopyStructureToOtherArrangementsCommand));
        list.Add(FromCommand("arrangement.sync", "Menu_ArrangementSync", tm,
            vm.ShellDialogs.OpenArrangementSyncDialogCommand));
        list.Add(FromCommand("tempo.insertBeat", "Keybind_InsertBeatAtPlayhead", tm, vm.InsertBeatAtPlayheadCommand,
            () => k.InsertBeatAtPlayhead));
        list.Add(FromCommand("tempo.removeBeat", "Keybind_RemoveBeatAtPlayhead", tm, vm.RemoveBeatAtPlayheadCommand,
            () => k.RemoveBeatAtPlayhead));
        list.Add(FromCommand("tempo.addSilenceAtEnd", "Menu_AddSilenceAtEnd", tm, vm.ShellDialogs.OpenAddSilenceAtEndDialogCommand));
        list.Add(FromCommand("tempo.setAllBpm", "Menu_SetAllBpm", tm, vm.ShellDialogs.OpenSetBpmDialogCommand));
        list.Add(FromCommand("tempo.alignBeatGrid", "Menu_AlignBeatGrid", tm, vm.ShellDialogs.OpenAlignBeatGridDialogCommand));
        list.Add(FromCommand("tempo.alignToRecording", "Menu_AlignToRecording", tm,
            vm.ShellDialogs.OpenAlignToRecordingDialogCommand));
        list.Add(FromCommand("tempo.lockBeatMap", "Menu_LockBeatMap", tm, vm.ToggleBeatMapLockCommand));

        // === Arrangements ===
        const string ar = "Menu_Arrangements";
        list.Add(FromCommand("arr.new", "Menu_NewArrangement", ar, vm.Project.NewArrangementCommand));
        list.Add(FromCommand("arr.delete", "Menu_DeleteArrangement", ar, vm.Project.DeleteArrangementCommand));
        list.Add(FromCommand("arr.settings", "Menu_ArrangementSettings", ar, vm.ShellDialogs.OpenArrangementSettingsCommand));

        // === Vocals ===
        const string vo = "Menu_Vocals";
        list.Add(FromCommand("vocals.new", "Menu_NewVocalTrack", vo, vm.Project.NewVocalTrackCommand));
        list.Add(FromCommand("vocals.delete", "Menu_DeleteVocalTrack", vo, vm.Project.DeleteVocalTrackCommand));
        list.Add(FromCommand("vocals.insert", "Keybind_InsertVocalAtPlayhead", vo, vm.InsertVocalAtPlayheadCommand,
            () => k.InsertVocalAtPlayhead));
        list.Add(FromCommand("vocals.settings", "Menu_VocalTrackSettings", vo,
            vm.ShellDialogs.OpenVocalTrackSettingsCommand));

        // === Tools ===
        const string to = "Menu_Tools";
        list.Add(FromCommand("tools.generateFhp", "Menu_GenerateFhp", to, vm.GenerateFhpCommand));
        list.Add(FromCommand("tools.generateFhpForSelection", "Menu_GenerateFhpForSelection", to,
            vm.GenerateFhpForSelectionCommand));
        list.Add(FromAction("tools.editFhp", "Keybind_EditFhp", to, () => vm.ShellDialogs.OpenFhp(), () => k.EditFhp));
        list.Add(FromCommand("tools.generateHandShape", "Keybind_GenerateHandShape", to, vm.GenerateHandShapeCommand,
            () => k.GenerateHandShape));
        list.Add(FromCommand("tools.generateArpeggio", "Keybind_GenerateArpeggio", to, vm.GenerateArpeggioCommand,
            () => k.GenerateArpeggio));
        list.Add(FromCommand("tools.generateHandShapesArrangement", "Menu_GenerateHandShapesArrangement", to,
            vm.GenerateHandShapesForArrangementCommand));
        list.Add(FromCommand("tools.acceptSuggestedHandShapes", "Menu_AcceptSuggestedHandShapes", to,
            vm.AcceptSuggestedHandShapesCommand, () => k.AcceptSuggestedHandShapes));
        list.Add(FromCommand("tools.acceptSuggestedArpeggios", "Menu_AcceptSuggestedArpeggios", to,
            vm.AcceptSuggestedArpeggiosCommand, () => k.AcceptSuggestedArpeggios));
        list.Add(FromCommand("tools.splitHandShape", "Keybind_SplitHandShape", to, vm.SplitHandShapeCommand,
            () => k.SplitHandShape));
        if (vm.Inspector.ChordShapes is { } chordShapes)
        {
            list.Add(FromCommand("tools.repeatPreviousChord", "Keybind_RepeatPreviousChord", to,
                chordShapes.RepeatPreviousChordCommand, () => k.RepeatPreviousChord));
        }

        list.Add(FromCommand("tools.applyHandShapeTemplate", "Menu_ApplyHandShapeTemplateToChords", to,
            vm.ApplyHandShapeTemplateToChordsCommand, () => k.ApplyHandShapeTemplateToChords));
        list.Add(FromCommand("tools.powerChordSmall", "Keybind_ConvertToPowerChordSmall", to,
            vm.ConvertToPowerChordSmallCommand, () => k.ConvertToPowerChordSmall));
        list.Add(FromCommand("tools.powerChordBig", "Keybind_ConvertToPowerChordBig", to,
            vm.ConvertToPowerChordBigCommand, () => k.ConvertToPowerChordBig));
        list.Add(FromCommand("tools.phraseCatalogAdd", "Keybind_PhraseCatalogAdd", to,
            vm.PhraseCatalog.CaptureSelectionCommand, () => k.PhraseCatalogAdd));
        list.Add(FromCommand("tools.phraseCatalogPaste", "Keybind_PhraseCatalogPaste", to,
            vm.PhraseCatalog.PasteSelectedCommand, () => k.PhraseCatalogPaste));
        list.Add(FromCommand("tools.phraseCatalogPasteByName", "Keybind_PhraseCatalogPasteByName", to,
            vm.FocusPhraseCatalogSearchCommand, () => k.PhraseCatalogPasteByName));
        list.Add(FromCommand("tools.mergeNotesIntoChord", "Menu_MergeNotesIntoChord", to,
            vm.MergeNotesIntoChordCommand));
        list.Add(FromCommand("tools.splitChordsIntoNotes", "Menu_SplitChordsIntoNotes", to,
            vm.SplitChordsIntoNotesCommand));
        list.Add(FromCommand("tools.toggleOctaveFingering", "Menu_ToggleOctaveFingering", to,
            vm.ToggleOctaveFingeringCommand));
        list.Add(FromCommand("tools.togglePowerChordFingering", "Menu_TogglePowerChordFingering", to,
            vm.TogglePowerChordFingeringCommand));
        list.Add(FromCommand("tools.mutedStrumsToChord", "Menu_MutedStrumsToChord", to,
            vm.MutedStrumsToChordCommand, () => k.MutedStrumsToChord));
        list.Add(FromCommand("tools.toggleGhostGems", "Menu_ToggleGhostGems", to,
            vm.ToggleGhostGemsCommand, () => k.ToggleGhostGems));
        list.Add(FromCommand("tools.tappedHarmonic", "Menu_TappedHarmonic", to,
            vm.TappedHarmonicCommand, () => k.TappedHarmonic));
        list.Add(FromCommand("tools.toggleChordSplit", "Keybind_ToggleChordSplit", to, vm.ToggleChordSplitCommand,
            () => k.ToggleChordSplit));
        list.Add(FromCommand("tools.toggleChordOnlyBox", "Keybind_ToggleChordOnlyBox", to, vm.ToggleChordOnlyBoxCommand,
            () => k.ToggleChordOnlyBox));
        list.Add(FromCommand("tools.toggleChordPassOtherNotes", "Keybind_ToggleChordPassOtherNotes", to,
            vm.ToggleChordPassOtherNotesCommand, () => k.ToggleChordPassOtherNotes));
        list.Add(FromCommand("tools.tuner", "Menu_Tuner", to, vm.ShellDialogs.OpenTunerCommand));
        list.Add(FromCommand("tools.calibrateLatency", "Settings_CalibrateLatency", to,
            vm.ShellDialogs.OpenLatencyWizardCommand));
        list.Add(FromCommand("tools.toneDesigner", "Menu_ToneDesigner", to, vm.ShellDialogs.OpenToneDesignerCommand));
        list.Add(FromCommand("tools.pedalboard", "Menu_Pedalboard", to, vm.ToneRig.OpenPedalboardCommand));
        list.Add(FromCommand("tools.generateRocksmithTones", "Menu_GenerateRocksmithTones", to,
            vm.ToneRig.GenerateRocksmithTonesCommand));
        // Tools ▸ Fix & clean up: the AI chart pass, on the selection or the whole arrangement.
        if (vm.ShellDialogs.AiChart is { } ai)
        {
            list.Add(FromCommand("tools.aiFixFhpFingering", "Menu_AiFixFhpFingering", to, ai.FixFhpAndFingeringCommand));
            list.Add(FromCommand("tools.aiFixFhpOnly", "Menu_AiFixFhpOnly", to, ai.FixFhpOnlyCommand));
            list.Add(FromCommand("tools.aiFixFingeringOnly", "Menu_AiFixFingeringOnly", to, ai.FixFingeringOnlyCommand));
            list.Add(FromCommand("tools.aiFixChordNames", "Menu_AiFixChordNames", to, ai.FixChordNamesCommand));
            list.Add(FromCommand("tools.aiFixChordNamesMusical", "Menu_AiFixChordNamesMusical", to,
                ai.FixChordNamesMusicalCommand));
            list.Add(FromCommand("tools.aiSettings", "Menu_AiSettings", to, ai.OpenAiSettingsCommand));
        }
        list.Add(FromCommand("tools.showlights", "Menu_ShowlightsEditor", to, vm.ShellDialogs.OpenShowlightsCommand));
        list.Add(FromCommand("tools.applyDlcBuilderFixes", "Timeline_Ctx_ApplyDlcBuilderFixes", to, vm.ApplyDlcBuilderFixesCommand));
        list.Add(FromCommand("tools.improveChart", "Menu_ImproveChart", to, vm.ImproveChartCommand));
        list.Add(FromCommand("tools.removeDoubleStopNames", "Menu_RemoveDoubleStopNames", to,
            vm.RemoveDoubleStopChordNamesCommand));
        list.Add(FromCommand("tools.autoFixChart", "Menu_AutoFixChart", to, vm.AutoFixChartCommand));
        list.Add(FromCommand("tools.squashLevels", "Menu_SquashLevels", to, vm.SquashLevelsCommand));
        list.Add(FromCommand("tools.generateLevels", "Menu_GenerateLevels", to,
            vm.ShellDialogs.OpenLevelGenerationCommand));
        list.Add(FromCommand("tools.generateStructure", "Menu_GenerateStructure", to,
            vm.GenerateStructureCommand));
        list.Add(FromCommand("tools.propagatePhrase", "Menu_PropagatePhrase", to, vm.PropagatePhraseCommand));
        list.Add(FromCommand("view.screenshot", "Menu_Screenshot", to, vm.Export.CaptureHighwayScreenshotCommand));
        list.Add(FromCommand("tools.refret", "Menu_Refret", to, vm.RefretCommand));
        list.Add(FromCommand("tools.chartDiff", "Menu_ChartDiff", to, vm.ShellDialogs.OpenChartDiffCommand));
        list.Add(FromCommand("tools.qualityReport", "Menu_QualityReport", to, vm.ShellDialogs.OpenQualityReportCommand));
        list.Add(FromCommand("tools.batchLint", "Menu_BatchLint", to, vm.ShellDialogs.OpenBatchLintCommand));
        list.Add(FromCommand("tools.separateStems", "Menu_SeparateStems", to, vm.StemSeparation.SeparateStemsCommand));

        // === Note modifiers: the vocabulary a charter uses most ===============
        //
        // The catalogue must wrap more than what hangs off MainWindowViewModel —
        // otherwise `Ctrl+Shift+P` cannot answer "what was that key again" for the
        // verbs pressed most often, and several would be reachable by KEYBIND ALONE.
        // That also skews the usage statistics, whose denominator is this catalogue: a verb
        // nobody can find reads as a verb nobody wants.
        //
        // Each is the same call the AppKeybindRegistrar row makes, so the palette
        // and the key cannot drift; each reuses the Keybinds settings tab's own
        // `Keybind_*` title, so no new strings.
        const string nm = "Keybinds_NoteModifiers";
        var highway = vm.HighwayEditing;

        // Per-string ("independent") toggles and their "group" variants, which
        // set every targeted sound to the FIRST one's next value. They are two
        // different verbs (see SelectedNoteMutationCommands), so both are listed.
        list.Add(FromAction("note.toggleHopo", "Keybind_ToggleHopo", nm,
            highway.ToggleSelectedHopo, () => k.ToggleHopo));
        list.Add(FromAction("note.toggleHopoGroup", "Keybind_ToggleHopoGroup", nm,
            highway.ToggleSelectedHopoGroup, () => k.ToggleHopoGroup));
        list.Add(FromAction("note.toggleHarmonic", "Keybind_ToggleHarmonic", nm,
            highway.ToggleSelectedHarmonic, () => k.ToggleHarmonic));
        list.Add(FromAction("note.toggleHarmonicGroup", "Keybind_ToggleHarmonicGroup", nm,
            highway.ToggleSelectedHarmonicGroup, () => k.ToggleHarmonicGroup));
        list.Add(FromAction("note.toggleMute", "Keybind_ToggleMute", nm,
            highway.ToggleSelectedMute, () => k.ToggleMute));
        list.Add(FromAction("note.toggleMuteGroup", "Keybind_ToggleMuteGroup", nm,
            highway.ToggleSelectedMuteGroup, () => k.ToggleMuteGroup));
        list.Add(FromAction("note.toggleLinkNext", "Keybind_ToggleLinkNext", nm,
            highway.ToggleSelectedLinkNext, () => k.ToggleLinkNext));
        list.Add(FromAction("note.toggleLinkNextGroup", "Keybind_ToggleLinkNextGroup", nm,
            highway.ToggleSelectedLinkNextGroup, () => k.ToggleLinkNextGroup));
        list.Add(FromAction("note.toggleVibrato", "Keybind_ToggleVibrato", nm,
            highway.ToggleSelectedVibrato, () => k.ToggleVibrato));
        list.Add(FromAction("note.toggleVibratoGroup", "Keybind_ToggleVibratoGroup", nm,
            highway.ToggleSelectedVibratoGroup, () => k.ToggleVibratoGroup));
        list.Add(FromAction("note.toggleTremolo", "Keybind_ToggleTremolo", nm,
            highway.ToggleSelectedTremolo, () => k.ToggleTremolo));
        list.Add(FromAction("note.toggleTremoloGroup", "Keybind_ToggleTremoloGroup", nm,
            highway.ToggleSelectedTremoloGroup, () => k.ToggleTremoloGroup));
        list.Add(FromAction("note.togglePicking", "Keybind_ToggleBassPicking", nm,
            highway.ToggleSelectedPicking, () => k.TogglePicking));
        list.Add(FromAction("note.togglePickingGroup", "Keybind_TogglePickingGroup", nm,
            highway.ToggleSelectedPickingGroup, () => k.TogglePickingGroup));
        list.Add(FromAction("note.toggleAccent", "Keybind_ToggleAccent", nm,
            highway.ToggleSelectedAccent, () => k.ToggleAccent));
        list.Add(FromAction("note.toggleAccentGroup", "Keybind_ToggleAccentGroup", nm,
            highway.ToggleSelectedAccentGroup, () => k.ToggleAccentGroup));
        list.Add(FromAction("note.toggleIgnore", "Keybind_ToggleIgnore", nm,
            highway.ToggleSelectedIgnore, () => k.ToggleIgnore));
        list.Add(FromAction("note.toggleIgnoreGroup", "Keybind_ToggleIgnoreGroup", nm,
            highway.ToggleSelectedIgnoreGroup, () => k.ToggleIgnoreGroup));
        list.Add(FromAction("note.togglePassOtherNotes", "Keybind_TogglePassOtherNotes", nm,
            highway.ToggleSelectedPassOtherNotes, () => k.TogglePassOtherNotes));
        list.Add(FromAction("note.togglePassOtherNotesGroup", "Keybind_TogglePassOtherNotesGroup", nm,
            highway.ToggleSelectedPassOtherNotesGroup, () => k.TogglePassOtherNotesGroup));

        // Fret and string. The octave shift is ONE command (a twelve-iteration
        // loop in the registrar would spend twelve history nodes).
        list.Add(FromAction("note.fretUp", "Keybind_MoveFretUp", nm,
            highway.MoveSelectedFretUp, () => k.FretUp));
        list.Add(FromAction("note.fretDown", "Keybind_MoveFretDown", nm,
            highway.MoveSelectedFretDown, () => k.FretDown));
        list.Add(FromAction("note.fretUpOctave", "Keybind_FretUpOctave", nm,
            () => highway.MoveSelectedFretByOctave(1), () => k.FretUpOctave));
        list.Add(FromAction("note.fretDownOctave", "Keybind_FretDownOctave", nm,
            () => highway.MoveSelectedFretByOctave(-1), () => k.FretDownOctave));
        list.Add(FromAction("note.stringUp", "Keybind_MoveStringUp", nm,
            // The ceiling is the ACTIVE ARRANGEMENT's lane count — the same answer
            // AppKeybindRegistrar gives the StringUp KEY, and it has to be, or the
            // palette and the keybind disagree about a 4-string bass; the project-level
            // ChartData carries no tuning to read.
            () => highway.MoveSelectedStringUp(
                StringDigitTargeting.StringCountOf(vm.ChartEditor.ActiveArrangement)),
            () => k.StringUp));
        list.Add(FromAction("note.stringDown", "Keybind_MoveStringDown", nm,
            highway.MoveSelectedStringDown, () => k.StringDown));

        // The same lane step, keeping the PITCH — the note is re-fretted for its
        // new string. The palette drives the MODEL direction directly; only the
        // arrow KEYS have to translate screen direction into an index step, and
        // they do that in AppKeybindRegistrar.
        list.Add(FromAction("note.stringUpSamePitch", "Keybind_StringUpSamePitch", nm,
            () => highway.MoveSelectedStringPreservingPitch(
                +1, StringDigitTargeting.StringCountOf(vm.ChartEditor.ActiveArrangement)),
            () => k.StringUpSamePitch));
        list.Add(FromAction("note.stringDownSamePitch", "Keybind_StringDownSamePitch", nm,
            () => highway.MoveSelectedStringPreservingPitch(
                -1, StringDigitTargeting.StringCountOf(vm.ChartEditor.ActiveArrangement)),
            () => k.StringDownSamePitch));

        // Chord-template fingering. Six verbs, all keyboard-only before this.
        list.Add(FromAction("note.fingerNone", "Keybind_SetFingerNone", nm,
            () => vm.Inspector.SetFingerOnSelectedStringsCommand.Execute(-1), () => k.SetFingerNone));
        list.Add(FromAction("note.fingerThumb", "Keybind_SetFingerThumb", nm,
            () => vm.Inspector.SetFingerOnSelectedStringsCommand.Execute(0), () => k.SetFingerThumb));
        list.Add(FromAction("note.finger1", "Keybind_SetFinger1", nm,
            () => vm.Inspector.SetFingerOnSelectedStringsCommand.Execute(1), () => k.SetFinger1));
        list.Add(FromAction("note.finger2", "Keybind_SetFinger2", nm,
            () => vm.Inspector.SetFingerOnSelectedStringsCommand.Execute(2), () => k.SetFinger2));
        list.Add(FromAction("note.finger3", "Keybind_SetFinger3", nm,
            () => vm.Inspector.SetFingerOnSelectedStringsCommand.Execute(3), () => k.SetFinger3));
        list.Add(FromAction("note.finger4", "Keybind_SetFinger4", nm,
            () => vm.Inspector.SetFingerOnSelectedStringsCommand.Execute(4), () => k.SetFinger4));

        // The string-edit mask. Only CLEAR is listed: the nine per-string toggles
        // are a digit FAMILY (`StringMaskBase` + 1..9) whose verb needs an
        // argument, and nine near-identical rows would bury the rest of the list.
        // Clearing is the one that returns the editor to a known state, and the
        // mask is drawn on the lane headers, so its state is never invisible.
        list.Add(FromAction("edit.stringMaskClear", "Keybind_StringMaskClear", nm,
            vm.ProjectState.ClearStringEditMask, () => k.StringMaskClear));

        // === Editing verbs that were menu- or keybind-only ====================
        var global = vm.GlobalEditing;

        list.Add(FromAction("edit.pasteAtMouse", "Keybind_PasteAtMouse", ed,
            () => global.Paste(vm.ProjectState.LastPointerTimeMs, vm.Transport.GridDivisor),
            () => k.PasteAtMouse));
        list.Add(FromAction("edit.duplicate", "Keybind_DuplicateSelection", ed, () =>
        {
            // Duplicate = copy + paste-at-mouse, exactly as the keybind spells it.
            global.CopySelected(vm.ChartEditor.FocusArea);
            global.Paste(vm.ProjectState.LastPointerTimeMs, vm.Transport.GridDivisor);
        }, () => k.DuplicateSelection));
        list.Add(FromAction("edit.deleteRelated", "Keybind_DeleteRelated", ed,
            () => global.DeleteSelectedRelated(vm.ChartEditor.FocusArea), () => k.DeleteRelated));
        list.Add(FromCommand("edit.checkpoints", "Checkpoints_Menu", ed,
            vm.ShellDialogs.OpenCheckpointsCommand));
        // Edit ▸ Bookmarks: ten menu rows with typed Int32 parameters, mirrored
        // here the way the Open Recent rows are. The keybind digit family
        // (BookmarkBase) is excluded from palette COVERAGE with a fair excuse
        // for the KEYS — but the menu proves the verb works without an
        // argument picker, and the palette carried no bookmark verb at all.
        for (var slot = 0; slot <= 9; slot++)
        {
            var captured = slot;
            list.Add(new AppCommand($"edit.toggleBookmark.{captured}",
                l.Format("Palette_ToggleBookmarkFmt", captured), l[ed],
                () =>
                {
                    if (vm.ToggleBookmarkCommand.CanExecute(captured))
                        vm.ToggleBookmarkCommand.Execute(captured);
                }));
        }

        list.Add(FromAction("tools.doubleTime", "Keybind_DoubleTime", to,
            global.ApplyDoubleTimeToSelection, () => k.DoubleTime));
        list.Add(FromAction("tools.halfTime", "Keybind_HalfTime", to,
            global.ApplyHalfTimeToSelection, () => k.HalfTime));
        list.Add(FromCommand("tools.stampSection", "Keybind_StampSection", to,
            vm.StampSectionCommand, () => k.StampSection));

        // FHP anchor nudges (0.40). The dialog (`tools.editFhp`) was already here;
        // the four keys that replaced 16 modal round trips in one session were not.
        list.Add(FromAction("tools.anchorFretUp", "Keybind_AnchorFretUp", to,
            () => NudgeAnchor(vm, 1, AnchorNudgeTarget.Fret), () => k.AnchorFretUp));
        list.Add(FromAction("tools.anchorFretDown", "Keybind_AnchorFretDown", to,
            () => NudgeAnchor(vm, -1, AnchorNudgeTarget.Fret), () => k.AnchorFretDown));
        list.Add(FromAction("tools.anchorWidthUp", "Keybind_AnchorWidthUp", to,
            () => NudgeAnchor(vm, 1, AnchorNudgeTarget.Width), () => k.AnchorWidthUp));
        list.Add(FromAction("tools.anchorWidthDown", "Keybind_AnchorWidthDown", to,
            () => NudgeAnchor(vm, -1, AnchorNudgeTarget.Width), () => k.AnchorWidthDown));

        // === Help ===
        const string hp = "Menu_Help";
        list.Add(FromCommand("help.keyboardShortcuts", "Menu_KeyboardShortcuts", hp,
            vm.ShellDialogs.OpenKeyboardShortcutsCommand));
        list.Add(FromCommand("help.about", "Menu_About", hp, vm.Help.ShowAboutCommand));
        list.Add(FromCommand("help.license", "Menu_License", hp, vm.Help.OpenLicenseFileCommand));
        // The two "something is wrong / what have I been doing" surfaces —
        // both belong in the palette: Session Health is exactly the
        // screen a user hunts for when something misbehaves.
        list.Add(FromCommand("help.usageStatistics", "Menu_UsageStatistics", hp,
            vm.Help.ShowUsageStatisticsCommand));
        list.Add(FromCommand("help.sessionHealth", "Menu_SessionHealth", hp, vm.Help.ShowDiagnosticsCommand));
        // Settings ▸ Updates ▸ Show pending update was the ONLY route, and the
        // startup check's notice lives in the one shared status slot that the
        // next message erases — so a found update was routinely unreachable.
        list.Add(FromCommand("help.checkForUpdates", "Menu_CheckForUpdates", hp,
            vm.Help.CheckOrShowUpdateCommand));

        // The YouTube entry is dropped when the public-build switch is off, a
        // package-reading one when the build cannot read packages.
        return ContestedFeatures.FilterPalette(list, c => c.Id, ContestedFeatures.Enabled, vm.Project.PackageFeatures);
    }

    /// <summary>The capo is read at PRESS time from the arrangement being edited,
    /// never captured — the same rule <c>AppKeybindRegistrar.NudgeAnchor</c>
    /// follows, because the anchor floor is <c>capo + 1</c>.</summary>
    private static void NudgeAnchor(MainWindowViewModel vm, int delta, AnchorNudgeTarget target) =>
        vm.HighwayEditing.AdjustSelectedAnchors(delta, target, vm.ChartEditor.ActiveArrangement?.Capo ?? 0);
}

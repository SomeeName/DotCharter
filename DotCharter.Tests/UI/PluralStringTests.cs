using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Localization;

namespace DotCharter.Tests.UI;

/// <summary>
///     The game surfaces print small counts constantly, and a plural-only
///     resource reads as broken the moment one of them is 1: the launch screen
///     said "1 Tage Serie", the calendar "1 aktive Tage" and "1 Sessions".
///     English hides it better than German, which is exactly why it survived.
///
///     <para>
///         <c>LocalizationService.Plural</c> is the two-form chooser. These
///         guards pin the chooser itself and pin that every counted string it is
///         used for HAS both forms in English and German — a missing singular
///         resolves to the key name at runtime, which is the silent failure
///         this replaces — and a translated form of each in Japanese, which
///         has no plural and so gets its own, different check.
///     </para>
/// </summary>
public sealed class PluralStringTests
{
    /// <summary>Composite-format argument 0, with or without an alignment /
    /// format specifier — <c>{0}</c>, <c>{0:N0}</c>, <c>{0,3}</c>.</summary>
    private static readonly Regex ArgZero = new(@"\{0(?:,-?\d+)?(?::[^}]*)?\}", RegexOptions.Compiled);

    /// <summary>Every (singular, plural) pair currently in use. Adding a counted
    /// string means adding its pair here — and
    /// <see cref="EveryPluralCallSiteInTheApp_IsRegisteredInThePairsTable" />
    /// fails the gate if you forget.</summary>
    public static readonly (string One, string Many)[] PairList =
    [
        // 0.49: the song browser's per-path note count and the settings
        // footer's unsaved-changes counter.
        ("SongBrowser_NoteCountOne", "SongBrowser_NoteCount"),
        ("ToneDesign_Status_CheckingModulesOne", "ToneDesign_Status_CheckingModules"),
        ("Settings_UnsavedOne", "Settings_Unsaved"),
        // 0.49: the onboarding wizard's library page ("412 songs found").
        ("Onboarding_Library_FoundOne", "Onboarding_Library_FoundFmt"),
        // 0.51: the timeline's SONG MAP caption ("4:12 · 84 BARS").
        ("Timeline_SongMap_BarOne", "Timeline_SongMap_BarsFmt"),
        // 0.51: the FEHLER tab's headline ("14 issues found"). It read
        // "1 Probleme gefunden" for a single finding, which is the tab a
        // charter sees most often once a chart is nearly clean.
        ("Inspector_Issues_CountOne", "Inspector_Issues_Count"),
        // 0.52: the Edit-history footer's meta line ("4 STATES · NO BRANCHES").
        ("History_Summary_StatesOne", "History_Summary_StatesFmt"),
        ("History_Summary_BranchesOne", "History_Summary_BranchesFmt"),
        // 0.52: the crash-recovery head's "N autosaved snapshots were found".
        ("Recovery_SubtitleOne", "Recovery_SubtitleFmt"),
        // 0.58 sweep: twelve counted strings that were still calling Format().
        // Every one of them can legitimately reach 1 — the phrase group with a
        // single iteration and the hand shape enclosing a single sound are the
        // ORDINARY case, not an edge — and each read "1 Iterationen",
        // "1 Klänge darin", "1 Durchläufe" in German until now.
        ("Inspector_Phrases_IterationsOne", "Inspector_Phrases_IterationsFmt"),
        // #246: the Song tab's sections and tones, in the Phrases panel's shape.
        ("Inspector_Sections_PlacesOne", "Inspector_Sections_PlacesFmt"),
        ("Inspector_Sections_DeleteTipOne", "Inspector_Sections_DeleteTipFmt"),
        ("Inspector_Tones_UsesOne", "Inspector_Tones_UsesFmt"),
        ("Inspector_Tones_DeleteTipOne", "Inspector_Tones_DeleteTipFmt"),
        ("Inspector_Phrases_DeleteTipOne", "Inspector_Phrases_DeleteTipFmt"),
        ("Inspector_Phrases_DeletedOne", "Inspector_Phrases_DeletedFmt"),
        ("Inspector_Scope_SoundsOne", "Inspector_Scope_Sounds"),
        ("Inspector_Issue_FindingsOne", "Inspector_Issue_Findings"),
        // #601: the Sound tab's next task with nothing selected.
        ("Inspector_Task_FingeringsOne", "Inspector_Task_Fingerings"),
        ("Inspector_Task_UnnamedOne", "Inspector_Task_Unnamed"),
        ("Inspector_Task_BlockersOne", "Inspector_Task_Blockers"),
        ("Inspector_Span_EnclosesOne", "Inspector_Span_Encloses"),
        ("Inspector_Shape_RebindsWarningOne", "Inspector_Shape_RebindsWarning"),
        ("Settings_SearchResultCountOne", "Settings_SearchResultCount"),
        ("Nam_SearchResultCountOne", "Nam_SearchResultCount"),
        ("PlayMode_InGameTones_SummaryOne", "PlayMode_InGameTones_Summary"),
        ("PlayMode_Launch_SongOne", "PlayMode_Launch_SongsFmt"),
        ("Status_PsarcScanCompleteOne", "Status_PsarcScanComplete"),
        // These two live in DotCharter.Services (the auto-fix / improve-chart
        // result records format their own status line), which is why the UI-only
        // lookup lint never saw them.
        ("Status_AutoFixAppliedOne", "Status_AutoFixApplied"),
        ("Status_ImproveChartAppliedOne", "Status_ImproveChartApplied"),
        // #952: Apply DLC Builder fixes' status; #184: the export findings' Fix.
        ("Status_DlcBuilderFixesAppliedOne", "Status_DlcBuilderFixesApplied"),
        ("Status_ExportFixAppliedOne", "Status_ExportFixApplied"),
        ("Dialog_QualityReport_EasierLevelsOne", "Dialog_QualityReport_EasierLevels"),
        // 0.58: the decoded-PSARC-audio cache row. Both reach 1 in the ordinary
        // case — the cache is CAPPED, so the first song opened after a Clear (or
        // after a sweep on a machine with one huge archive) is exactly one song.
        ("Settings_PsarcAudioCache_UsageOne", "Settings_PsarcAudioCache_UsageFmt"),
        ("Settings_PsarcAudioCache_ClearedOne", "Settings_PsarcAudioCache_ClearedFmt"),
        // 0.60: batch lint's bulk-overwrite gate and its after-the-fact report.
        // Both reach 1 in the ordinary case — a folder holding one project, or
        // one project in a library needing a fix — and the sentence they carry
        // is the last thing between the user and an unundoable rewrite, so
        // "1 Projektdateien" is exactly the place not to look careless.
        ("BatchLint_ConfirmOverwriteOne", "BatchLint_ConfirmOverwriteFmt"),
        ("BatchLint_OverwroteOne", "BatchLint_OverwroteFmt"),
        // 0.61 l10n sweep: ten more counted strings (one went with the practice
        // plan, 2026-09-19), two of which were wrong
        // in ENGLISH as well ("1 controls" under every gear card with a single
        // knob, "1 hits" on a section a player clipped once).
        ("Inspector_SustainBeatsOne", "Inspector_SustainBeats"),
        ("PlayMode_DropTuner_LabelOne", "PlayMode_DropTuner_Label"),
        ("PlayMode_TuningMatch_ToggleOne", "PlayMode_TuningMatch_Toggle"),
        ("Settings_BackupMigrate_ExportedOne", "Settings_BackupMigrate_Exported"),
        ("Settings_BackupMigrate_ImportedOne", "Settings_BackupMigrate_Imported"),
        ("Status_RsXmlImportedVocalsOne", "Status_RsXmlImportedVocals"),
        ("Status_RsXmlImportedShowlightsOne", "Status_RsXmlImportedShowlights"),
        ("Rig_Gear_ControlsOne", "Rig_Gear_Controls"),
        ("PlayMode_Results_Timing_SectionHitsOne", "PlayMode_Results_Timing_SectionHits"),
        // …and the phrase catalogue summary, which was ONE string carrying
        // FIVE counts ("3 notes, 1 chords, 1 hand shapes, 1 anchors — 4 beats
        // long"). It is five pairs and a joiner now; every fragment reaches 1
        // in the ordinary case.
        ("PhraseCatalog_Entry_NotesOne", "PhraseCatalog_Entry_NotesFmt"),
        ("PhraseCatalog_Entry_ChordsOne", "PhraseCatalog_Entry_ChordsFmt"),
        ("PhraseCatalog_Entry_ShapesOne", "PhraseCatalog_Entry_ShapesFmt"),
        ("PhraseCatalog_Entry_AnchorsOne", "PhraseCatalog_Entry_AnchorsFmt"),
        ("PhraseCatalog_Entry_LengthOne", "PhraseCatalog_Entry_LengthFmt"),
        // 2026-09-04: the Pattern Builder's rail summary, which was thirty-odd
        // English literals in DotCharter.Services until it was localized. Its
        // one counted clause was wrong in English too — "within 1 frets" is
        // what a max-distance of 1 printed, and 1 is the tightest and most
        // useful setting the facet has.
        ("PatternSummary_WithinFretsOne", "PatternSummary_WithinFretsFmt"),
        // 2026-09-24: thirty-two counted strings that hedged with "(s)" — "Rewrote 1 iteration(s)",
        // "{0} Iteration(en)" — take their form from the count instead.
        ("Inspector_Phrases_PropagateConfirmOne", "Inspector_Phrases_PropagateConfirmFmt"),
        ("Inspector_Phrases_PropagatedOne", "Inspector_Phrases_PropagatedFmt"),
        // #484: the "Apply to" ladder's same-grip count and the status line after a commit.
        ("ChordTemplate_Apply_MoreShapesOne", "ChordTemplate_Apply_MoreShapes"),
        ("ChordTemplate_Applied_ShapesOne", "ChordTemplate_Applied_Shapes"),
        ("Inspector_Lyrics_StatusArmedOne", "Inspector_Lyrics_StatusArmed"),
        ("Status_DoubleStopNamesClearedOne", "Status_DoubleStopNamesCleared"),
        ("Status_HandShapesGeneratedOne", "Status_HandShapesGenerated"),
        ("Status_SuggestedHandShapesAcceptedOne", "Status_SuggestedHandShapesAccepted"),
        ("Status_OctaveFingeringSetOne", "Status_OctaveFingeringSet"),
        ("Status_PowerChordFingeringSetOne", "Status_PowerChordFingeringSet"),
        ("Status_MutedStrums_ConvertedOne", "Status_MutedStrums_Converted"),
        ("Status_GhostGems_GhostedOne", "Status_GhostGems_Ghosted"),
        ("Status_GhostGems_UnghostedOne", "Status_GhostGems_Unghosted"),
        ("Status_TappedHarmonic_DoneOne", "Status_TappedHarmonic_Done"),
        ("Status_ApplyHandShapeTemplate_DoneOne", "Status_ApplyHandShapeTemplate_Done"),
        ("Status_PowerChordSmallCreatedOne", "Status_PowerChordSmallCreated"),
        ("Status_PowerChordBigCreatedOne", "Status_PowerChordBigCreated"),
        ("Settings_StemModels_CacheUsageOne", "Settings_StemModels_CacheUsage"),
        ("Settings_StemModels_CacheClearedOne", "Settings_StemModels_CacheCleared"),
        ("Dialog_Tones_GenerateDoneOne", "Dialog_Tones_GenerateDone"),
        ("Dialog_Tones_GenerateSkippedOne", "Dialog_Tones_GenerateSkipped"),
        ("Dialog_Tones_GenerateSilentOne", "Dialog_Tones_GenerateSilent"),
        ("Dialog_Tones_GenerateNoisyOne", "Dialog_Tones_GenerateNoisy"),
        ("Dialog_Tones_GenerateTooShortOne", "Dialog_Tones_GenerateTooShort"),
        ("Dialog_Tones_ImportResultOne", "Dialog_Tones_ImportResult"),
        ("LibraryDoctor_HealthyOne", "LibraryDoctor_Healthy"),
        ("Status_PropagatePhraseAppliedOne", "Status_PropagatePhraseApplied"),
        ("BatchLint_RunningOne", "BatchLint_Running"),
        ("BatchLint_RowIssuesOne", "BatchLint_RowIssues"),
        ("ChartDiff_ChordNotesOne", "ChartDiff_ChordNotes"),
        ("PlayMode_InGameTones_ApproximatedOne", "PlayMode_InGameTones_Approximated"),
        ("Diagnostics_ExportedOne", "Diagnostics_Exported"),
        ("Status_RefretAppliedOne", "Status_RefretApplied"),
        ("EofExport_DoneLossyOne", "EofExport_DoneLossy"),
        ("ToneGenerate_DoneOne", "ToneGenerate_Done"),
        ("Status_MeasuresAddedAtStartOne", "Status_MeasuresAddedAtStart"),
        ("Status_MeasuresAddedAtEndOne", "Status_MeasuresAddedAtEnd"),
        ("Status_MeasuresAddedAtEndPastAudioOne", "Status_MeasuresAddedAtEndPastAudio"),
        ("Status_NotesMergedIntoChordsOne", "Status_NotesMergedIntoChords"),
        ("Status_ChordsSplitIntoNotesOne", "Status_ChordsSplitIntoNotes"),
        // 2026-09-24: the Library doctor's duplicate finding, an English literal until then.
        ("LibraryDoctor_Detail_DuplicateOne", "LibraryDoctor_Detail_Duplicate"),
        // …and Song Select's artist groups, which said "1 SONGS" for every one-song artist.
        ("PlayMode_SongSelect_GroupCountOne", "PlayMode_SongSelect_GroupCountFmt"),
        ("PlayMode_GetReady_Path_NotesOne", "PlayMode_GetReady_Path_Notes"),
        // 2026-09-25: the guitar VST chain's status pill, a hand-rolled English "slot{s}" until then.
        ("Progress_LoadingGuitarVstChainOne", "Progress_LoadingGuitarVstChain"),
        // #483: what the chord panel's scope switch reaches — "1 chord · 1 hand shape" is the common case.
        ("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords"),
        ("ChordTemplate_Preview_MarkedOne", "ChordTemplate_Preview_Marked"),
        ("ChordTemplate_Reach_HandShapesOne", "ChordTemplate_Reach_HandShapes"),
        // #477: the chord-shape palette reshaping the selection ("Reshaped 3 selected sounds to G.").
        ("ChordShapes_Status_ReshapedOne", "ChordShapes_Status_Reshaped"),
        // #506: the AI chart pass review card: a section row's changes and the checks' rejections.
        ("AiChartReview_AnchorsOne", "AiChartReview_Anchors"),
        ("AiChartReview_FingeringsOne", "AiChartReview_Fingerings"),
        ("AiChartReview_NamesOne", "AiChartReview_Names"),
        ("AiChartReview_FailedOne", "AiChartReview_Failed"),
        // #560: the AI chat's reply to an instruction ("I propose changes in 2 sections:").
        ("AiChat_Proposed_One", "AiChat_Proposed_Many"),
        // #232: the EOF import dialog's vocal track row ("PART VOCALS (42 lyrics)").
        ("Dialog_EofImport_VocalTrack_One", "Dialog_EofImport_VocalTrack_Many"),
        // #561: the chat's reply to a sections & phrases instruction, and the changes the checks refused.
        ("AiChat_StructureProposed_One", "AiChat_StructureProposed_Many"),
        ("AiStructure_FailedOne", "AiStructure_Failed"),
        // #803: the findings a pass or an Accept left in its range.
        ("AiChat_Leftovers_One", "AiChat_Leftovers_Many"),
        ("AiChat_TypedTargets_One", "AiChat_TypedTargets_Many"),
        // #614: the chat's reply to a Tones instruction, and the tones the gate refused.
        ("AiChat_TonesProposed_One", "AiChat_TonesProposed_Many"),
        ("AiTones_FailedOne", "AiTones_Failed"),
        // #314: the analysis monitor's per-string confidence tooltip ("over the last 5 judged notes").
        ("LivePlay_Monitor_StringConfidenceOne", "LivePlay_Monitor_StringConfidence"),
        // #618: the Issues tab's fix menu ("Re-finger 1-3, as 4 other chords play it").
        ("Inspector_IssueFix_RefingerOne", "Inspector_IssueFix_Refinger"),
        // #647: Chart from audio's merge — the dialog's held/occupied lines and the merge card's counts.
        ("ChartFromAudio_MergeHeldOne", "ChartFromAudio_MergeHeld"),
        ("ChartFromAudio_MergeAllOccupiedOne", "ChartFromAudio_MergeAllOccupied"),
        ("DraftMerge_CountOne", "DraftMerge_Count"),
        ("DraftMerge_OccupiedOne", "DraftMerge_Occupied"),
        // #707: the song-package export's stems line.
        ("Dialog_DcSong_Stems_DescOne", "Dialog_DcSong_Stems_Desc"),
        // #646: a Play Mode setlist's song count on Song Select.
        ("PlayMode_Setlist_SongsOne", "PlayMode_Setlist_SongsFmt"),
        // The AI tone confirmation: how many tones the model proposes.
        ("ToneGenerate_ConfirmOne", "ToneGenerate_Confirm"),
        // Linked measures: a group's length, the toast, the Inspector line, Link identical measures.
        ("LinkPanel_MeasuresOne", "LinkPanel_MeasuresFmt"),
        ("LinkToast_ChangedOne", "LinkToast_ChangedFmt"),
        ("LinkBanner_AppliesOne", "LinkBanner_AppliesFmt"),
        ("LinkIdentical_DoneOne", "LinkIdentical_DoneFmt"),
        // Linked measures: the import summary, the manager's suggestion rows and the Link all status.
        ("LinkSuggest_GroupsOne", "LinkSuggest_Groups"),
        ("LinkGroups_MeasuresOne", "LinkGroups_Measures"),
        ("Status_LinkSuggestionsLinkedOne", "Status_LinkSuggestionsLinked"),
        // Clean up shapes: the dialog's summary, its Apply caption and the status after Apply.
        ("ShapeCleanup_SummaryDuplicatesOne", "ShapeCleanup_SummaryDuplicates"),
        ("ShapeCleanup_ApplyOne", "ShapeCleanup_Apply"),
        ("ShapeCleanup_Status_AppliedOne", "ShapeCleanup_Status_Applied")
    ];

    public static TheoryData<string, string> Pairs
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (one, many) in PairList) data.Add(one, many);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void BothFormsExistInBothLanguages(string oneKey, string manyKey)
    {
        var differsSomewhere = false;

        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            var values = Values(file);
            foreach (var key in new[] { oneKey, manyKey })
            {
                Assert.True(values.ContainsKey(key),
                    $"{file} has no '{key}' — Plural() would render the KEY NAME on screen");
                Assert.False(string.IsNullOrWhiteSpace(values[key]),
                    $"{file}'s '{key}' is empty");
                // Argument ZERO is the count — Plural() supplies it, so a form
                // that does not print it drops the number silently. A format
                // SPECIFIER on it is fine and several use one ("{0:N0} songs").
                Assert.True(ArgZero.IsMatch(values[key]),
                    $"{file}'s '{key}' does not print {{0}} — Plural() passes the COUNT there, " +
                    "so the number would vanish from the message");
            }

            if (values[oneKey] != values[manyKey]) differsSomewhere = true;
        }

        // The pair has to EARN its existence somewhere — but not everywhere.
        // English is legitimately one-form for some of these ("12 day streak"
        // and "1 day streak" are both correct), while German is not; a pair
        // that is identical in BOTH languages is decorative and the caller
        // should just use `Format`.
        Assert.True(differsSomewhere,
            $"'{oneKey}' and '{manyKey}' are identical in every shipped language — " +
            "the pair buys nothing and the call site should use Format()");
    }

    /// <summary>
    ///     Japanese has no plural: every pair's two forms are the same text
    ///     there, so the check above cannot apply and "the pair differs" means
    ///     nothing. What a Japanese reader CAN be let down by is a form that is
    ///     missing, empty, drops the count, or is still the English sentence —
    ///     a key ja-JP lacks falls back to English at runtime, so "missing" and
    ///     "untranslated" look the same on screen. Each form is checked on its
    ///     own, against the English form of the SAME key.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void BothFormsAreTranslatedInJapanese(string oneKey, string manyKey)
    {
        var english = Values("Resources.resx");
        var japanese = Values("Resources.ja-JP.resx");

        foreach (var key in new[] { oneKey, manyKey })
        {
            Assert.True(japanese.TryGetValue(key, out var value),
                $"Resources.ja-JP.resx has no '{key}' — a Japanese user reads the English form");
            Assert.False(string.IsNullOrWhiteSpace(value), $"Resources.ja-JP.resx's '{key}' is empty");
            Assert.True(ArgZero.IsMatch(value),
                $"Resources.ja-JP.resx's '{key}' does not print {{0}} — Plural() passes the COUNT there, " +
                "so the number would vanish from the message");
            Assert.False(string.Equals(value, english[key], StringComparison.Ordinal),
                $"Resources.ja-JP.resx's '{key}' is the English text ('{value}') — it was never translated");
        }
    }

    /// <summary>One is one; everything else — including zero and a negative —
    /// takes the plural, which is what both two-form languages want.
    /// <para>
    ///     Through a lookup that answers with the KEY, so the two forms differ
    ///     by construction. Asked through <c>LocalizationService.Instance</c>,
    ///     this compared the output with the singular STRING, and with the
    ///     singleton left on ja-JP — where both forms are the same text — it
    ///     passed whatever the chooser picked.
    /// </para></summary>
    [Theory]
    [InlineData(1, "One")]
    [InlineData(0, "Many")]
    [InlineData(2, "Many")]
    [InlineData(-1, "Many")]
    public void TheChooserPicksTheSingularForExactlyOne(int count, string expectedKey)
    {
        Assert.Equal(expectedKey, LocalizationService.PluralKey("One", "Many", count));
        Assert.Equal($"{expectedKey}:{count}",
            LocalizationService.FormatPlural(key => key + ":{0}", CultureInfo.InvariantCulture, "One", "Many", count, []));
        // The test fake chooses the same way, or every view-model test written
        // against it pins a rule the app does not have.
        Assert.Equal($"{expectedKey}({count})", FakeLocalization.KeysOnly().Plural("One", "Many", count));
    }

    /// <summary>The count is passed as <c>{0}</c> by the chooser itself, and any
    /// extra arguments keep their order after it — the PSARC audio cache row is
    /// "{0} song, {1} of {2}". Read with the English table directly, never the
    /// singleton, so no other test's culture can change what is formatted.</summary>
    [Fact]
    public void TheCountIsArgumentZero_AndExtrasFollowIt()
    {
        var english = Values("Resources.resx");
        var text = LocalizationService.FormatPlural(key => english[key], CultureInfo.InvariantCulture,
            "Settings_PsarcAudioCache_UsageOne", "Settings_PsarcAudioCache_UsageFmt", 1, ["42 MB", "2 GB"]);

        Assert.Equal(string.Format(CultureInfo.InvariantCulture, english["Settings_PsarcAudioCache_UsageOne"], 1, "42 MB", "2 GB"), text);
        Assert.Contains("1", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("42 MB", StringComparison.Ordinal) < text.IndexOf("2 GB", StringComparison.Ordinal),
            $"extras out of order: '{text}'");
    }

    /// <summary>
    ///     The registry above only guards the pairs someone remembered to add to
    ///     it — which makes it exactly as strong as an agent's memory, and the
    ///     bug it exists to stop ("1 Tage Serie") is invisible in English. So
    ///     walk the OTHER way: every <c>Plural(oneKey, manyKey, …)</c> written in
    ///     <c>DotCharter.UI</c> or <c>DotCharter.Services</c> must appear in
    ///     <see cref="Pairs" />, or its two forms are never checked against
    ///     German at all.
    /// </summary>
    [Fact]
    public void EveryPluralCallSiteInTheApp_IsRegisteredInThePairsTable()
    {
        var registered = PairList.ToHashSet();

        var callSites = new HashSet<(string, string)>();
        var files = 0;
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone" })
        {
            foreach (var file in UiSourceTree.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                files++;
                foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(file),
                             @"\.Plural(?:With)?\s*\(\s*""(?<one>[A-Za-z0-9_.]+)""\s*,\s*""(?<many>[A-Za-z0-9_.]+)"""))
                {
                    callSites.Add((m.Groups["one"].Value, m.Groups["many"].Value));
                }
            }
        }

        // Non-vacuity: a regex over source that matched nothing would pass
        // forever. Both numbers are floors, not exact counts.
        Assert.True(files > 500, $"only {files} C# files scanned — the walk is not reaching the projects");
        Assert.True(callSites.Count >= 15,
            $"only {callSites.Count} Plural() call sites found — the pattern has gone blind");

        var unregistered = callSites.Except(registered)
            .Select(p => $"(\"{p.Item1}\", \"{p.Item2}\")")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(unregistered.Count == 0,
            "These Plural() call sites are not in PluralStringTests.Pairs, so nothing checks that " +
            "both forms exist in German (a missing one prints the KEY NAME):\n" +
            string.Join("\n", unregistered));
    }

    // Parsed once per file: the theories above read the same three tables for
    // every pair.
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Parsed = new(StringComparer.Ordinal);

    private static Dictionary<string, string> Values(string fileName) => Parsed.GetOrAdd(fileName, Parse);

    private static Dictionary<string, string> Parse(string fileName)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName);
        Assert.True(File.Exists(path), $"{fileName} moved — this guard is blind");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in XDocument.Load(path).Root!.Elements("data"))
        {
            var name = (string?)data.Attribute("name");
            if (name != null) map[name] = data.Element("value")?.Value ?? "";
        }

        Assert.True(map.Count > 3000, $"only {map.Count} keys parsed from {fileName}");
        return map;
    }
}

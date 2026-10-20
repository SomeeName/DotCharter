using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The old Settings ▸ General tab was the widest settings surface in the app
///     (11 titled groups, 54 <c>Grid.FormRow</c>s), and on 2026-09-22 it was SPLIT
///     into four screens: General, Editor, Play and Files &amp; backups. A split
///     is the change most likely to lose a row: a dropped option compiles, lays
///     out and reads as done while its config field, its save path and its
///     round-trip test all stay green.
///     <para>
///         So this pins the INVENTORY across the four views rather than their
///         layout. Moving a row between groups or tabs is free; losing one is
///         red, in both directions (a new binding must be added here). It is a
///         source lint, because an unvisited tab has no visual descendants.
///     </para>
/// </summary>
public sealed class GeneralSplitInventoryTests
{
    private static readonly string[] Views =
        ["GeneralSettingsView.axaml", "EditorSettingsView.axaml", "PlaySettingsView.axaml", "FilesSettingsView.axaml"];

    /// <summary>
    ///     Every binding path the four views carry, <c>DataTemplate</c> bodies
    ///     excluded. The old tab's 80 are all here under the contract's names —
    ///     the backup verbs moved onto <c>Backup.</c>, the startup choices into
    ///     the view — plus the search's <c>HiddenKeys</c>, the Advanced
    ///     disclosure and the two derived flags a row's enabled state reads.
    /// </summary>
    private static readonly string[] ExpectedBindings =
    [
        "AutoBackupEnabled", "AutoBackupIntervalMinutes", "AutoBackupKeepCount", "Backup.ExportCommand", "Backup.HasStatus",
        "Backup.ImportCommand", "Backup.IsBusy", "Backup.StatusText", "CaptureExternalConsoleLogs",
        "ClearLibraryAudioCacheCommand", "CoalesceRepeatedLogs", "CrashReportOptIn", "DefaultCharterName",
        "EnableConsoleColors", "EnableConsoleLogging", "EnableFileLogging", "GameplayClapsEnabled",
        "GameplayCountInIndex", "GameplayMetronomeEnabled", "GameplayRockMeterEnabled",
        "GpImportBeatTextTechniques", "GpImportDropMidBeatTempoChanges", "GpImportMarkersToSections",
        "GpImportPreserveShortRests", "GpImportSlideInFretDistance", "GpImportTimeSignatures",
        "GpImportTruncateShortChords", "GpImportTruncateShortNotes", "HasLibraryAudioCacheDetail", "HiddenKeys",
        "IsAdvancedOpen", "IsMinNoteDistanceInMilliseconds", "KeepPowerChordNames", "LanguageOptions",
        "LeadingSilenceSeconds", "LogApplication", "LogAudio", "LogEditing", "LogErrors", "LogEvents",
        "LogExternal", "LogFileMaxMb", "LogFileMaxRollovers", "LogFilePath", "LogHistory", "LogImport",
        "LogInput", "LogProject", "LogRepeatCoalesceWindowMs", "LogRepeatSummaryIntervalMs", "LogSettings",
        "LyricSpanPhraseGap", "LyricSpanWordGap", "MinNoteDistanceDivisor", "MinNoteDistanceModeIndex",
        "MinNoteDistanceMs", "MinimumLogLevelIndex", "MusicalChordNames", "OnlineLatinNameLookup", "PadSilenceOnNewProject",
        "PasteIncludeChords", "PasteIncludeEvents", "PasteIncludeFretAnchors", "PasteIncludeHandShapes",
        "PasteIncludeNotes", "PasteIncludeTones", "LibraryAudioCacheDetail", "LibraryAudioCacheHasEntries",
        "LibraryAudioCacheUsage", "PsarcOpenHardestLevelOnly", "RefreshLibraryAudioCacheCommand", "RotateLogFiles", "RunSetupAgainCommand",
        "SaveCompanionEof",
        "SaveCompanionEofAudio", "SaveCompanionPsarc", "SaveCompanionPsarcCodecIndex", "SaveCompanionRs2Dlc", "RsXmlSixStringProjection",
        "ExportGenerateDynamicDifficulty", "ExportDdUseMlModel", "ExportApplyDlcBuilderFixes",
        "SelectedLanguageIndex", "SelectedStartupScreenIndex", "SuppressDoubleStopChordNames",
        "SyncAnchorToChordFingering", "SyncHandShapeToChordTemplate", "TappedHarmonicFretOffset", "TrailingSilenceSeconds",
        "UseFastChartingInput"
    ];

    /// <summary>The old tab's eleven sections, now spread over four views. A regroup may move them; dropping one is red.</summary>
    private static readonly string[] ExpectedGroupTitleKeys =
    [
        "Settings_LanguageRegion_Title", "Settings_Diagnostics", "Settings_Logging",
        "Settings_ChartEditing", "Settings_CopyPaste_Title", "Settings_NewProject", "Settings_Import_Title",
        "Settings_Gameplay",
        "Settings_SaveCompanions", "Settings_AutoBackups", "Settings_PsarcAudioCache_Title", "Settings_PsarcOpen_Title"
    ];

    /// <summary>
    ///     Rows per view. 57 against the old 54: the log's two checkbox rows
    ///     ("Outputs", "Console") became four switches of their own, and Files gained
    ///     the auto-backup Keep row (#318). Editor's "Musical chord names" (#493)
    ///     makes 58. Files' three DLC Builder rows (#952) make 61.
    /// </summary>
    private static readonly Dictionary<string, int> ExpectedRows = new(StringComparer.Ordinal)
    {
        ["GeneralSettingsView.axaml"] = 17,
        ["EditorSettingsView.axaml"] = 26,
        ["PlaySettingsView.axaml"] = 4,
        ["FilesSettingsView.axaml"] = 15
    };

    [Fact]
    public void EveryOptionBinding_OfTheOldGeneralTab_IsStillBound()
    {
        var actual = Views.SelectMany(v => BindingPaths(Source(v))).ToHashSet(StringComparer.Ordinal);

        var lost = ExpectedBindings.Except(actual).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var added = actual.Except(ExpectedBindings).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.True(lost.Length == 0,
            "The split views no longer bind: " + string.Join(", ", lost) +
            ". A restructure must not drop an option — move the row, keep the binding.");
        Assert.True(added.Length == 0,
            "The split views bind new paths not in this test's list: " + string.Join(", ", added) +
            ". Add them to ExpectedBindings so the next restructure cannot lose them either.");
    }

    [Fact]
    public void EveryGroup_IsTitled_AndNoneWentMissing()
    {
        var titles = new List<string>();
        foreach (var view in Views)
        {
            var source = Source(view);
            var groups = Regex.Matches(source, "Classes=\"SettingsGroup[ \"]").Count;
            var found = Regex
                .Matches(source, "Classes=\"GroupTitle\"\\s+Text=\"\\{localization:Loc\\s+([A-Za-z0-9_]+)")
                .Select(m => m.Groups[1].Value)
                .ToArray();
            Assert.True(groups == found.Length, $"{view}: {groups} SettingsGroup(s) but {found.Length} GroupTitle(s)");
            titles.AddRange(found);
        }

        Assert.Equal(
            ExpectedGroupTitleKeys.OrderBy(k => k, StringComparer.Ordinal),
            titles.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    ///     A row outside a <c>Border.SettingsGroup</c> belongs to no group the
    ///     search can report, and a group with no row is a heading over nothing.
    ///     Both are what a hasty regroup produces.
    /// </summary>
    [Fact]
    public void NoRowSitsOutsideAGroup_AndNoGroupIsEmpty()
    {
        foreach (var view in Views)
        {
            var source = Source(view);
            var firstGroup = source.IndexOf("Classes=\"SettingsGroup", StringComparison.Ordinal);
            var firstRow = source.IndexOf("Classes=\"FormRow", StringComparison.Ordinal);
            Assert.True(firstGroup >= 0 && firstRow > firstGroup, $"{view}: a FormRow is declared before the first SettingsGroup.");

            var rowsPerGroup = source
                .Split(["Classes=\"SettingsGroup\"", "Classes=\"SettingsGroup First\""], StringSplitOptions.None)
                .Skip(1)
                .Select(chunk => Regex.Matches(chunk, "Classes=\"FormRow").Count)
                .ToArray();

            Assert.All(rowsPerGroup, count => Assert.True(count > 0, $"A SettingsGroup on {view} carries no FormRow."));
            Assert.True(rowsPerGroup.Sum() == ExpectedRows[view],
                $"{view} declares {rowsPerGroup.Sum()} rows; this inventory expects {ExpectedRows[view]}.");
        }
    }

    private static IReadOnlyCollection<string> BindingPaths(string source)
    {
        var withoutItemTemplates = Regex.Replace(source, "<DataTemplate.*?</DataTemplate>", string.Empty, RegexOptions.Singleline);
        return Regex
            .Matches(withoutItemTemplates, "\\{Binding\\s+(?:Path=)?!?([A-Za-z_][A-Za-z0-9_.]*)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Source(string view)
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", view));
        Assert.True(File.Exists(path), $"source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}

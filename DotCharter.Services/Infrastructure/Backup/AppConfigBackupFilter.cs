using System.Text.Json;
using System.Text.Json.Nodes;
using DotCharter.Services.Infrastructure.Recovery;

namespace DotCharter.Services.Infrastructure.Backup;

/// <summary>What <see cref="AppConfigBackupFilter.TryFilter" /> made of a backed-up
/// <c>appconfig.json</c>: the JSON to install, and the settings it did NOT take.</summary>
public sealed record AppConfigFilterResult(string Json, IReadOnlyList<string> NotRestored, bool Changed);

/// <summary>How a restore treats one <c>appconfig.json</c> setting.</summary>
public enum BackupSettingKind
{
    /// <summary>A plain preference, taken as it is.</summary>
    Value,

    /// <summary>A folder the user works in (library, project, export, last-used
    /// folders). Taken only when the value is a local, rooted, non-UNC, non-device
    /// path to a directory that exists on THIS machine.</summary>
    DataFolder,

    /// <summary>A list of project files (<c>RecentProjects</c>). Each entry is taken
    /// only when it is a plain local <c>.rscp</c> that exists on this machine.</summary>
    ExistingProjectFiles,

    /// <summary>Never taken: it names something the app executes, loads, opens at
    /// startup or sends a secret to. This machine's value stays.</summary>
    Never
}

/// <summary>
///     Decides which settings of a backed-up <c>appconfig.json</c> a restore may take.
///     <para>
///         A backup is a file the user RECEIVED, and <c>appconfig.json</c> carries
///         values the app turns into native code, file access or network traffic
///         without asking: the guitar VST chain and the instrument plug-in are loaded
///         at startup, the ffmpeg / yt-dlp paths and the Claude Code path are executed,
///         the last project is opened at startup, the AI endpoint receives the stored
///         API key. Restoring them verbatim lets the sender choose.
///     </para>
///     <para>
///         So this is an ALLOWLIST with a <see cref="BackupSettingKind" /> per key.
///         Anything that is not listed (a key this build has never heard of, a setting
///         added tomorrow) is treated as <see cref="BackupSettingKind.Never" />: it
///         keeps THIS machine's value, or stays absent on a machine that has none.
///         <c>ConfigBackupImportSecurityTests.EveryAppConfigSetting_HasExactlyOneKind</c>
///         fails until a new <c>AppConfig</c> property is classified here.
///     </para>
/// </summary>
public static class AppConfigBackupFilter
{
    /// <summary>Preferences, layout, rig and tone data. Nothing here names an
    /// executable, a plug-in, a location or an endpoint (<c>Ai</c> is allowed only
    /// minus <see cref="NestedNever" />).</summary>
    internal static readonly string[] ValueKeys =
    [
        "StartupScreen", "StartInPlayMode", "OnboardingCompleted", "OnboardingCompletedVersion", "LastVerifiedTuning", "AlwaysAskTuning",
        "LanguageCulture", "WasapiOutputDevice", "AsioOutputDevice", "InputDriver", "WasapiInputDevice",
        "AsioInputDevice", "LivePlayHitWindow", "LivePlayChordStrictness", "LivePlayStringAware",
        "LivePlayUseCqtVerifier", "LivePlayCqtDefaultMigrated", "LivePlayJudgeBends", "LivePlayJudgeSlides",
        "LivePlayJudgeVibrato", "LivePlayJudgeTechniqueContours", "LivePlayLatencyOffsetMs",
        "LivePlayAutoCalibrateTiming", "LivePlayLatencyOffsetsByDevice", "LivePlaySpeedLadderStartPercent",
        "LivePlaySpeedLadderStepPercent", "LivePlayCountInBeats", "LivePlaySpeedLadderEnabled",
        "LivePlayDynamicDifficulty", "GameplayCountInBeats", "GameplayMetronomeEnabled", "GameplayClapsEnabled",
        "GameplayResumeRewindMs", "GameplayRockMeterEnabled", "GameplayMuteMyInstrument", "GameplayDynamicStemMix",
        "StemSeparationSecondsPerAudioSecond", "UseInGameTones", "ShowGameplayToneHud", "ShowMinimap",
        "SongTonePreferences", "DropTunerSongs", "LivePlayTakeRecorderEnabled", "LivePlayTakeMaxMinutes",
        "LivePlayTakeCacheMaxMB", "StreamingMirrorDevice", "AutoFollowDeviceAudioSettings", "MidiOutputDeviceName",
        "ChartSynthPlaysOwnRig", "UseFastChartingInput", "SyncHandShapeToChordTemplate", "TappedHarmonicFretOffset",
        "SyncAnchorToChordFingering", "SuppressDoubleStopChordNames", "KeepPowerChordNames", "ChordNamingStyle",
        "AiFingeringJobGuitar", "AiFingeringJobBass", "LyricSpanWordGap", "LyricSpanPhraseGap", "MinNoteDistanceMode",
        "MinNoteDistanceMs", "MinNoteDistanceDivisor", "AutoBackupEnabled", "AutoBackupIntervalMinutes",
        "AutoBackupKeepCount", "RecoveryEnabled", "RecoveryAutosaveSeconds", "PadSilenceOnNewProject",
        "LeadingSilenceSeconds", "TrailingSilenceSeconds", "PasteIncludeNotes", "PasteIncludeChords",
        "PasteIncludeHandShapes", "PasteIncludeFretAnchors", "PasteIncludeEvents", "PasteIncludeTones",
        "Show3DFingering", "AnimatedHomeHero", "RenderScalePercent", "MaxFps", "IdleFps", "PresentMode",
        "PresentModeMailboxMigrated", "MsaaSamples", "Preview3DCameraFill", "EnableNoteHitExplosions",
        "EnableHighwayNearFade", "EnableShowlightBeams", "EnableShowlightFog", "EnableAnchorPreview",
        "EnableAnchorSweep", "AnchorPreviewLeadMs", "VisualLeadMs", "HighwayScrollSpeedOverrideEnabled",
        "HighwayScrollSpeed", "HighwayDrawDistanceZ", "EnableBeatPulse", "EnableDepthFog", "EnableVignette",
        "EnableEnhancedHitFeedback", "EnableCrowdBackground", "EnableHighwayLighting", "EnableBloom",
        "EnableStringShimmer", "EnableHitSparks", "EnableFilmicTonemap", "InvertStrings", "Timeline2DPolishEnabled",
        "Timeline2DLaneTint", "RibbonShowTones", "RibbonShowSections", "RibbonShowPhrases", "RibbonShowLyrics",
        "RibbonShowMeasureInfo", "RibbonShowLinks", "RibbonAutoHideEmpty", "RibbonCompactRows", "HtDemucsActiveModelId",
        "StemDefaultUpgradeDeclinedId", "StemSeparationExecutionProvider", "StemSeparationGpuAdapter",
        "ThemeColorOverrides", "StringColorPresetId", "UiScale", "UiScaleAuto", "LayoutDockHeight",
        "LayoutTimelineAreaHeight", "LayoutInspectorWidth", "LayoutAiChatVisible", "LayoutAiChatWidth",
        "LayoutSheetHeight", "SheetMusicZoom", "SheetMusicStaves", "SheetMusicPageMode", "DockLayout",
        "GpImportTruncateShortNotes", "GpImportTruncateShortChords", "GpImportPreserveShortRests",
        "OnlineLatinNameLookup", "GpImportBeatTextTechniques", "GpImportMarkersToSections",
        "GpImportSlideInFretDistance", "GpImportTimeSignatures", "GpImportDropMidBeatTempoChanges", "LastExportAppId",
        "ExportAudioVolume", "ExportPreviewVolume", "ExportIncludeVocals", "SaveCompanionEof",
        "SaveCompanionEofAudio", "PsarcOpenHardestLevelOnly", "SaveCompanionPsarc", "SaveCompanionPsarcCodec",
        "SaveCompanionRs2Dlc", "RsXmlSixStringProjection", "RsXmlMoverPhrases", "ExportGenerateDynamicDifficulty",
        "ExportApplyDlcBuilderFixes", "ExportDdLevelCountMode", "RocksmithKeys", "DefaultCharterName",
        "MatchPatterns", "MatchPatternsSeeded", "ActiveTonePresetId", "WindowWidth", "WindowHeight",
        "WindowMaximized", "UpdateCheckOnStartup", "AudioDriver", "AsioOutputChannelLeft", "AsioOutputChannelRight",
        "WasapiOutputDeviceName", "AsioOutputDeviceName", "EnableInput", "DropTunerEngine", "AsioInputChannelLeft",
        "AsioInputChannelRight", "AsioInputMono", "InputChannelsAreSeparateGuitars", "WasapiInputDeviceName",
        "AsioInputDeviceName", "EnableStreamingMirror", "StreamingMirrorDeviceName", "EnableMasterLimiter",
        "MasterLimiterCeilingDb", "AudioSampleRate", "AudioBufferFrames", "EnableHighProcessPriority", "AudioDelayMs",
        "MidiDelayMs", "MidiOutputDevice", "ChartSynthGuitarModel", "ChartSynthGuitarPickup",
        "ChartSynthGuitarCoilSplit", "ChartSynthBassModel", "ChartSynthBassPickup", "ChartSynthBassCoilSplit",
        "OutOfProcessVst", "EnableConsoleLogging", "EnableConsoleColors", "EnableFileLogging",
        "CaptureExternalConsoleLogs", "CoalesceRepeatedLogs", "LogRepeatCoalesceWindowMs",
        "LogRepeatSummaryIntervalMs", "RotateLogFiles", "LogFileMaxBytes", "LogFileMaxRollovers",
        "EnabledLogCategories", "MinimumLogLevel", "LogLevelOverrides", "UserTonePresets", "ActiveToneRig",
        "ActiveToneRigsByPlayer", "DefaultTonePresetId", "RestoreToneOnStartup", "PedalboardZoom",
        "RigMidiInputDevice", "RigMidiChannel", "RigMidiProgramChange", "RigMidiSceneCc", "RigSetlists",
        "RigActiveSetlistId", "RigWorkspaceHeight", "RigToneBrowserHeight", "RigFavoriteGear", "RigSpillover",
        "TunerReferenceHz", "TunerTuning", "InputPitchShiftSemis", "Ai"
    ];

    /// <summary>Folders the user works in; restored when they exist here.</summary>
    internal static readonly string[] DataFolderKeys =
    [
        "PsarcLibraryPath", "ExportTestFolder", "LastRscpDir", "LastAudioDir", "LastEofDir", "LastGpDir",
        "LastRsXmlDir", "LastMidiDir", "LastLintFolderDir", "LastProjectFolderDir", "LastExportDir",
        "LastAlbumArtDir"
    ];

    internal static readonly string[] ExistingProjectFileKeys = ["RecentProjects"];

    /// <summary>Reviewed and refused. Together with the other lists they must cover every
    /// <c>AppConfig</c> property.</summary>
    internal static readonly string[] NeverKeys =
    [
        // Native code loaded at startup.
        "GuitarVstChain", "LastGuitarVstPath", "LastInstrumentVstPath",
        // Executables the app starts.
        "FfmpegPath", "YtDlpPath",
        // Opened at startup / a file the app writes to.
        "LastProjectPath", "LogFilePath",
        // The user's own consents and identity: a received file does not give them.
        "YoutubeRightsAccepted", "CrashReportOptIn", "Tone3000ClientId", "StemCheckpointPickleConsent",
        // Forward-compat bag of keys this build does not know.
        "UnknownSettings"
    ];

    /// <summary>Members of an allowed object that are still <see cref="BackupSettingKind.Never" />:
    /// the AI provider kind and address decide where the stored API key is sent, or which
    /// executable runs (<c>BaseUrl</c> doubles as the Claude Code / Codex path).</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> NestedNever =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ai"] = ["Kind", "BaseUrl"]
        };

    private static readonly Dictionary<string, BackupSettingKind> Kinds = BuildKinds();

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>How the restore treats <paramref name="settingName" />; an unlisted
    /// name is <see cref="BackupSettingKind.Never" />.</summary>
    public static BackupSettingKind KindOf(string settingName)
        => Kinds.GetValueOrDefault(settingName, BackupSettingKind.Never);

    /// <summary>Every classified setting name (for the coverage test).</summary>
    internal static IReadOnlyCollection<string> ClassifiedNames => Kinds.Keys;

    private static Dictionary<string, BackupSettingKind> BuildKinds()
    {
        var kinds = new Dictionary<string, BackupSettingKind>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<string> names, BackupSettingKind kind)
        {
            foreach (var name in names) kinds.Add(name, kind); // a name in two lists throws at first use
        }

        Add(ValueKeys, BackupSettingKind.Value);
        Add(DataFolderKeys, BackupSettingKind.DataFolder);
        Add(ExistingProjectFileKeys, BackupSettingKind.ExistingProjectFiles);
        Add(NeverKeys, BackupSettingKind.Never);
        return kinds;
    }

    /// <summary>
    ///     Builds the <c>appconfig.json</c> to install from the backup's and the current
    ///     machine's. Returns <c>null</c> when the backup's file is not a JSON object (a
    ///     restore must not replace working settings with something unreadable).
    /// </summary>
    /// <param name="backupJson">The file as it came out of the archive.</param>
    /// <param name="currentJson">This machine's file, or <c>null</c> when it has none
    /// (or it is unreadable — then there is nothing to keep).</param>
    /// <param name="directoryExists">Seam for the folder check; defaults to the file system.</param>
    /// <param name="fileExists">Seam for the project-file check; defaults to the file system.</param>
    public static AppConfigFilterResult? TryFilter(
        string backupJson, string? currentJson,
        Func<string, bool>? directoryExists = null, Func<string, bool>? fileExists = null)
    {
        directoryExists ??= Directory.Exists;
        fileExists ??= File.Exists;

        var backup = TryParseObject(backupJson);
        if (backup == null) return null;
        var current = currentJson == null ? null : TryParseObject(currentJson);

        var result = new JsonObject();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notRestored = new List<string>();

        foreach (var (name, value) in backup)
        {
            var ours = Find(current, name)?.Value;
            JsonNode? accepted = null;
            var take = false;
            var report = false;

            switch (KindOf(name))
            {
                case BackupSettingKind.Value:
                    accepted = value?.DeepClone();
                    take = true;
                    break;
                case BackupSettingKind.DataFolder:
                    take = value is JsonValue v && v.TryGetValue<string>(out var folder) &&
                           IsLocalExistingFolder(folder, directoryExists);
                    accepted = take ? value!.DeepClone() : null;
                    report = !take && Carries(value, ours);
                    break;
                case BackupSettingKind.ExistingProjectFiles:
                    take = TryKeepExistingProjects(value, fileExists, out accepted);
                    report = Carries(value, ours) && (!take || ((JsonArray)value!).Count != ((JsonArray)accepted!).Count);
                    break;
                default:
                    report = Carries(value, ours);
                    break;
            }

            if (take)
            {
                result[name] = accepted;
                taken.Add(name);
            }

            if (report) notRestored.Add(name);
        }

        // This machine's own value for everything the backup did not (or could not) set.
        if (current != null)
        {
            foreach (var (name, value) in current)
            {
                if (!taken.Contains(name)) result[name] = value?.DeepClone();
            }
        }

        foreach (var (parent, members) in NestedNever)
            KeepNestedLocal(result, current, parent, members, notRestored);

        var changed = !JsonNode.DeepEquals(backup, result);
        return new AppConfigFilterResult(
            changed ? result.ToJsonString(WriteOptions) : backupJson,
            [.. notRestored.Distinct()],
            changed);
    }

    /// <summary>A rooted local path: no UNC (<c>\\server\share</c>), no device
    /// (<c>\\?\</c>, <c>\\.\</c>), no relative or dotted segments, and a directory that
    /// exists on this machine.</summary>
    internal static bool IsLocalExistingFolder(string? path, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        if (!Path.IsPathFullyQualified(path)) return false;
        if (path.IndexOf(':', 2) >= 0) return false;

        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment is "." or "..") return false;
        }

        return directoryExists(path);
    }

    private static bool TryKeepExistingProjects(JsonNode? value, Func<string, bool> fileExists, out JsonNode? kept)
    {
        kept = null;
        if (value is not JsonArray array) return false;

        var keep = new JsonArray();
        foreach (var entry in array)
        {
            if (Find(entry as JsonObject, "Path")?.Value is JsonValue p && p.TryGetValue<string>(out var path) &&
                RecoveredProjectPath.IsAdoptable(path) && fileExists(path))
            {
                keep.Add(entry!.DeepClone());
            }
        }

        if (keep.Count == 0) return false;
        kept = keep;
        return true;
    }

    private static void KeepNestedLocal(
        JsonObject result, JsonObject? current, string parent, string[] members, List<string> notRestored)
    {
        if (Find(result, parent)?.Value is not JsonObject target) return;
        var currentParent = Find(current, parent)?.Value as JsonObject;

        foreach (var member in members)
        {
            var theirs = Find(target, member);
            var ours = Find(currentParent, member);
            if (theirs is { } t)
            {
                if (Carries(t.Value, ours?.Value)) notRestored.Add($"{parent}.{member}");
                target.Remove(t.Key);
            }

            if (ours is { } o) target[o.Key] = o.Value?.DeepClone();
        }
    }

    private static KeyValuePair<string, JsonNode?>? Find(JsonObject? obj, string name)
    {
        if (obj == null) return null;
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair;
        }

        return null;
    }

    private static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether the backup actually carried something worth telling the user
    /// about: the app writes every setting, so an empty value, a "None" or the same
    /// value this machine already has is not news.</summary>
    private static bool Carries(JsonNode? theirs, JsonNode? ours)
        => HasValue(theirs) && !JsonNode.DeepEquals(theirs, ours);

    private static bool HasValue(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value when value.TryGetValue<string>(out var text)
            => !string.IsNullOrWhiteSpace(text) && !string.Equals(text, "None", StringComparison.Ordinal),
        _ => true
    };
}

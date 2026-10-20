namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>The six sections of the Play Mode settings modal, in rail order.
/// The enum IS the rail index — <c>PlayModeSettingsViewModel.GameplaySettingsSection</c>
/// is an <c>int</c> because the rail binds <c>SelectedIndex</c>, and casting one
/// place beats keeping two orders in step.</summary>
public enum GameplaySettingsSectionId
{
    Audio = 0,
    Devices = 1,
    Gameplay = 2,
    Graphics = 3,
    Appearance = 4,
    Keybinds = 5
}

/// <summary>
/// ONE row of the settings modal: which section it lives on, its label and —
/// always — the one sentence that says what changing it does.
///
/// <para><b><see cref="DescriptionKey" /> is not optional and there is no
/// overload that omits it.</b> §3.8 of the redesign makes "every
/// configuration-changing action states its consequence in one sentence next to
/// the control" a rule, and a settings screen is nothing but such actions. Making
/// the description a required constructor argument is what stops the rule rotting
/// one row at a time.</para>
/// </summary>
public sealed record GameplaySettingsEntry(
    GameplaySettingsSectionId Section,
    string TitleKey,
    string DescriptionKey);

/// <summary>One search result: the entry plus the strings it matched on, already
/// resolved, so the results list binds text rather than re-looking-up per
/// frame.</summary>
public sealed record GameplaySettingsHit(
    GameplaySettingsEntry Entry,
    string Title,
    string Description,
    string SectionName)
{
    public int SectionIndex => (int)Entry.Section;
}

/// <summary>
/// The Play Mode settings modal's row catalogue, and the search over it.
///
/// <para><b>Why a catalogue and not a tree walk.</b> The editor's Settings page
/// searches what each tab's view model declares. This modal's rows are
/// all declared inline in one region of <c>PlayModeView.axaml</c>, and a walk
/// there would have to be given a live view — which the default gate cannot
/// build. So the catalogue is the data, and
/// <c>GameplaySettingsCatalogTests</c> lints it against the real markup in BOTH
/// directions: every entry's title key must appear in the modal's XAML, and every
/// row title in that XAML must be in the catalogue. Two sources that are proven
/// equal are one source.</para>
///
/// <para>Pure: no Avalonia types, no view, no dispatcher. The resolver is
/// injectable so the tests can drive it against either resx without booting
/// anything.</para>
/// </summary>
public static class GameplaySettingsCatalog
{
    /// <summary>Every row the modal shows, in the order it shows them.</summary>
    public static IReadOnlyList<GameplaySettingsEntry> All { get; } =
    [
        // ── Audio ────────────────────────────────────────────────────────────
        new(GameplaySettingsSectionId.Audio, "PlayMode_GameSettings_MasterVolume", "PlayMode_GS_MasterVolume_Desc"),
        new(GameplaySettingsSectionId.Audio, "PlayMode_GameSettings_SongVolume", "PlayMode_GS_SongVolume_Desc"),
        new(GameplaySettingsSectionId.Audio, "PlayMode_GameSettings_InputVolume", "PlayMode_GS_InputVolume_Desc"),
        new(GameplaySettingsSectionId.Audio, "PlayMode_GameSettings_MonitorVolume", "PlayMode_GS_MonitorVolume_Desc"),
        new(GameplaySettingsSectionId.Audio, "PlayMode_GS_InputLevel", "PlayMode_GS_InputLevel_Desc"),
        new(GameplaySettingsSectionId.Audio, "Settings_GameplayMetronomeLabel", "PlayMode_GS_Metronome_Desc"),
        new(GameplaySettingsSectionId.Audio, "Settings_GameplayClapsLabel", "PlayMode_GS_Claps_Desc"),
        new(GameplaySettingsSectionId.Audio, "Settings_GameplayCountIn_RowLabel", "PlayMode_GS_CountIn_Desc"),

        // ── Devices ──────────────────────────────────────────────────────────
        new(GameplaySettingsSectionId.Devices, "PlayMode_GS_OutputDriver", "PlayMode_GS_OutputDriver_Desc"),
        new(GameplaySettingsSectionId.Devices, "PlayMode_GS_OutputDevice", "PlayMode_GS_OutputDevice_Desc"),
        new(GameplaySettingsSectionId.Devices, "PlayMode_GS_EnableInput", "PlayMode_GS_EnableInput_Desc"),
        new(GameplaySettingsSectionId.Devices, "PlayMode_GS_InputDriver", "PlayMode_GS_InputDriver_Desc"),
        new(GameplaySettingsSectionId.Devices, "PlayMode_GS_InputDevice", "PlayMode_GS_InputDevice_Desc"),
        new(GameplaySettingsSectionId.Devices, "PlayMode_GS_AutoFollow", "PlayMode_GS_AutoFollow_Desc"),
        new(GameplaySettingsSectionId.Devices, "PlayMode_GS_SampleRate", "PlayMode_GS_SampleRate_Desc"),
        new(GameplaySettingsSectionId.Devices, "PlayMode_GS_BufferSize", "PlayMode_GS_BufferSize_Desc"),
        new(GameplaySettingsSectionId.Devices, "PlayMode_GS_OutputLatency", "PlayMode_GS_OutputLatency_Desc"),

        // ── Gameplay ─────────────────────────────────────────────────────────
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_HitWindow", "PlayMode_GS_HitWindow_Desc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_ChordStrictness", "PlayMode_GS_ChordStrictness_Desc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_GameSettings_CqtVerifier",
            "PlayMode_GameSettings_CqtVerifier_Desc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_GameSettings_JudgeBends",
            "PlayMode_GameSettings_JudgeBends_Desc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_GameSettings_JudgeSlides",
            "PlayMode_GameSettings_JudgeSlides_Desc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_GameSettings_JudgeVibrato",
            "PlayMode_GameSettings_JudgeVibrato_Desc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_GameSettings_DetectionMonitor",
            "PlayMode_GameSettings_DetectionMonitor_Desc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_Speed", "PlayMode_GS_Speed_Desc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_GameSettings_ResumeRewind",
            "PlayMode_GameSettings_ResumeRewind_Desc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_RockMeter_SettingLabel", "PlayMode_RockMeter_SettingDesc"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_InGameTones_Use", "PlayMode_InGameTones_Tip"),
        new(GameplaySettingsSectionId.Gameplay, "PlayMode_ToneHud_Show", "PlayMode_ToneHud_Tip"),

        // ── Graphics ─────────────────────────────────────────────────────────
        new(GameplaySettingsSectionId.Graphics, "PlayMode_GameSettings_FpsCap", "PlayMode_GS_FpsCap_Desc"),
        new(GameplaySettingsSectionId.Graphics, "PlayMode_GameSettings_RenderScale", "PlayMode_GS_RenderScale_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_CameraDistance", "PlayMode_GS_CameraDistance_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_NoteHitExplosions",
            "PlayMode_GS_NoteHitExplosions_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_EnhancedHitFeedback",
            "PlayMode_GS_EnhancedHitFeedback_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_HitSparks", "PlayMode_GS_HitSparks_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_BeatPulse", "PlayMode_GS_BeatPulse_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_Bloom", "PlayMode_GS_Bloom_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_Vignette", "PlayMode_GS_Vignette_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_FilmicTonemap", "PlayMode_GS_FilmicTonemap_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_DepthFog", "PlayMode_GS_DepthFog_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_HighwayLighting",
            "PlayMode_GS_HighwayLighting_Desc"),
        new(GameplaySettingsSectionId.Graphics, "Settings_Graphics_StringShimmer", "PlayMode_GS_StringShimmer_Desc"),

        // ── Appearance ───────────────────────────────────────────────────────
        new(GameplaySettingsSectionId.Appearance, "Settings_Graphics_HighwayNearFade",
            "PlayMode_GS_HighwayNearFade_Desc"),
        new(GameplaySettingsSectionId.Appearance, "Settings_Graphics_AnchorPreview", "PlayMode_GS_AnchorPreview_Desc"),
        new(GameplaySettingsSectionId.Appearance, "Settings_Graphics_AnchorSweep", "PlayMode_GS_AnchorSweep_Desc"),
        new(GameplaySettingsSectionId.Appearance, "Settings_Graphics_ShowlightBeams",
            "PlayMode_GS_ShowlightBeams_Desc"),
        new(GameplaySettingsSectionId.Appearance, "Settings_Graphics_ShowlightFog", "PlayMode_GS_ShowlightFog_Desc"),
        new(GameplaySettingsSectionId.Appearance, "Settings_Graphics_CrowdBackground",
            "PlayMode_GS_CrowdBackground_Desc"),
        new(GameplaySettingsSectionId.Appearance, "Settings_Appearance_StringColorsTitle",
            "Settings_Appearance_StringColorsSubtitle"),
        new(GameplaySettingsSectionId.Appearance, "Settings_UiScale", "PlayMode_GS_UiScale_Desc"),

        // ── Keybinds ─────────────────────────────────────────────────────────
        new(GameplaySettingsSectionId.Keybinds, "Keybind_PlayPause", "PlayMode_GS_KbPlayPause_Desc"),
        new(GameplaySettingsSectionId.Keybinds, "Keybind_ToggleMetronome", "PlayMode_GS_KbMetronome_Desc"),
        new(GameplaySettingsSectionId.Keybinds, "Keybind_ToggleClaps", "PlayMode_GS_KbClaps_Desc"),
        new(GameplaySettingsSectionId.Keybinds, "Keybind_SpeedUp", "PlayMode_GS_KbSpeedUp_Desc"),
        new(GameplaySettingsSectionId.Keybinds, "Keybind_SpeedDown", "PlayMode_GS_KbSpeedDown_Desc"),
        new(GameplaySettingsSectionId.Keybinds, "Keybind_SetLoopStart", "PlayMode_GS_KbLoopStart_Desc"),
        new(GameplaySettingsSectionId.Keybinds, "Keybind_SetLoopEnd", "PlayMode_GS_KbLoopEnd_Desc"),
        new(GameplaySettingsSectionId.Keybinds, "Keybind_ToggleLoop", "PlayMode_GS_KbToggleLoop_Desc"),
        new(GameplaySettingsSectionId.Keybinds, "Keybind_SeekStart", "PlayMode_GS_KbSeekStart_Desc"),
        new(GameplaySettingsSectionId.Keybinds, "Keybind_SeekEnd", "PlayMode_GS_KbSeekEnd_Desc")
    ];

    /// <summary>The nav rail, in order. Each item carries a description — the
    /// mockup puts one under EVERY nav item, and §6.5 lists that as one of the
    /// three things new about this screen.</summary>
    public static IReadOnlyList<GameplaySettingsSectionId> Sections { get; } =
    [
        GameplaySettingsSectionId.Audio, GameplaySettingsSectionId.Devices,
        GameplaySettingsSectionId.Gameplay, GameplaySettingsSectionId.Graphics,
        GameplaySettingsSectionId.Appearance, GameplaySettingsSectionId.Keybinds
    ];

    public static string SectionTitleKey(GameplaySettingsSectionId section) => section switch
    {
        GameplaySettingsSectionId.Audio => "PlayMode_GameSettings_AudioSection",
        GameplaySettingsSectionId.Devices => "PlayMode_GS_DevicesSection",
        GameplaySettingsSectionId.Gameplay => "PlayMode_GameSettings_GameplaySection",
        GameplaySettingsSectionId.Graphics => "PlayMode_GameSettings_GraphicsSection",
        GameplaySettingsSectionId.Appearance => "PlayMode_GameSettings_AppearanceSection",
        _ => "PlayMode_GS_KeybindsSection"
    };

    /// <summary>The one line under the nav item — and, on the content header,
    /// the subtitle. Same string in both places on purpose: the rail promises
    /// what the pane delivers.</summary>
    public static string SectionDescriptionKey(GameplaySettingsSectionId section) => section switch
    {
        GameplaySettingsSectionId.Audio => "PlayMode_GS_AudioSection_Desc",
        GameplaySettingsSectionId.Devices => "PlayMode_GS_DevicesSection_Desc",
        GameplaySettingsSectionId.Gameplay => "PlayMode_GS_GameplaySection_Desc",
        GameplaySettingsSectionId.Graphics => "PlayMode_GS_GraphicsSection_Desc",
        GameplaySettingsSectionId.Appearance => "PlayMode_GS_AppearanceSection_Desc",
        _ => "PlayMode_GS_KeybindsSection_Desc"
    };

    /// <summary>Every localization key this catalogue depends on — the two per
    /// row plus the two per section. What the "both resx files carry it" guard
    /// enumerates.</summary>
    public static IEnumerable<string> AllKeys()
    {
        foreach (var entry in All)
        {
            yield return entry.TitleKey;
            yield return entry.DescriptionKey;
        }

        foreach (var section in Sections)
        {
            yield return SectionTitleKey(section);
            yield return SectionDescriptionKey(section);
        }
    }

    /// <summary>
    /// Filters the catalogue by a free-text query, matching the row's TITLE, its
    /// DESCRIPTION and its SECTION NAME — all three, because a settings search
    /// that only reads labels cannot find "restarts the audio engine", which is
    /// the sentence the user actually remembers.
    /// </summary>
    /// <param name="query">Raw user input. Empty/whitespace returns nothing:
    /// "no query" is not "everything", it is the un-searched screen.</param>
    /// <param name="resolve">Key → display string: the screen's localization, or
    /// a test's, so the match can be driven against either resx with no Avalonia
    /// platform.</param>
    public static List<GameplaySettingsHit> Search(string? query, Func<string, string> resolve)
    {
        var hits = new List<GameplaySettingsHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;

        var needle = query.Trim();

        foreach (var entry in All)
        {
            var title = resolve(entry.TitleKey);
            var description = resolve(entry.DescriptionKey);
            var section = resolve(SectionTitleKey(entry.Section));

            if (!Contains(title, needle) && !Contains(description, needle) && !Contains(section, needle))
                continue;

            hits.Add(new GameplaySettingsHit(entry, title, description, section));
        }

        return hits;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack != null && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}

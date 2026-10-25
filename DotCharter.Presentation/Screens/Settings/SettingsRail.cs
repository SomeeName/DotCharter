namespace DotCharter.Presentation.Screens.Settings;

/// <summary>The rail keys. A tab's <see cref="ISettingsTab.Key" /> is one of these, and its rail row is in <see cref="SettingsRail" />.</summary>
public static class SettingsTabKeys
{
    public const string General = "general";
    public const string Editor = "editor";
    public const string Play = "play";
    public const string Appearance = "appearance";
    public const string Graphics = "graphics";
    public const string Audio = "audio";
    public const string Files = "files";
    public const string Keybinds = "keybinds";
    public const string StemModels = "stemModels";
    public const string LyricModels = "lyricModels";
    public const string Tone3000 = "tone3000";
    public const string Ai = "ai";
    public const string Updates = "updates";
}

public enum SettingsRailSection
{
    Workspace,
    System,
    Configuration
}

/// <summary>One rail row: where it sits and what it says. The keys are localization keys.</summary>
public sealed record SettingsRailEntry(
    string Key,
    SettingsRailSection Section,
    string TitleKey,
    string DescriptionKey,
    string IconKey);

/// <summary>
///     The rail, declared once: the order the tabs appear in, the eyebrow each
///     run sits under, and each row's title, description and glyph. A tab the
///     app registers must have a row here; the shell refuses one that does not.
/// </summary>
public static class SettingsRail
{
    public static IReadOnlyList<SettingsRailEntry> Entries { get; } =
    [
        new(SettingsTabKeys.General, SettingsRailSection.Workspace,
            "Settings_GeneralTab", "Settings_GeneralTab_Desc", "IconCog"),
        new(SettingsTabKeys.Editor, SettingsRailSection.Workspace,
            "Settings_EditorTab", "Settings_EditorTab_Desc", "IconPencil"),
        new(SettingsTabKeys.Play, SettingsRailSection.Workspace,
            "Settings_PlayTab", "Settings_PlayTab_Desc", "IconPlay"),
        new(SettingsTabKeys.Appearance, SettingsRailSection.Workspace,
            "Settings_AppearanceTab", "Settings_AppearanceTab_Desc", "IconPalette"),
        new(SettingsTabKeys.Graphics, SettingsRailSection.System,
            "Settings_GraphicsTab", "Settings_GraphicsTab_Desc", "IconMonitor"),
        new(SettingsTabKeys.Audio, SettingsRailSection.System,
            "Settings_AudioTab", "Settings_AudioTab_Desc", "IconVolumeHigh"),
        new(SettingsTabKeys.Files, SettingsRailSection.System,
            "Settings_FilesTab", "Settings_FilesTab_Desc", "IconFolder"),
        new(SettingsTabKeys.Keybinds, SettingsRailSection.Configuration,
            "Settings_KeybindsTab", "Settings_KeybindsTab_Desc", "IconKeyboard"),
        new(SettingsTabKeys.Tone3000, SettingsRailSection.Configuration,
            "Tone3000_Title", "Tone3000_Subtitle", "IconAmp"),
        new(SettingsTabKeys.Ai, SettingsRailSection.Configuration,
            "Settings_AiTab", "Settings_AiTab_Desc", "IconSparkles"),
        new(SettingsTabKeys.Updates, SettingsRailSection.Configuration,
            "Settings_UpdatesTab", "Settings_UpdatesTab_Desc", "IconUpdate")
    ];

    public static string SectionTitleKey(SettingsRailSection section) => section switch
    {
        SettingsRailSection.Workspace => "Settings_Section_Workspace",
        SettingsRailSection.System => "Settings_Section_System",
        _ => "Settings_Section_Configuration"
    };

    /// <summary>
    ///     Tabs drawn as a section of another tab's page, keyed by the hosted tab:
    ///     no rail row of their own, but still loaded, saved, reverted and counted
    ///     on their own. Their view sits in the host's view, and a search hit on
    ///     one of their rows lands on the host's page.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Hosted { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SettingsTabKeys.StemModels] = SettingsTabKeys.Ai,
        [SettingsTabKeys.LyricModels] = SettingsTabKeys.Ai
    };

    public static SettingsRailEntry? Find(string key) =>
        Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    /// <summary>The rail row a tab is shown under: its own, or its host's.</summary>
    public static string PageOf(string key) => Hosted.GetValueOrDefault(key, key);

    /// <summary>The tabs in rail order, each hosted tab after its host. Throws for a tab with no rail
    /// row and no host — that is a missing line, not a hidden tab.</summary>
    public static List<ISettingsTab> Order(IEnumerable<ISettingsTab> tabs)
    {
        var byKey = new Dictionary<string, ISettingsTab>(StringComparer.Ordinal);
        foreach (var tab in tabs)
        {
            if (Find(PageOf(tab.Key)) is null)
                throw new InvalidOperationException($"Settings tab '{tab.Key}' has no row in SettingsRail.");
            if (!byKey.TryAdd(tab.Key, tab))
                throw new InvalidOperationException($"Settings tab '{tab.Key}' is registered twice.");
        }

        return
        [
            .. Entries.SelectMany(e => Hosted.Where(h => h.Value == e.Key).Select(h => h.Key).Prepend(e.Key))
                .Where(byKey.ContainsKey).Select(k => byKey[k])
        ];
    }
}

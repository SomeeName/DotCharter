namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The rows this tab declares to the cross-tab search, and the one place
///     that decides whether each is on screen. A row is visible when the search
///     keeps it AND the chosen provider wants it, so the view binds one flag per
///     row and asks nothing.
/// </summary>
public sealed partial class AiSettingsViewModel
{
    public IReadOnlyList<SettingsSearchGroup> SearchGroups =>
    [
        new(_localization["Ai_Title"], _localization["Ai_Subtitle"],
        [
            new("ai.provider", _localization["Ai_ProviderLabel"], _localization["Ai_ProviderDesc"]),
            new("ai.model", _localization["Ai_ModelLabel"], _localization["Ai_ModelDesc"]),
            new("ai.effort", _localization["Ai_EffortLabel"], _localization["Ai_EffortDesc"]),
            new("ai.cliPath", _localization["Ai_CliPathLabel"], _localization["Ai_CliPathDesc"]),
            new("ai.baseUrl", _localization["Ai_BaseUrlLabel"], _localization["Ai_BaseUrlDesc"]),
            new("ai.apiKey", _localization["Ai_ApiKeyLabel"], _localization["Ai_ApiKeyDesc"]),
            new("ai.useLibraryTones", _localization["Ai_UseLibraryTonesLabel"], _localization["Ai_UseLibraryTonesDesc"]),
            new("ai.webResearch", _localization["Ai_WebResearchLabel"], _localization["Ai_WebResearchDesc"]),
            new("ai.test", _localization["Ai_TestLabel"], _localization["Ai_TestDesc"])
        ]),
        new(_localization["Settings_Ai_MatchModel_Title"], _localization["Settings_Ai_MatchModel_Subtitle"],
        [
            new("ai.matchModel", _localization["Settings_Ai_MatchModel_StatusLabel"]),
            new("ai.matchLibrary", _localization["Settings_Ai_MatchLibrary_StatusLabel"])
        ])
    ];

    public void ApplySearch(string query)
    {
        _query = query;
        var groups = SearchGroups;
        var has = query.Length > 0;

        var kept = Kept(groups[0], query, has);
        IsProviderRowVisible = kept[0];
        IsModelRowVisible = kept[1] && IsEnabledProvider;
        IsEffortRowVisible = kept[2] && IsEnabledProvider;
        var shape = Shape;
        IsCliPathRowVisible = kept[3] && shape.ShowCliPath;
        IsBaseUrlRowVisible = kept[4] && shape.ShowBaseUrl;
        IsApiKeyRowVisible = kept[5] && shape.ShowKey;
        IsUseLibraryTonesRowVisible = kept[6] && IsEnabledProvider;
        IsWebResearchRowVisible = kept[7] && shape.ShowWebResearch;
        IsTestRowVisible = kept[8] && IsEnabledProvider;

        IsClaudeCliNoteVisible = IsModelRowVisible && shape.ShowClaudeCliNote;
        IsCodexCliNoteVisible = IsModelRowVisible && shape.ShowCodexCliNote;
        AreClaudeModelPresetsVisible = IsModelRowVisible && shape.ShowClaudePresets;
        AreCodexModelPresetsVisible = IsModelRowVisible && shape.ShowCodexPresets;
        IsKeySessionOnlyNoteVisible = IsApiKeyRowVisible && KeyIsSessionOnly;
        IsProviderGroupVisible = !has || kept.Any(v => v);

        var match = Kept(groups[1], query, has);
        _match.SetRowVisibility(match[0], match[1], !has || match.Any(v => v));
    }

    private static List<bool> Kept(SettingsSearchGroup group, string query, bool has)
    {
        var groupMatches = has && SettingsSearch.GroupMatches(group, query);
        return group.Rows.Select(r => !has || groupMatches || SettingsSearch.RowMatches(r, query)).ToList();
    }
}

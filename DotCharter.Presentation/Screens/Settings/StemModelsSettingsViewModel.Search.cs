namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The rows this screen declares to the cross-tab search, and the one place
///     that decides which of its six groups are on screen. Each block owns its
///     own flags, so the view binds one flag per row and asks nothing.
/// </summary>
public sealed partial class StemModelsSettingsViewModel
{
    public IReadOnlyList<SettingsSearchGroup> SearchGroups =>
    [
        new(_localization["Settings_StemModels_Title"], _localization["Settings_StemModels_Subtitle"],
        [
            new("stemModels.folder", _localization["Settings_StemModels_ModelsFolder"])
        ]),
        new(_localization["Settings_StemModels_GpuTitle"], _localization["Settings_StemModels_GpuInfo"],
        [
            new("stemModels.executionProvider", _localization["Settings_StemModels_ExecutionProvider"]),
            new("stemModels.gpuAdapter", _localization["Settings_StemModels_GpuAdapter"],
                _localization["Settings_StemModels_GpuAdapterDesc"]),
            new("stemModels.backendStatus", _localization["Settings_StemModels_RefreshStatus"])
        ]),
        new(_localization["Settings_StemModels_GpuAccelTitle"], _localization["Settings_StemModels_GpuAccelSubtitle"],
        [
            new("stemModels.gpuRuntime", _localization["Settings_StemModels_GpuAccelStatusLabel"])
        ]),
        new(_localization["Settings_StemModels_CacheTitle"], _localization["Settings_StemModels_CacheSubtitle"],
        [
            new("stemModels.cacheUsage", _localization["Settings_StemModels_CacheUsageLabel"],
                _localization["Settings_StemModels_CacheClear"])
        ]),
        new(_localization["Settings_StemModels_CustomTitle"], _localization["Settings_StemModels_CustomSubtitle"],
        [
            new("stemModels.customImport", _localization["Settings_StemModels_CustomTitle"],
                _localization["Settings_StemModels_CustomSubtitle"])
        ]),
        new(_localization["Settings_StemModels_AvailableTitle"], _localization["Settings_StemModels_AvailableSubtitle"],
        [
            new("stemModels.catalog", _localization["Settings_StemModels_AvailableTitle"],
                _localization["Settings_StemModels_AvailableSubtitle"])
        ])
    ];

    public void ApplySearch(string query)
    {
        var groups = SearchGroups;
        var has = query.Length > 0;

        var folder = Kept(groups[0], query, has);
        IsFolderGroupVisible = !has || folder[0];

        var backend = Kept(groups[1], query, has);
        _backend.SetRowVisibility(backend[0], backend[1], backend[2], !has || backend.Any(v => v));

        var gpu = Kept(groups[2], query, has);
        _gpu.SetRowVisibility(gpu[0], !has || gpu[0]);

        var cache = Kept(groups[3], query, has);
        _cache.SetRowVisibility(cache[0], !has || cache[0]);

        IsCustomGroupVisible = !has || Kept(groups[4], query, has)[0];
        IsCatalogGroupVisible = !has || Kept(groups[5], query, has)[0];
    }

    private static List<bool> Kept(SettingsSearchGroup group, string query, bool has)
    {
        var groupMatches = has && SettingsSearch.GroupMatches(group, query);
        return group.Rows.Select(r => !has || groupMatches || SettingsSearch.RowMatches(r, query)).ToList();
    }
}

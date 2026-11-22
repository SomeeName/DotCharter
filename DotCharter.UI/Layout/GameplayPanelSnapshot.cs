namespace DotCharter.UI.Layout;

/// <summary>The editor pane choices a run restores after its temporary visibility overrides.</summary>
internal readonly record struct GameplayPanelSnapshot(
    bool InspectorVisible,
    bool PreviewVisible,
    bool StemMixerVisible,
    bool SheetMusicVisible,
    bool PhraseCatalogVisible,
    bool AiChatVisible)
{
    /// <summary>Reset shows Inspector and Preview while preserving the other pre-run choices.</summary>
    public GameplayPanelSnapshot Reset() => this with { InspectorVisible = true, PreviewVisible = true };
}

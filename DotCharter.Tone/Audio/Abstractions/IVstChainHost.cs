namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The third-party plugin host: the guitar VST3 chain, the instrument VSTi,
///     their editor windows and their load state. One of the focused seams
///     <see cref="IAudioPlaybackService" /> is made of
///     (docs/agents/services/audio-engine.md, "Focused interfaces for view
///     models").
///     <para>
///         <b>Threading.</b> An event here may arrive on the caller's thread or
///         on a worker (the plugin load task, the out-of-process sandbox's pipe
///         reader) — marshal before touching UI state. A load returns before the
///         plugin is up; its result is the matching <c>…LoadChanged</c> event.
///     </para>
/// </summary>
public interface IVstChainHost
{
    bool IsVstEffectEnabled { get; set; }
    double VstEffectVolume { get; set; }
    bool IsVstInstrumentEnabled { get; set; }
    double VstInstrumentVolume { get; set; }

    /// <summary>Raised when the instrument VST3 finishes loading (or fails).
    /// <c>bool</c> argument indicates whether a plugin is currently loaded.</summary>
    event EventHandler<bool>? InstrumentVstLoadChanged;

    /// <summary>Raised when a guitar chain slot finishes loading.
    /// <c>Guid</c> is the slot id, <c>bool</c> indicates whether the slot now
    /// has a live plugin handle.</summary>
    event EventHandler<(Guid SlotId, bool Loaded)>? GuitarVstSlotLoadChanged;

    /// <summary>Raised (from a non-UI thread) when the user closes a guitar
    /// chain slot's plugin editor window with its native close button, so the
    /// UI's editor-open toggle can be cleared instead of going stale.</summary>
    event EventHandler<Guid>? GuitarVstSlotEditorClosed;

    /// <summary>Raised (from a non-UI thread) when the user closes the
    /// instrument VSTi's editor window with its native close button.</summary>
    event EventHandler? InstrumentVstEditorClosed;

    /// <summary>True while the instrument VSTi has a live plugin handle.</summary>
    bool IsInstrumentVstLoaded { get; }

    /// <summary>True while an instrument VSTi load is queued or in flight.</summary>
    bool IsInstrumentVstLoading { get; }

    /// <summary>True if the named guitar chain slot has a live plugin handle.</summary>
    bool IsGuitarVstSlotLoaded(Guid id);

    List<VstPluginInfo> ScanVstPlugins();
    void SetGuitarVstChain(IReadOnlyList<VstChainEntry> entries);
    void ToggleGuitarVstSlotEditor(Guid id, bool open);
    void SetGuitarVstSlotBypass(Guid id, bool bypass);
    void LoadInstrumentVst(string pluginPath, bool openEditorAfterLoad = false);
    void ToggleInstrumentVstEditor(bool open);
}

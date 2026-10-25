using System.ComponentModel;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Graphics: what the renderer does (scale, frame cap, present
///     mode, MSAA), how the highway reads (anchor preview, latency trim, pace,
///     draw distance, camera), the 2D timeline's look, and — behind the
///     Advanced disclosure — the cosmetic effects.
///     <para>
///         <b>Every setting here applies LIVE</b>, so the user can judge a change
///         on screen before keeping it; the footer's Save persists it and
///         Cancel reverts both the value and what is being drawn. The member
///         names are the old page's, one for one, so the row markup ports
///         across unchanged.
///     </para>
///     <para>
///         Search hides rows through <see cref="HiddenKeys" /> — one set of row
///         and group keys rather than a flag per row. A tab with thirty-odd rows
///         would otherwise carry forty members that exist only to say "this one
///         is filtered out".
///     </para>
/// </summary>
public interface IGraphicsSettingsScreen : INotifyPropertyChanged
{
    // ── display ──────────────────────────────────────────────────────────

    double RenderScalePercent { get; set; }

    IReadOnlyList<IGraphicsChoice> MaxFpsOptions { get; }

    IGraphicsChoice? SelectedMaxFps { get; set; }

    /// <summary>The paused 3D loop's trickle rate, in Hz.</summary>
    double IdleFps { get; set; }

    /// <summary>Only modes the device reports — plus the persisted one, so a Save never clobbers it.</summary>
    IReadOnlyList<IGraphicsChoice> PresentModeOptions { get; }

    IGraphicsChoice? SelectedPresentMode { get; set; }

    IReadOnlyList<IGraphicsChoice> MsaaOptions { get; }

    IGraphicsChoice? SelectedMsaa { get; set; }

    // ── readability ──────────────────────────────────────────────────────

    bool EnableAnchorPreview { get; set; }

    bool EnableAnchorSweep { get; set; }

    double AnchorPreviewLeadMs { get; set; }

    /// <summary>Display-latency compensation, in ms, trimmed by eye while a song plays.</summary>
    double VisualLeadMs { get; set; }

    /// <summary>The slider's top end, from the renderer, so the view states no limit of its own.</summary>
    double MaxVisualLeadMs { get; }

    /// <summary>Off: every chart scrolls the way its charter set it. On: <see cref="HighwayScrollSpeed" /> wins.</summary>
    bool HighwayScrollSpeedOverrideEnabled { get; set; }

    /// <summary>Rocksmith units — LOWER IS FASTER. <see cref="HighwayVisibleSecondsLabel" /> is what makes that legible.</summary>
    double HighwayScrollSpeed { get; set; }

    double HighwayDrawDistanceZ { get; set; }

    /// <summary>"2.3 s" of chart on screen — the one number the two sliders above actually mean.</summary>
    string HighwayVisibleSecondsLabel { get; }

    /// <summary>Drag right and the board pulls away: the INVERSE of the engine's fill.</summary>
    double CameraDistancePercent { get; set; }

    bool Show3DFingering { get; set; }

    // ── advanced: highway effects ────────────────────────────────────────

    bool EnableHighwayNearFade { get; set; }

    bool EnableNoteHitExplosions { get; set; }

    bool EnableEnhancedHitFeedback { get; set; }

    bool EnableHitSparks { get; set; }

    bool EnableBeatPulse { get; set; }

    bool EnableDepthFog { get; set; }

    bool EnableVignette { get; set; }

    bool EnableHighwayLighting { get; set; }

    bool EnableBloom { get; set; }

    bool EnableStringShimmer { get; set; }

    bool EnableFilmicTonemap { get; set; }

    // ── advanced: showlights and background ──────────────────────────────

    bool EnableShowlightBeams { get; set; }

    bool EnableShowlightFog { get; set; }

    bool EnableCrowdBackground { get; set; }

    // ── search ───────────────────────────────────────────────────────────

    /// <summary>
    ///     The Advanced disclosure. Two-way with its header, and forced open by
    ///     the search while a query keeps any row inside it — a hit the user
    ///     cannot see is not a hit.
    /// </summary>
    bool IsAdvancedOpen { get; set; }

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }
}

/// <summary>One entry in a graphics choice list: what the user reads.</summary>
public interface IGraphicsChoice
{
    string Label { get; }
}

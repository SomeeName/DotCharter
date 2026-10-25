namespace DotCharter.Services.State;

public sealed class UserSettingsState
{
    public bool Is3DPreviewVisible { get; set; } = true;
    public KeybindConfig Keybinds { get; set; } = new();
    public double PixelsPerMs { get; set; } = 0.2;
    public bool ShowWaveform { get; set; } = true;

    /// <summary>The dashed suggested hand-shape bars on the timeline.</summary>
    public bool ShowSuggestedHandShapes { get; set; } = true;

    // Advanced 3D preview visuals (Vulkan).
    public bool EnableNoteHitExplosions { get; set; } = true;
    public bool EnableHighwayNearFade { get; set; } = true;
    public bool EnableShowlightBeams { get; set; } = true;
    public bool EnableShowlightFog { get; set; } = true;

    // 0.14.2 gameplay-readability aids + visual effects (Settings → Graphics).
    // Persisted in AppConfig; mirrored here at startup and on Settings save
    // because the 3D snapshot reads UserSettings each frame.
    public bool EnableAnchorPreview { get; set; } = true;
    public bool EnableAnchorSweep { get; set; } = true;
    public int AnchorPreviewLeadMs { get; set; } = 1000;

    /// <summary>
    ///     The EFFECTIVE highway scroll speed being drawn right now — already
    ///     resolved (per-playthrough > global override > the chart's own value >
    ///     Rocksmith's 1.3 default) by whoever started the run.
    ///     <b>Rocksmith units: LOWER IS FASTER.</b>
    ///     Defaults are literals rather than HighwaySpeedPolicy constants only
    ///     because Services cannot reference Rendering; the UI clamps through
    ///     the policy on apply, exactly as it does for AnchorPreviewLeadMs.
    /// </summary>
    public double HighwayScrollSpeed { get; set; } = 1.3;

    /// <summary>How far down the board the player can see, in world units.</summary>
    public double HighwayDrawDistanceZ { get; set; } = 35.0;

    /// <summary>3D preview camera fill (percent of the tuned default; higher =
    /// board fills more / camera closer). The UI clamps through
    /// GraphicsSettingsPolicy on apply, exactly as it does for the values above
    /// (Services cannot reference Rendering, so the literal default lives here).</summary>
    public int Preview3DCameraFill { get; set; } = 100;
    public bool EnableBeatPulse { get; set; } = true;
    public bool EnableDepthFog { get; set; } = true;
    public bool EnableVignette { get; set; } = true;
    public bool EnableEnhancedHitFeedback { get; set; } = true;
    public bool EnableCrowdBackground { get; set; } = true;

    // 0.15.6 highway fidelity (plan 5.6): in-shader directional lighting.
    public bool EnableHighwayLighting { get; set; } = true;

    // 0.15.6 highway fidelity (plan 5.5): bloom post chain (default off).
    public bool EnableBloom { get; set; }

    // 0.15.6 highway fidelity (plan 5.7): stateless, Live-Play-suppressed.
    public bool EnableStringShimmer { get; set; } = true;
    public bool EnableHitSparks { get; set; } = true;

    /// <summary>Left-handed mode (v0.34): mirrors the 3D highway fret axis and
    /// the 2D lanes. PROFILE-level — mirrored here from the active
    /// <c>PlayerProfile.LeftyFlip</c> (startup + ActiveProfileChanged +
    /// toggle), never persisted in AppConfig.</summary>
    public bool LeftyFlip { get; set; }

    /// <summary>The editor's String order: <c>true</c> is Tab (low string at the bottom). It
    /// flips the VERTICAL string order of the 2D timeline and the editor's 3D preview.
    /// Independent of <see cref="LeftyFlip"/> (a HORIZONTAL fret-axis mirror in 3D); in the 2D
    /// lanes the two compose (XOR). AppConfig display setting, mirrored here — default OFF.</summary>
    public bool InvertStrings { get; set; }

    /// <summary>Play Mode's String order, for the highway while a run is presenting.</summary>
    public bool PlayModeInvertStrings { get; set; }

    /// <summary>A Play Mode run is presenting on the editor's 3D surface (the shell's
    /// gameplay presentation): the highway takes <see cref="PlayModeInvertStrings" />.</summary>
    public bool GameplayPresentation { get; set; }

    /// <summary>The 3D highway's string order this frame: Play Mode's during a run, the editor's otherwise.</summary>
    public bool HighwayInvertStrings => GameplayPresentation ? PlayModeInvertStrings : InvertStrings;

    // 0.15.6 highway fidelity: composite-time filmic tonemap (default off).
    public bool EnableFilmicTonemap { get; set; }
}

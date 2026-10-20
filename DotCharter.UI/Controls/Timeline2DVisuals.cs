namespace DotCharter.UI.Controls;

/// <summary>
///     Process-wide visual toggles for the 2D editor timeline
///     (Settings → Graphics → 2D timeline, 0.14.3). Mirrors the
///     <c>Preview3DPacing</c> static-with-setter channel so the settings view
///     model and the timeline render path stay decoupled: the layers read the
///     volatile fields once per frame, the UI thread writes them at startup
///     and on Settings load/save/live-preview. Persisted in
///     <c>AppConfig.Timeline2DPolishEnabled</c> / <c>Timeline2DLaneTint</c>.
/// </summary>
public static class Timeline2DVisuals
{
    private static volatile bool _polishEnabled = true;
    private static volatile bool _laneTintEnabled;

    /// <summary>Master toggle for the 0.14.3 decorative pass (gem
    /// gradients/rims, waveform gradient body, beat-grid fade ramp, playhead
    /// glow boost + scrub trail, ribbon chip shadows). Default on; off
    /// restores the pre-0.14.3 flat look.</summary>
    public static bool PolishEnabled
    {
        get => _polishEnabled;
        set => _polishEnabled = value;
    }

    /// <summary>Whisper-faint per-string lane wash. Default off; the layer
    /// additionally suppresses it in Marks LOD regardless of this flag.</summary>
    public static bool LaneTintEnabled
    {
        get => _laneTintEnabled;
        set => _laneTintEnabled = value;
    }
}

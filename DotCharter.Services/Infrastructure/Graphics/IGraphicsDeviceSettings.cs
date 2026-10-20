namespace DotCharter.Services.Infrastructure.Graphics;

/// <summary>
///     What the renderer can do and the three knobs a settings screen turns.
///     Every member is a primitive on purpose — the capability lists are already
///     ids and sample counts, so nothing here needs the rendering assembly.
///     <para>
///         It exists because that assembly is referenced by <c>DotCharter.UI</c>
///         alone: a settings screen in <c>DotCharter.Presentation</c> cannot name
///         <c>IVulkanRenderService</c> at all. Same shape as
///         <see cref="DotCharter.Services.Editing.IChartEditorSurface" /> — the
///         layer below declares the seam, the implementation stays where it is.
///     </para>
/// </summary>
public interface IGraphicsDeviceSettings
{
    /// <summary>Present-mode ids the device actually supports, in the order it reports them.</summary>
    IReadOnlyList<string> SupportedPresentModes { get; }

    /// <summary>MSAA sample counts the device supports.</summary>
    IReadOnlyList<int> SupportedMsaaSamples { get; }

    void SetPresentMode(string presentMode);

    void SetMsaaSamples(int samples);

    void SetRenderScalePercent(int percent);

    /// <summary>The MSAA level a fresh install gets, for a device that reports it.</summary>
    int DefaultMsaaSamples { get; }

    /// <summary>The largest display-latency trim, in ms, the highway accepts.</summary>
    double MaxVisualLeadMs { get; }

    /// <summary>The 3D loop's frame cap while playing; 0 is uncapped.</summary>
    void SetMaxFps(int fps);

    /// <summary>The 3D loop's trickle rate while paused.</summary>
    void SetIdleFps(int fps);

    /// <summary>The 2D timeline's two visual toggles. Takes effect on the next frame.</summary>
    void SetTimelineVisuals(bool polish, bool laneTint);

    /// <summary>Display-latency compensation, live, so it can be trimmed by eye while a song plays.</summary>
    void SetVisualLeadTrimMs(double ms);

    /// <summary>The 3D fingering overlay. Applied on SAVE rather than live, as it always was.</summary>
    void SetFingeringOverlay(bool enabled);
}

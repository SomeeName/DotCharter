using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>
///     A renderer that can render on the CALLER's thread.
///     <para>
///         <see cref="IRigRenderer.RenderAsync" /> hops to the thread pool, which
///         is right for a match (the caller is the UI) and wrong for the library's
///         background growth: a pool thread runs at Normal priority and there is
///         no way to lower it from outside. The grower therefore runs on its own
///         <see cref="ThreadPriority.BelowNormal" /> thread and, when the renderer
///         offers this, renders THERE — so 2.8 hours of offline DSP yields to
///         everything the player is actually doing. A renderer without it is
///         still grown, just at Normal.
///     </para>
/// </summary>
public interface ISynchronousRigRenderer : IRigRenderer
{
    /// <summary>Renders <paramref name="di" /> through <paramref name="rig" /> on
    /// the calling thread. Same result as <see cref="IRigRenderer.RenderAsync" />.</summary>
    /// <param name="di">The dry take.</param>
    /// <param name="rig">The rig.</param>
    /// <param name="ct">Checked before the render starts.</param>
    MonoAudio Render(MonoAudio di, DcRigPreset rig, CancellationToken ct);
}

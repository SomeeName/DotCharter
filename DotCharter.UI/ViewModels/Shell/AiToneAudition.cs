// The AI chat's Tones review: Audition — a proposed tone heard on the rig over its own section,
// through the editor's tone preview and the transport's one-pass audition. Nothing is written.
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>Plays a proposed tone body over a span of the song through the rig, and stops doing so.</summary>
public interface IAiToneAudition
{
    /// <summary>
    ///     Puts <paramref name="json" /> on the rig in place of tone <paramref name="key" />'s committed body
    ///     and plays <paramref name="fromMs" />–<paramref name="toMs" /> once. Null when it plays; otherwise
    ///     why it cannot (localized), and nothing changed.
    /// </summary>
    string? Start(string key, string json, double fromMs, double toMs);

    /// <summary>The committed bodies again, and the preview toggle as the charter left it. Idempotent.</summary>
    void Stop();
}

/// <summary>
///     The shipping audition, over the two paths the editor already has: the chart tone preview's
///     unsaved-body seam (<see cref="ChartTonePreviewController.PreviewToneEdit" />, the Tone designer's
/// live edit) and the transport's one-pass span audition (<c>EditorLoopService.BeginAudition</c>).
///     <para>
///         <b>Heard through the rig, so through what drives it</b>: the live guitar (the preview toggle,
///         switched on for the audition when it was off and back off after), and the chart synth when it
/// plays through the chart's tones. The song audio itself never goes through the rig. A
///         tone the project has no body for yet cannot be previewed (the preview converts the project's
///         definitions) — the row says so instead of looking live.
///     </para>
/// </summary>
public sealed class ChartToneAudition : IAiToneAudition
{
    private readonly Func<ChartTonePreviewController?> _preview;
    private readonly Func<double, double, bool> _playOnce;
    private readonly ILocalizationService _loc;
    private ChartTonePreviewController? _active;
    private bool _turnedOn;

    /// <param name="preview">The editor's tone preview, built on first use.</param>
    /// <param name="playOnce">Plays a span once (seek + one-pass loop + play); false when it could not.</param>
    /// <param name="loc">The refusal's words.</param>
    public ChartToneAudition(Func<ChartTonePreviewController?> preview, Func<double, double, bool> playOnce,
        ILocalizationService loc)
    {
        _preview = preview;
        _playOnce = playOnce;
        _loc = loc;
    }

    /// <inheritdoc />
    public string? Start(string key, string json, double fromMs, double toMs)
    {
        Stop();
        if (_preview() is not { } preview) return _loc["AiChartReview_AuditionUnavailable"];
        if (!preview.IsAvailable) return preview.Tip;

        preview.PreviewToneEdit(key, json);
        _active = preview;
        if (!preview.IsOn && !preview.IsDriving)
        {
            if (!preview.TurnOn())
            {
                Stop();
                return preview.Tip;
            }

            _turnedOn = true;
        }

        if (_playOnce(fromMs, toMs)) return null;
        Stop();
        return _loc["AiChartReview_AuditionUnavailable"];
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_active is not { } preview) return;
        _active = null;
        preview.EndToneEditPreview();
        if (_turnedOn) preview.TurnOff();
        _turnedOn = false;
    }
}

using DotCharter.Services.Editing;

namespace DotCharter.Services.State;

public sealed class EditorUiState
{
    /// <summary>
    /// The persistent per-string edit mask (v0.35, Stage 1 of the editing
    /// rework) — EOF's <c>eof_pro_guitar_fret_bitmask</c> promoted out of the
    /// chord inspector's checkboxes into editor state. EMPTY = every string, so
    /// it is a strict no-op until the user presses <c>Ctrl+1..9</c>.
    /// Deliberately NOT persisted: like the grid divisor it is a working mode,
    /// and a mask silently restored at launch would make the first keyboard
    /// edit of a session act on a subset for no visible reason.
    /// </summary>
    public StringEditMask StringEditMask { get; set; } = StringEditMask.Empty;

    /// <summary>
    /// The editor's A/B loop region (v0.35, Stage 3) — null until the user has
    /// punched both points in. It is the SAME <c>LivePlayLoop</c> Live Play's
    /// practice repeater uses; only the driver differs. Deliberately NOT
    /// persisted: like the string mask and the grid divisor it is a working
    /// mode, and a loop silently restored at launch would trap the transport in
    /// a region the user does not remember marking.
    /// </summary>
    public Audio.LivePlay.LivePlayLoop? LoopRegion { get; set; }

    /// <summary>Whether the region above actually loops the transport. Kept
    /// separate from the region so toggling off does not throw the punch points
    /// away.</summary>
    public bool LoopEnabled { get; set; }

    /// <summary>Punch-in armed but not yet closed (loop start pressed, loop end
    /// not). Drawn as a single marker so a half-set loop is visible rather than
    /// silently pending.</summary>
    public double? LoopPendingStartMs { get; set; }

    /// <summary>True while the region is a ONE-SHOT audition of the selection
    /// rather than a repeating loop: the pass ends by seeking back to the start
    /// and pausing.</summary>
    public bool LoopIsAudition { get; set; }

    public string CurrentLyricDisplay { get; set; } = "";
    public string CurrentPhraseDisplay { get; set; } = "";
    public string CurrentSectionDisplay { get; set; } = "";
    public string MainDisplayMessage { get; set; } = "";

    /// <summary>How <see cref="MainDisplayMessage" /> reads on the rail. Written
    /// together with the message by <c>ProjectState.SetMessage</c>; a plain
    /// message assignment resets it to <see cref="StatusSeverity.Info" /> so an
    /// old error's colour never outlives its text.</summary>
    public StatusSeverity MainDisplaySeverity { get; set; } = StatusSeverity.Info;
    public int PreviewAnchorFret { get; set; }
    public int PreviewNoteFret { get; set; }
    public double LastPointerTimeMs { get; set; }

    /// <summary>
    /// When true (the default) the 2D timeline viewport is locked to the
    /// playhead — the pre-0.16 behaviour. When false the viewport pans freely
    /// via <see cref="TimelineViewAnchorMs"/> without seeking the transport.
    /// Deliberately NOT persisted: Follow always starts ON.
    /// </summary>
    public bool TimelineFollowsPlayhead { get; set; } = true;

    /// <summary>
    /// The chart time (ms) displayed at the fixed anchor screen X
    /// (ChartEditorConstants.PlayheadX) while the viewport is NOT following
    /// the playhead — the "ViewOffsetMs" of the 0.16 viewport decoupling.
    /// Only meaningful while <see cref="TimelineFollowsPlayhead"/> is false.
    /// </summary>
    public double TimelineViewAnchorMs { get; set; }

    /// <summary>
    /// Last known width (px) of the main 2D timeline renderer, published by
    /// ChartRenderer on resize so viewport-center zoom anchoring works from
    /// commands that have no visual-tree access (keybinds, toolbar). 0 =
    /// unknown (fall back to the PlayheadX anchor column).
    /// </summary>
    public double TimelineViewportWidthPx { get; set; }

    /// <summary>
    /// The anchor time the main 2D renderer actually DREW on its last frame,
    /// published once per frame by ChartRenderer while playing. NaN = "no live
    /// frame" (paused, or before the first frame), which is the only state in
    /// which <see cref="ProjectState.TimelineViewAnchorEffectiveMs"/> falls back
    /// to the coalesced playhead.
    /// <para>
    /// It exists because the renderer resolves its playhead from the LIVE audio
    /// clock plus the visual lead, while the coalesced
    /// <c>Playback.CurrentTimeMs</c> lags both — up to ~100 ms, i.e. 100 px at
    /// 1 px/ms, for the entire duration of playback. Hit-testers that mapped
    /// through the coalesced value put clicks on the wrong chart time.
    /// </para>
    /// </summary>
    public double TimelineRenderedAnchorMs { get; set; } = double.NaN;
}

using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Data;
using DotCharter.Core.Graphics;

namespace DotCharter.Rendering.Scene;

public class RenderContext
{
    public FrameSnapshot Snapshot { get; set; } = null!;
    public float BoardWidth { get; set; }
    public double TimeMs { get; set; }
    public QuadBatcher QuadBatcher { get; set; } = null!;
    public TextBatcher TextBatcher { get; set; } = null!;
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }

    /// <summary>Upcoming-anchor lookup (0.14.2 B), computed ONCE per frame by
    /// <c>Fretboard3DEngine.RenderFrame</c> via the pure
    /// <see cref="NextAnchorWindow" /> and shared by the fret-number preview
    /// and the floor sweep. <see cref="AnchorPreviewInfo.None" /> when both
    /// toggles are off or no shift is inside the lead window.</summary>
    public AnchorPreviewInfo AnchorPreview { get; set; }

    // ---- deferred 3D text ----------------------------------------------------
    // A geometry pass that knows WHERE a label goes may not stage it: the text
    // batcher is still pointed at the previous frame's ortho HUD matrix until the
    // Ui pass. It enqueues here instead, and BendLabelComponent drains it from
    // UiElements. See Deferred3DLabel for the full reason.
    //
    // The array is owned by the context, which Fretboard3DEngine reuses across
    // frames (`_frameContext`), so the steady state allocates nothing.

    private Deferred3DLabel[] _deferred3DLabels = new Deferred3DLabel[128];

    /// <summary>How many labels the geometry passes deferred this frame.</summary>
    public int Deferred3DLabelCount { get; private set; }

    /// <summary>The deferred labels, valid until the next <see cref="ClearDeferred3DLabels" />.</summary>
    public ReadOnlySpan<Deferred3DLabel> Deferred3DLabels => _deferred3DLabels.AsSpan(0, Deferred3DLabelCount);

    /// <summary>
    ///     Queue in-world text for the Ui pass. Call this from an opaque or
    ///     transparent component instead of <c>TextBatcher.DrawText3D</c>, which
    ///     would silently draw nothing from there.
    /// </summary>
    public void Defer3DLabel(in Deferred3DLabel label)
    {
        if (Deferred3DLabelCount == _deferred3DLabels.Length)
            Array.Resize(ref _deferred3DLabels, _deferred3DLabels.Length * 2);
        _deferred3DLabels[Deferred3DLabelCount++] = label;
    }

    /// <summary>
    ///     Reset the queue. The engine calls this once per frame while it sets the
    ///     context up — BEFORE the geometry passes fill it — so a frame that never
    ///     reaches the Ui pass cannot leak its labels into the next one.
    /// </summary>
    public void ClearDeferred3DLabels() => Deferred3DLabelCount = 0;

    // ---- highway geometry (was compile-time constants) -----------------------
    // Derived once per frame by Fretboard3DEngine.RenderFrame from the
    // snapshot's scroll speed + draw distance through HighwaySpeedPolicy. Every
    // renderer reads these instead of FretboardConstants, which is what lets the
    // user change the pace and the board length at runtime.

    /// <summary>World units per millisecond of chart.</summary>
    public float TimeToZScale { get; set; } = HighwaySpeedPolicy.BaseTimeToZScale;

    /// <summary>The board's far edge — the cull distance for every element.</summary>
    public float FadeEndZ { get; set; } = HighwaySpeedPolicy.DefaultDrawDistanceZ;

    /// <summary>Where the distance fade / depth fog begins.</summary>
    public float FadeStartZ { get; set; } =
        HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.DefaultDrawDistanceZ);

    /// <summary>How many milliseconds of chart are on the board. The single
    /// look-ahead notion: renderers must window against THIS, never against a
    /// hardcoded literal.</summary>
    public float HorizonMs { get; set; } =
        HighwaySpeedPolicy.HorizonMs(1f, HighwaySpeedPolicy.DefaultDrawDistanceZ);

    /// <summary>Depth of an object at <paramref name="objectTime"/> given the
    /// current playhead. Replaces <c>FretboardConstants.GetZDepthForTime</c>,
    /// which folded a compile-time scale.</summary>
    public float GetZDepthForTime(double objectTime, double currentTimeMs) =>
        (float)(objectTime - currentTimeMs) * TimeToZScale;

    /// <summary>The depth a <paramref name="ms"/>-away object sits at — the
    /// runtime form of the old <c>FadedDistanceZ</c>/<c>CloseDistanceZ</c>
    /// constants, which were "50 ms" and "250 ms" expressed in world units.</summary>
    public float ZForMs(float ms) => ms * TimeToZScale;

    /// <summary>
    ///     Lead time of the pre-note anticipation cue, capped to a fraction of
    ///     the horizon. The cue is time-based (500 ms) and so is nominally
    ///     speed-independent — but on the fastest highway a note is only on
    ///     screen for ~875 ms, and a cue covering 57% of a note's visible life
    ///     reads as a strobe, not a warning. At the default settings the cap is
    ///     inactive (0.35 × 1750 = 612 ms > 500), so this changes nothing until
    ///     the player actually speeds the highway up.
    /// </summary>
    public float AnticipationMs => MathF.Min(FretboardConstants.AnticipationWindow, HorizonMs * 0.35f);
}

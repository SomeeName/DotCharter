using Avalonia.Media;

namespace DotCharter.Rendering.Data;

public static class FretboardConstants
{
    // ==========================================
    // CAMERA TWEAKING VARIABLES
    // ==========================================
    public const float CameraPitch = 0.00f; // Tilt up/down (rotX). Increase for a more top-down view.
    public const float CameraYaw = 0.03f; // Pan left/right (rotY). Set to 0.0f to look perfectly straight ahead.
    public const float CameraRoll = 0.0f; // Tilt sideways (rotZ).
    public const float CameraBaseHeight = 5.0f; // Base Y position. Increase to move the camera higher.

    public const float
        CameraBaseDistance = -2.5f; // Base Z position. Make more negative to pull the camera further back.
    // ==========================================

    public const float ChartboardYPosition = 0.0f;
    public const float StringDistance = 0.35f;
    public const float BottomStringPosition = ChartboardYPosition + StringDistance;
    public const float FretThickness = 0.02f;
    public const float StringThickness = 0.015f;
    public const float NoteHalfWidth = 0.6f; // Fixed standard size
    public const float TailHalfWidth = NoteHalfWidth * 0.35f; // Increased width for technique weight
    public const float TailGlowHalfWidth = TailHalfWidth * 2.5f;
    public const float NoteHeadOffset = -0.03f;
    /// <summary>How long before a note arrives its anticipation cue flashes.
    /// Time-based, so it is speed-independent by construction — but on a very
    /// fast highway a note is only on screen for ~875 ms, so the cue is capped
    /// to a fraction of the horizon at the call site rather than strobing.</summary>
    public const float AnticipationWindow = 500f;

    // Scroll speed, the time→Z scale, the visibility window and the fade
    // distances are deliberately not compile-time constants — which is what lets
    // the highway's pace be changed. They are derived per frame
    // from the user's settings by HighwaySpeedPolicy and reach the renderers on
    // the RenderContext (TimeToZScale / FadeStartZ / FadeEndZ / HorizonMs /
    // ZForMs). Nothing may reintroduce a constant here: a const folds into every
    // call site and silently ignores the setting.

    // Linear Spacing Constants
    public const float LinearFretWidth = 1.0f;

    public static readonly Color[] StringColors =
    [
        Color.Parse("#FF3333"), Color.Parse("#FFD700"), Color.Parse("#3388FF"),
        Color.Parse("#FF8C00"), Color.Parse("#22CC22"), Color.Parse("#9933FF"),
        Color.Parse("#C8C8C8"), Color.Parse("#787878")
    ];

    /// <summary>
    ///     String <paramref name="stringIndex" />'s colour on an instrument of
    ///     <paramref name="stringCount" /> strings — THE lookup every 3D surface uses, so a
    ///     7-string's low E stays red and its low B is grey. See
    ///     <see cref="StringColorSlots" />.
    /// </summary>
    public static Color StringColorFor(int stringIndex, int stringCount)
        => StringColors[StringColorSlots.For(stringIndex, stringCount)];

    /// <summary>
    ///     Maps a string index to its VERTICAL row in the highway string stack
    ///     (the row then becomes a world Y via
    ///     <c>BottomStringPosition + row * StringDistance</c>). The default puts
    ///     StringIndex 0 (low E) at the TOP; <paramref name="invertStrings" />
    ///     flips to the standard-tab layout (low E at the bottom). THE one place
    ///     the 3D string→row order is decided — every per-string Y must resolve
    ///     through it so note heads, sustains, chords, the string lines and the
    ///     hit effects can never disagree. Mirrors the 2D
    ///     <c>ChartRenderContext.LaneTopFor</c> convention.
    /// </summary>
    public static int StringLaneIndex(int stringIndex, int stringCount, bool invertStrings)
        => invertStrings ? stringIndex : stringCount - 1 - stringIndex;

    public static float GetLinearFretPosition(int fret) => fret * LinearFretWidth;

    public static float GetLinearFretMiddle(int fret) => (fret - 0.5f) * LinearFretWidth;
}

namespace DotCharter.Rendering.Data;

/// <summary>
///     Constants for note and chord rendering behavior
/// </summary>
public static class RenderingConstants
{
    // Segment counts for rendering techniques
    public const int TechniqueSegmentCount = 48;
    public const int BasicSegmentCount = 2;

    // Vibrato effect parameters
    public const float VibratoFrequencyHz = 80.0f;
    public const float VibratoAmplitude = 0.12f;
    public const float VibratoShineWidth = 0.02f;
    public const float VibratoShineZOffset = 0.001f;
    public const byte VibratoShineAlpha = 200;

    // Tremolo effect parameters
    public const float TremoloAmplitude = 0.08f;

    // How long a vibrato / tremolo eases in or out at a link to a tail without it
    // (NoteFlags.RampIn / RampOut), capped at half the tail.
    public const double LinkRampMs = 60.0;

    // Bend rendering
    public const float BendMultiplierFactor = 0.5f; // Multiplied by StringDistance

    // Slide easing
    public const float SlideEasePower = 3.0f;

    // Chord box rendering
    public const float ChordEdgeThickness = 0.075f;
    public const float ChordBoxPadding = 0.01f;
    public const float ChordTextOffsetY = 0.12f;
    public const float ChordTextSize = 0.09f;

    // Alpha values
    public const byte TailInnerAlpha = 192;
    public const byte BarAlpha = 255;

    /// <summary>
    ///     Shader mode for an open-string bar (and its landing cue): 1, solid
    ///     colour. It was a constant named <c>NoteHeadZOffset = 5</c> passed as the
    ///     vertex TYPE, i.e. mode 5 — the chord-pane mode with the strike-line
    ///     near-fade — so every open note faded to ~10 % exactly as it had to be
    ///     played, and its landing cue on the board never showed at all.
    /// </summary>
    public const float OpenNoteBarVertexType = 1.0f;

    // Z-depth offsets
    public const float ChordOutlineZOffset = 0.001f;

    // Time conversion
    public const float MillisecondsToSeconds = 1000.0f;
    public const double VisibleRangeBufferMs = 5000.0;

    // Tail width multipliers
    public const float TailHalfWidthMultiplier = 0.5f;
    public const float OpenTailStartHalfWidthMultiplier = 0.7f;
    public const float OpenTailEndHalfWidthMultiplier = 0.45f;
    public const byte OpenTailEdgeAlpha = 235;

    // Fingering rendering
    public const float FingeringSize = 0.16f;
    public const float FingeringZOffset = 0.01f;
    public const float FingeringVerticalOffset = 0.3f;

    // Fret anchor rendering
    public const double AnchorTailMaxMs = 5000.0;
}

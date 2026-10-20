namespace DotCharter.Rendering.Data.Render;

public struct RenderAnchor
{
    public double Time;
    public double EndTime;
    public byte Fret;
    public byte Width;
    public bool IsSelected;
    public bool IsHovered;

    /// <summary>A pending change removes this anchor (the AI chart pass review):
    /// <see cref="DotCharter.Rendering.Data.SnapshotUpdateContext.EditTargetAnchors" />.</summary>
    public bool IsEditTarget;
}

public struct RenderBeat
{
    public double Time;
    public bool IsMeasure;
    public bool IsAnchor;
    public double Bpm;
    public int BeatsInMeasure;
    public int NoteDenominator;
    public int MeasureNumber;
}

public struct RenderHandShape
{
    public double Time;
    public double EndTime;
    public int TemplateId;
    public bool IsSelected;
    public bool IsHovered;

    /// <summary>
    ///     What the bar prints: a committed bar's template NAME (resolved by id in
    ///     the snapshot), a PREVIEW bar's <c>HandShape.Name</c> — the grip a bar
    ///     being drawn will carry, whose template may not exist yet. Null when
    ///     there is nothing to print. The layer has ONE text path for both.
    /// </summary>
    public string? Label;

    /// <summary>The bar's template is an arpeggio by EITHER flag
    /// (<c>ChordTemplate.IsArpeggio</c>, resolved in the snapshot).</summary>
    public bool IsArpeggio;

    /// <summary>The inspector reports E05 or D09 against this shape — copied from
    /// <see cref="SnapshotUpdateContext.HandShapeFindings" />, never derived here.
    /// The 2D bar wears the warning style.</summary>
    public bool HasFinding;

    /// <summary>A SUGGESTED bar — a run of notes one grip holds, which no bar covers
    /// yet. Never a committed shape: the 2D layer draws it dashed, and a click accepts it.</summary>
    public bool IsSuggested;
    /// <summary>A pending bulk chord edit would change this shape — the chord
    /// panel's preview, copied from <see cref="SnapshotUpdateContext.EditTargetHandShapes" />.</summary>
    public bool IsEditTarget;

    /// <summary>The GHOST gems of the bar's grip (<c>ChordTemplate.GhostMask</c>): bit n =
    /// string n holds <see cref="GhostFret" />(n) without playing it. The 2D layer draws each one
    /// dimmed at the bar's start. Strings 0–7 only (eight bits of fret each).</summary>
    public int GhostMask;

    /// <summary>Eight bits of fret per ghost string, string n at bits 8n..8n+7.</summary>
    public ulong GhostFrets;

    /// <summary>Ghost strings a PLAYED gem also sits on at the bar's start (a tapped harmonic over
    /// its own held fret): the layer draws those ghosts just before the start so neither hides.</summary>
    public int GhostNudgeMask;

    public readonly int GhostFret(int stringIndex) =>
        stringIndex is >= 0 and < MaxGhostStrings ? (int)((GhostFrets >> (stringIndex * 8)) & 0xFF) : 0;

    public const int MaxGhostStrings = 8;
}

public struct RenderBendValue
{
    public double Time;
    public float Step;
}

public struct RenderEventPoint
{
    public double Time;
    public string Phrase;
    public string Section;
    public bool IsSelected;
    public bool IsHovered;

    /// <summary>A pending change replaces this marker's section (the AI chat review).</summary>
    public bool SectionIsEditTarget;

    /// <summary>A pending change replaces this marker's phrase.</summary>
    public bool PhraseIsEditTarget;
}

public struct RenderToneChange
{
    public double Time;
    public string ToneName;
    public bool IsSelected;
    public bool IsHovered;
}

public struct RenderLyricWord
{
    public double Time;
    public double EndTime;
    public string Text;
    public bool IsWordPart;
    public bool IsHovered;
    public bool IsSelected;
}

public struct RenderLyricPhrase
{
    public double StartTime;
    public double EndTime;
    public int WordOffset;
    public int WordCount;
}

public enum RenderShowlightLane : byte
{
    Fog,
    Beams,
    Lasers
}

public struct RenderShowlight
{
    public double Time;
    public int Note;
    public RenderShowlightLane Lane;
    public byte R;
    public byte G;
    public byte B;
}

/// <summary>One Live Play verdict freshly drained into the snapshot for the
/// 3D hit/miss feedback markers. Each snapshot carries only THIS frame's new
/// events; the render component keeps its own lifetime list.</summary>
public struct RenderLivePlayFeedback
{
    /// <summary>Monotonic id so the feedback component (which persists
    /// markers across frames) never ingests the same event twice when a
    /// snapshot is re-rendered after a failed staging update.</summary>
    public long Seq;
    public double NoteTimeMs;
    public double JudgedAtMs;
    public short StringIndex;
    public short Fret;
    public bool IsChord;
    /// <summary>LivePlayVerdict ordinal: 0 = Hit, 1 = HitWrongTechnique, 2 = Miss.</summary>
    public byte Verdict;
    /// <summary>LiveTimingDirection ordinal: 0 = None, 1 = Early, 2 = OnTime, 3 = Late.
    /// None means the judgment has no honest attack time (a miss, or a CQT
    /// pull-verified hit judged at window close) — the marker must NOT be
    /// depth-nudged from <see cref="JudgedAtMs"/> in that case, because that
    /// timestamp is the window expiry and would fling it behind the camera.</summary>
    public byte Direction;
}

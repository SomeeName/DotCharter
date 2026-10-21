using System.Globalization;
using System.Text;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Linking;

/// <summary>
///     THE measure-content comparison: one rule shared by link detection, link
///     verification, the load check and the stored link hash. Built once per chart
///     level, it answers <see cref="KeyFor" /> for any run of whole measures, in one of two
///     roles (<see cref="MeasureContentKind" />).
///     <para>
///         <b>Content is in beats.</b> Every position is taken relative to the run's first
///         beat, EXACTLY (a <see cref="FractionalPosition" />, no rounding), so the tempo map
///         never matters; the time signature and beat count of every measure are part of the
///         key. A sound belongs to the measure it STARTS in, and its sustain comes with it.
///     </para>
///     <para>
///         <b>Detection</b> compares what is played ("what is played"): every note and chord
///         with its onset, string, fret and techniques, bends, chord templates by SHAPE
///         (frets, fingers, ghost strings, arpeggio flags — not the id, not the name),
///         left-hand fingers, fret-hand mutes, the fret-hand position in effect at every onset,
///         and hand shapes clipped to the run. It ignores exactly what <c>ChartSpacingEnforcer</c>
///         (run on every import, save and load) changes:
///     </para>
///     <list type="bullet">
///         <item>A sustain ending within <see cref="MeasureContentOptions.Spacing" /> of the next onset,
///         of the run's end or beyond it, reads as "held to it" — the enforcer clamps to exactly that
///         bound, evaluated at the sound's own position (so in ms mode at its own tempo), and past the
///         run's edge the bound depends on what follows the run. A sustain shorter than that keeps its
///         exact length: staccato and legato never compare equal. Hand-shape ends likewise.</item>
///         <item>A link-next whose continuation lies outside the run is not compared, nor are bend
///         points past a trimmed end.</item>
///         <item>Anchors are compared as the position IN EFFECT at each onset (an anchor up to
///         <see cref="MeasureContentOptions.AnchorSnapToleranceMs" /> after it counts), never as stored
///         anchors: redundant-anchor removal and the snap move stored anchors depending on context.</item>
///         <item>A hand shape that starts before the run reads as starting at its first beat.</item>
///         <item><c>Ignore</c> derived by the enforcer (<c>IgnoreDerived</c>) is not compared.</item>
///     </list>
///     <para>
///         <b>Integrity</b> is the full stored content, exactly — every field above plus exact
///         sustains, notation durations, notation voices, stored anchors, hand shapes and
///         template names. It fingerprints ONE placement; edits propagate through the history,
///         and the detection key verifies that copies still agree.
///     </para>
/// </summary>
public sealed class MeasureContentIndex
{
    /// <summary>Slack for "within the gap": the enforcer's ms gap goes through doubles.</summary>
    private static readonly Fraction Slack = new(1, 3840);

    private readonly IList<BeatMarker> _beats;
    private readonly MeasureContentOptions _options;
    private readonly bool _integrity;
    private readonly List<SoundRef> _sounds;
    private readonly FractionalPosition[] _onsets;
    private readonly List<(double Ms, FretAnchor Anchor)> _anchorsByTime;
    private readonly List<FretAnchor> _anchors;
    private readonly List<HandShape> _shapes;
    private readonly List<FractionalPosition> _mutes;
    private readonly Dictionary<int, ChordTemplate> _templates = [];

    public MeasureContentIndex(Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats,
        MeasureContentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(beats);
        Arrangement = arrangement;
        Level = level;
        _beats = beats;
        _options = options ?? MeasureContentOptions.Detection;
        _integrity = _options.Kind == MeasureContentKind.Integrity;
        Measures = MeasureGrid.Build(beats);

        foreach (var template in arrangement.ChordTemplates)
            _templates.TryAdd(template.Id, template);

        _sounds = new List<SoundRef>(level.Notes.Count + level.Chords.Count);
        foreach (var note in level.Notes) _sounds.Add(new SoundRef(note.Position, note, null, 0));
        foreach (var chord in level.Chords) _sounds.Add(new SoundRef(chord.Position, null, chord, 0));
        if (_integrity)
        {
            foreach (var voice in arrangement.NotationVoices)
            {
                foreach (var note in voice.Notes) _sounds.Add(new SoundRef(note.Position, note, null, voice.VoiceIndex));
                foreach (var chord in voice.Chords) _sounds.Add(new SoundRef(chord.Position, null, chord, voice.VoiceIndex));
            }
        }

        _sounds.Sort(static (a, b) => a.Position.CompareTo(b.Position));
        // The onsets the enforcer clamps against: the level's own sounds (never notation voices).
        _onsets = _sounds.Where(s => s.Voice == 0 && (s.Note != null || s.Chord!.ChordNotes.Count > 0))
            .Select(s => s.Position).Distinct().ToArray();

        _anchors = [.. level.Anchors.OrderBy(a => a.Position)];
        _anchorsByTime = [.. level.Anchors.Select(a => (TimelineMath.CalculateTimeMs(a.Position, beats), a)).OrderBy(a => a.Item1)];
        _shapes = [.. level.HandShapes.OrderBy(h => h.Position)];
        _mutes = [.. level.FretHandMutes.Select(m => m.Position).Order()];
    }

    /// <summary>The arrangement whose level this index reads (templates resolve through it).</summary>
    public Arrangement Arrangement { get; }

    /// <summary>The level this index reads.</summary>
    public ChartLevel Level { get; }

    /// <summary>The measures of the beat map this index was built on.</summary>
    public IReadOnlyList<MeasureSpan> Measures { get; }

    /// <summary>The index over the arrangement's HARDEST level (the highest
    /// <see cref="ChartLevel.DifficultyIndex" />) — the only level links compare.</summary>
    public static MeasureContentIndex ForHardestLevel(Arrangement arrangement, IList<BeatMarker> beats,
        MeasureContentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var level = arrangement.Levels.MaxBy(l => l.DifficultyIndex) ?? new ChartLevel();
        return new MeasureContentIndex(arrangement, level, beats, options);
    }

    /// <summary>The content key of <paramref name="count" /> measures from
    /// <paramref name="firstMeasure" /> (indexes into <see cref="Measures" />).</summary>
    public MeasureContentKey KeyFor(int firstMeasure, int count = 1)
    {
        if (count < 1 || firstMeasure < 0 || firstMeasure + count > Measures.Count)
            throw new ArgumentOutOfRangeException(nameof(count), $"Run {firstMeasure}+{count} is outside the {Measures.Count} measures.");

        var start = Measures[firstMeasure].Start;
        var end = Measures[firstMeasure + count - 1].End;
        var sb = new StringBuilder(256);
        sb.Append(_options.Header());
        for (var m = firstMeasure; m < firstMeasure + count; m++)
        {
            var measure = Measures[m];
            sb.Append(CultureInfo.InvariantCulture, $"|t{measure.BeatsInMeasure}/{measure.NoteDenominator}x{measure.BeatCount}");
        }

        var tokens = new List<(FractionalPosition At, string Text)>();
        var soundCount = 0;
        for (var i = LowerBound(start); i < _sounds.Count && _sounds[i].Position < end; i++)
        {
            var sound = _sounds[i];
            var voice = sound.Voice == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"V{sound.Voice}");
            tokens.Add((sound.Position, voice + (sound.Note != null
                ? NoteToken(sound.Note, start, end)
                : ChordToken(sound.Chord!, start, end))));
            if (sound.Voice == 0) soundCount++;
        }

        tokens.Sort(static (a, b) =>
        {
            var byPosition = a.At.CompareTo(b.At);
            return byPosition != 0 ? byPosition : string.CompareOrdinal(a.Text, b.Text);
        });
        foreach (var (_, text) in tokens) sb.Append('|').Append(text);

        foreach (var mute in _mutes)
        {
            if (mute >= start && mute < end) sb.Append("|M").Append(Rel(mute, start));
        }

        if (_integrity)
        {
            AppendStoredAnchors(sb, start, end);
            AppendStoredHandShapes(sb, start, end);
        }
        else
        {
            if (_options.IncludeAnchors) AppendAnchorsInEffect(sb, start, end);
            if (_options.IncludeHandShapes) AppendHandShapes(sb, start, end);
        }

        return new MeasureContentKey(sb.ToString(), soundCount);
    }

    private bool Fingering => _integrity || _options.IncludeFingering;

    private string NoteToken(GuitarNote note, FractionalPosition start, FractionalPosition end)
    {
        var sb = new StringBuilder(48);
        sb.Append('N').Append(Rel(note.Position, start)).Append(CultureInfo.InvariantCulture, $":{note.StringIndex}:{note.Fret}");
        AppendTechniques(sb, note);
        if (note.PickDirection != PickDirectionType.None) sb.Append(CultureInfo.InvariantCulture, $",pk{(int)note.PickDirection}");
        if (note.GraceFret is { } grace) sb.Append(CultureInfo.InvariantCulture, $",g{grace}{(note.GraceSlide ? "s" : "")}");
        if (note.PassOtherNotes) sb.Append(",po");
        if (Fingering && note.LeftHandFinger >= 0) sb.Append(CultureInfo.InvariantCulture, $",f{note.LeftHandFinger}");
        if (_integrity)
        {
            if (note.Ignore) sb.Append(note.IgnoreDerived ? ",igd" : ",ig");
            if (note.NotationDuration is { } duration) sb.Append(",nd").Append(duration.ToString());
        }
        else if (note.Ignore && !note.IgnoreDerived)
        {
            sb.Append(",ig");
        }

        AppendSustain(sb, note, note.Position, end, note.PassOtherNotes);
        return sb.ToString();
    }

    private string ChordToken(Chord chord, FractionalPosition start, FractionalPosition end)
    {
        var sb = new StringBuilder(96);
        sb.Append('C').Append(Rel(chord.Position, start)).Append(':');
        AppendTemplate(sb, chord.TemplateId);
        if (chord.Strum != PickDirectionType.None) sb.Append(CultureInfo.InvariantCulture, $",st{(int)chord.Strum}");
        if (chord.Accent) sb.Append(",ac");
        if (chord.SplitIntoNotes) sb.Append(",sp");
        if (chord.ForceNoNotes) sb.Append(",nn");
        if (chord.PassOtherNotes) sb.Append(",po");
        if (_integrity)
        {
            if (chord.Ignore) sb.Append(chord.IgnoreDerived ? ",igd" : ",ig");
            if (chord.NotationDuration is { } duration) sb.Append(",nd").Append(duration.ToString());
        }
        else if (chord.Ignore && !chord.IgnoreDerived)
        {
            sb.Append(",ig");
        }

        sb.Append('[');
        foreach (var chordNote in chord.ChordNotes.OrderBy(n => n.StringIndex).ThenBy(n => n.Fret))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{chordNote.StringIndex}:{chordNote.Fret}");
            AppendTechniques(sb, chordNote);
            if (chordNote.GraceFret is { } grace) sb.Append(CultureInfo.InvariantCulture, $",g{grace}");
            if (chordNote.Ignore) sb.Append(",ig");
            if (_integrity && chordNote.ExplicitSustain) sb.Append(",es");
            AppendSustain(sb, chordNote, chord.Position, end, chord.PassOtherNotes);
            sb.Append(';');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private void AppendTechniques(StringBuilder sb, IGuitarSound sound)
    {
        if (sound.Mute != MuteType.None) sb.Append(CultureInfo.InvariantCulture, $",m{(int)sound.Mute}");
        if (sound.Hopo != HopoType.None) sb.Append(CultureInfo.InvariantCulture, $",h{(int)sound.Hopo}");
        if (sound.Harmonic != HarmonicType.None) sb.Append(CultureInfo.InvariantCulture, $",x{(int)sound.Harmonic}");
        if (sound.BassPicking != BassPickingType.None) sb.Append(CultureInfo.InvariantCulture, $",bp{(int)sound.BassPicking}");
        if (sound.Vibrato) sb.Append(CultureInfo.InvariantCulture, $",v{sound.VibratoStrength}");
        if (sound.Tremolo) sb.Append(",tr");
        if (sound.Accent) sb.Append(",ac");
        if (sound.SlideTo is { } slide) sb.Append(CultureInfo.InvariantCulture, $",sl{slide}");
        if (sound.UnpitchedSlide) sb.Append(",us");
        if (_integrity && sound.LinkNext) sb.Append(",ln");
    }

    /// <summary>
    ///     Integrity: the exact sustain and every bend point. Detection: "held to the next
    ///     onset" (<c>~~</c>), a link-next continued inside the run (<c>~L</c> + length), or the
    ///     exact length — plus the bend points the enforcer's trim cannot touch.
    /// </summary>
    private void AppendSustain(StringBuilder sb, IGuitarSound sound, FractionalPosition onset, FractionalPosition end,
        bool passOtherNotes)
    {
        var sustainEnd = onset + sound.Sustain;
        FractionalPosition? keepBendsBefore = null;
        if (_integrity)
        {
            sb.Append('~').Append(Rel(sustainEnd, onset));
        }
        else if (sound.Sustain <= FractionalPosition.Zero)
        {
            sb.Append("~0");
        }
        else if (sound.LinkNext && sustainEnd < end)
        {
            sb.Append("~L").Append(Rel(sustainEnd, onset));
        }
        else
        {
            var bound = HeldThreshold(onset, end, passOtherNotes);
            if (sustainEnd >= bound)
            {
                sb.Append("~~");
                keepBendsBefore = bound;
            }
            else
            {
                sb.Append('~').Append(Rel(sustainEnd, onset));
            }
        }

        FractionalPosition? previous = null;
        foreach (var bend in sound.BendValues.OrderBy(b => b.Position))
        {
            var at = _integrity ? bend.Position : FractionalPosition.Max(bend.Position, onset);
            if (keepBendsBefore is { } keep && at >= keep) break;
            if (!_integrity && previous == at) continue;
            previous = at;
            sb.Append(",b").Append(Rel(at, onset)).Append(CultureInfo.InvariantCulture, $"={bend.Step:0.###}");
        }
    }

    private void AppendTemplate(StringBuilder sb, int templateId)
    {
        if (templateId < 0)
        {
            sb.Append('-');
            return;
        }

        if (!_templates.TryGetValue(templateId, out var template))
        {
            sb.Append('?');
            return;
        }

        foreach (var (stringIndex, fret) in template.Frets.OrderBy(f => f.Key))
            sb.Append(CultureInfo.InvariantCulture, $"{stringIndex}={fret}");
        if (Fingering)
        {
            sb.Append('/');
            foreach (var (stringIndex, finger) in template.Fingers.Where(f => f.Value >= 0).OrderBy(f => f.Key))
                sb.Append(CultureInfo.InvariantCulture, $"{stringIndex}={finger}");
        }

        if (template.GhostMask != 0) sb.Append(CultureInfo.InvariantCulture, $"/gh{template.GhostMask}");
        if (template.Arpeggio) sb.Append("/arp");
        if (template.ForceArpeggioInRs) sb.Append("/farp");
        if (template.NopInRs) sb.Append("/nop");
        if (!_integrity) return;
        sb.Append("/n:").Append(template.Name);
        if (template.Pinned) sb.Append("/pin");
        if (template.FollowedFingering) sb.Append("/fol");
        if (template.Fingerless) sb.Append("/nof");
    }

    /// <summary>The fret-hand position in effect at each onset of the run, written only
    /// where it changes (the first onset always).</summary>
    private void AppendAnchorsInEffect(StringBuilder sb, FractionalPosition start, FractionalPosition end)
    {
        string? previous = null;
        for (var i = UpperBound(_onsets, start + Slack.Negate()); i < _onsets.Length && _onsets[i] < end; i++)
        {
            var onset = _onsets[i];
            var anchor = AnchorInEffect(TimelineMath.CalculateTimeMs(onset, _beats) + _options.AnchorSnapToleranceMs + 1e-6);
            var state = anchor == null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{anchor.Fret}/{anchor.Width}");
            if (state == previous) continue;
            previous = state;
            sb.Append("|A").Append(Rel(onset, start)).Append(':').Append(state);
        }
    }

    private FretAnchor? AnchorInEffect(double ms)
    {
        int lo = 0, hi = _anchorsByTime.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_anchorsByTime[mid].Ms <= ms) lo = mid + 1;
            else hi = mid;
        }

        return lo == 0 ? null : _anchorsByTime[lo - 1].Anchor;
    }

    private void AppendStoredAnchors(StringBuilder sb, FractionalPosition start, FractionalPosition end)
    {
        foreach (var anchor in _anchors.Where(a => a.Position >= start && a.Position < end))
            sb.Append("|A").Append(Rel(anchor.Position, start)).Append(CultureInfo.InvariantCulture, $":{anchor.Fret}/{anchor.Width}");
    }

    private void AppendStoredHandShapes(StringBuilder sb, FractionalPosition start, FractionalPosition end)
    {
        foreach (var shape in _shapes.Where(h => h.Position >= start && h.Position < end))
        {
            sb.Append("|H").Append(Rel(shape.Position, start)).Append('-').Append(Rel(shape.EndPosition, start))
                .Append(":n:").Append(shape.Name).Append(':');
            AppendTemplate(sb, shape.TemplateId);
        }
    }

    private void AppendHandShapes(StringBuilder sb, FractionalPosition start, FractionalPosition end)
    {
        for (var i = 0; i < _shapes.Count; i++)
        {
            var shape = _shapes[i];
            if (shape.Position >= end) break;
            if (shape.EndPosition <= start) continue;

            string endToken;
            if (IsShapeHeld(shape, end)) endToken = "~";
            else if (ShapeEndOnset(shape, end) is { } o) endToken = "<" + Rel(o, start);
            else endToken = Rel(shape.EndPosition, start);

            sb.Append("|H").Append(Rel(FractionalPosition.Max(shape.Position, start), start)).Append('-').Append(endToken).Append(':');
            AppendTemplate(sb, shape.TemplateId);
        }
    }

    /// <summary>The spacing gap (in beats) for a sound or hand shape starting at <paramref name="position" />.</summary>
    public FractionalPosition GapAt(FractionalPosition position) => _options.Spacing.At(position, _beats);

    /// <summary>The next onset of the level's own sounds strictly after <paramref name="position" /> (any string).</summary>
    public FractionalPosition? NextOnset(FractionalPosition position) => NextOnsetAfter(position);

    /// <summary>
    ///     Where detection's "held to the next sound" starts for a sound at <paramref name="onset" />
    ///     inside a run ending at <paramref name="runEnd" />: a sustain ending at or after it reads
    ///     as <c>~~</c>. THE threshold <see cref="KeyFor" /> uses.
    /// </summary>
    public FractionalPosition HeldThreshold(FractionalPosition onset, FractionalPosition runEnd, bool passOtherNotes) =>
        HoldLimit(onset, runEnd, passOtherNotes) - GapAt(onset) + Slack.Negate();

    /// <summary>The end the clean-up pass gives a sound held as long as it may be:
    /// the hold limit (next onset or run end) minus the gap.</summary>
    public FractionalPosition HeldEnd(FractionalPosition onset, FractionalPosition runEnd, bool passOtherNotes) =>
        HoldLimit(onset, runEnd, passOtherNotes) - GapAt(onset);

    /// <summary>Whether detection reads <paramref name="sound" />'s sustain as held to what follows it.</summary>
    public bool IsHeld(IGuitarSound sound, FractionalPosition onset, FractionalPosition runEnd, bool passOtherNotes)
    {
        ArgumentNullException.ThrowIfNull(sound);
        if (sound.Sustain <= FractionalPosition.Zero) return false;
        var sustainEnd = onset + sound.Sustain;
        if (sound.LinkNext && sustainEnd < runEnd) return false;
        return sustainEnd >= HeldThreshold(onset, runEnd, passOtherNotes);
    }

    /// <summary>The end detection reads as "held" for a hand shape: the next shape's start or the
    /// run's end, whichever is first, minus the gap.</summary>
    public FractionalPosition ShapeHeldEnd(HandShape shape, FractionalPosition runEnd)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var limit = runEnd;
        foreach (var other in _shapes)
        {
            if (other.Position <= shape.Position) continue;
            limit = FractionalPosition.Min(limit, other.Position);
            break;
        }

        return limit - _options.Spacing.At(shape.Position, _beats);
    }

    /// <summary>Whether detection reads <paramref name="shape" /> as held to what follows it.</summary>
    public bool IsShapeHeld(HandShape shape, FractionalPosition runEnd) =>
        shape.EndPosition >= ShapeHeldEnd(shape, runEnd) + Slack.Negate();

    /// <summary>
    ///     The onset a hand shape's end reads as clamped against (<c>&lt;onset</c>: it ends within the
    ///     gap before a sound inside the run), or null when it is held or ends freely.
    /// </summary>
    public FractionalPosition? ShapeEndOnset(HandShape shape, FractionalPosition runEnd)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (IsShapeHeld(shape, runEnd)) return null;
        var onset = FirstOnsetAtOrAfter(shape.EndPosition);
        return onset is { } o && o < runEnd && shape.EndPosition >= o - _options.Spacing.At(shape.Position, _beats) + Slack.Negate()
            ? o
            : null;
    }

    private FractionalPosition HoldLimit(FractionalPosition onset, FractionalPosition runEnd, bool passOtherNotes) =>
        passOtherNotes ? runEnd : FractionalPosition.Min(NextOnsetAfter(onset) ?? runEnd, runEnd);

    private FractionalPosition? NextOnsetAfter(FractionalPosition position)
    {
        var i = UpperBound(_onsets, position);
        return i < _onsets.Length ? _onsets[i] : null;
    }

    private FractionalPosition? FirstOnsetAtOrAfter(FractionalPosition position)
    {
        var i = Array.BinarySearch(_onsets, position);
        if (i < 0) i = ~i;
        return i < _onsets.Length ? _onsets[i] : null;
    }

    private static int UpperBound(FractionalPosition[] sorted, FractionalPosition position)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid] <= position) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private int LowerBound(FractionalPosition position)
    {
        int lo = 0, hi = _sounds.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_sounds[mid].Position < position) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>A position relative to <paramref name="origin" />, exactly: <c>beats</c> or <c>beats+num/den</c>.</summary>
    private static string Rel(FractionalPosition position, FractionalPosition origin)
    {
        var rel = position - origin;
        return rel.Fraction.Numerator == 0
            ? rel.BeatId.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{rel.BeatId}+{rel.Fraction.Numerator}/{rel.Fraction.Denominator}");
    }

    private readonly record struct SoundRef(FractionalPosition Position, GuitarNote? Note, Chord? Chord, int Voice);
}

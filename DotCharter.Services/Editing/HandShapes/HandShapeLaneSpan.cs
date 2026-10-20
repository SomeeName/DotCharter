using DotCharter.Services.Editing.Fingering;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     Where a bar drawn by POINTER on the hand-shape lane starts and ends, and what it
/// carries. Pure: it reads the level and never writes it; the tool commits the
///     bar through <c>IHighwayEditingService.AddHandShape</c> with the templates it minted,
///     so the bar and its grip are ONE undo node.
///     <list type="bullet">
///         <item><b>A drag</b> spans the two snapped ends, in either direction, clamped to
///         the free stretch of lane the press landed in, so it never overlaps a bar.</item>
///         <item><b>A plain click</b> spans from the cursor to the first sound that is not
///         played with the same grip (<see cref="ClickEnd" />). The bar ends ON that sound;
///         <c>ChartSpacingEnforcer</c> opens the gap, as it does for every bar.</item>
///     </list>
/// </summary>
public static class HandShapeLaneSpan
{
    /// <summary>How far a click reaches when nothing after the cursor bounds it — the
    /// one-beat bar.</summary>
    public static readonly FractionalPosition FallbackLength = new(1);

    /// <summary>
    ///     The half-open span a drag from <paramref name="anchor" /> to
    ///     <paramref name="current" /> draws, ordered and clamped between the bars either
    ///     side of the anchor. Null when the span is empty or the anchor sits inside a bar.
    /// </summary>
    public static (FractionalPosition Start, FractionalPosition End)? DragSpan(
        IEnumerable<HandShape> handShapes, FractionalPosition anchor, FractionalPosition current)
    {
        ArgumentNullException.ThrowIfNull(handShapes);
        var lo = FractionalPosition.Zero;
        FractionalPosition? hi = null;
        foreach (var bar in handShapes)
        {
            if (bar.Covers(anchor)) return null;
            if (bar.EndPosition.CompareTo(anchor) <= 0 && bar.EndPosition.CompareTo(lo) > 0) lo = bar.EndPosition;
            if (bar.Position.CompareTo(anchor) > 0 && (hi is not { } h || bar.Position.CompareTo(h) < 0)) hi = bar.Position;
        }

        var start = FractionalPosition.Max(FractionalPosition.Min(anchor, current), lo);
        var end = FractionalPosition.Max(anchor, current);
        if (hi is { } limit && limit.CompareTo(end) < 0) end = limit;
        return end.CompareTo(start) > 0 ? (start, end) : null;
    }

    /// <summary>
    ///     Where a bar opened by a click at <paramref name="start" /> ends: just before the
    ///     next FRET CHANGE or the next CHORD.
    ///     <list type="bullet">
    ///         <item>The first sound at or after the cursor sets the grip. Single notes
    ///         keep adding strings to it; a note that plays a string the grip already holds
    ///         at a DIFFERENT fret is the fret change, and any chord ends a run of notes.</item>
    ///         <item>A run that opens on a chord keeps repeated strums of the SAME frets,
    ///         and single notes that pick out the grip's own string/fret pairs. Anything
    ///         else ends it.</item>
    ///         <item>The bar never runs into an existing bar, nor across an FHP anchor
    ///         after its first sound (an anchor inside a hand shape is I20).</item>
    ///         <item>With nothing to end the run the bar covers the run's longest tail, or
    ///         one beat past its last onset; with no sound at all it is
    ///         <see cref="FallbackLength" /> long.</item>
    ///     </list>
    ///     Null when no bar fits (the next bar starts at the cursor).
    /// </summary>
    public static FractionalPosition? ClickEnd(
        Arrangement arrangement,
        IEnumerable<GuitarNote> notes,
        IEnumerable<Chord> chords,
        IEnumerable<HandShape> handShapes,
        IEnumerable<FretAnchor> anchors,
        FractionalPosition start)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var sounds = new List<Sound>();
        foreach (var note in notes)
        {
            if (note.Position.CompareTo(start) >= 0)
                sounds.Add(new Sound(note.Position, note.EndPosition, new Dictionary<int, int> { [note.StringIndex] = note.Fret }, false));
        }

        foreach (var chord in chords)
        {
            if (chord.Position.CompareTo(start) < 0) continue;
            var frets = GripOf(arrangement, chord);
            if (frets.Count > 0) sounds.Add(new Sound(chord.Position, chord.EndPosition, frets, true));
        }

        sounds.Sort((l, r) => l.Position.CompareTo(r.Position));

        FractionalPosition end;
        var boundFrom = start;
        if (sounds.Count == 0)
        {
            end = start + FallbackLength;
        }
        else
        {
            var first = sounds[0];
            var grip = new Dictionary<int, int>(first.Frets);
            var reach = first.EndPosition;
            var lastOnset = first.Position;
            FractionalPosition? stop = null;
            for (var i = 1; i < sounds.Count; i++)
            {
                var sound = sounds[i];
                // Everything struck WITH the first sound is part of the grip it opens.
                if (sound.Position.CompareTo(first.Position) != 0 && !Continues(grip, first.IsChord, sound))
                {
                    stop = sound.Position;
                    break;
                }

                if (!first.IsChord)
                {
                    foreach (var (stringIndex, fret) in sound.Frets) grip.TryAdd(stringIndex, fret);
                }

                if (sound.EndPosition.CompareTo(reach) > 0) reach = sound.EndPosition;
                lastOnset = sound.Position;
            }

            end = stop ?? (reach.CompareTo(lastOnset) > 0 ? reach : lastOnset + FallbackLength);
            boundFrom = first.Position;
        }

        foreach (var bar in handShapes)
        {
            if (bar.Position.CompareTo(start) > 0 && bar.Position.CompareTo(end) < 0) end = bar.Position;
        }

        foreach (var anchor in anchors)
        {
            if (anchor.Position.CompareTo(boundFrom) > 0 && anchor.Position.CompareTo(end) < 0) end = anchor.Position;
        }

        return end.CompareTo(start) > 0 ? end : null;
    }

    /// <summary>
    ///     The bar over [<paramref name="start" />, <paramref name="end" />) with the grip
    ///     built from the sounds inside it, resolved through
    ///     <see cref="ChordTemplateResolver" /> (plain flavour). A template that has to be
    ///     minted lands in <paramref name="minted" /> and NOT in the arrangement. The bar is
    ///     named after its template — empty when the template has no name or there is no
    ///     sound to build one from — never a placeholder.
    /// </summary>
    public static HandShape BuildBar(
        Arrangement arrangement,
        IFingeringGenerationService fingering,
        IEnumerable<GuitarNote> notes,
        IEnumerable<Chord> chords,
        FractionalPosition start,
        FractionalPosition end,
        IList<ChordTemplate> minted)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(fingering);
        var bar = new HandShape { Position = start, EndPosition = end, Name = string.Empty };
        var shape = fingering.BuildTemplateForRange(arrangement, notes, chords, start, end);
        if (shape == null) return bar;

        var template = ChordTemplateResolver.Resolve(arrangement, shape.Frets, ArpeggioFlavour.Plain, null, minted);
        bar.TemplateId = template.Id;
        bar.Name = template.Name;
        return bar;
    }

    /// <summary>What a preview bar prints: the grip's name and frets, e.g. <c>Am x02210</c>.</summary>
    public static string GripLabel(ChordTemplate? template, int stringCount)
    {
        if (template == null) return string.Empty;
        return $"{template.Name} {template.GetTemplateFrets(stringCount)}".Trim();
    }

    private static bool Continues(Dictionary<int, int> grip, bool chordRun, Sound sound)
    {
        if (sound.IsChord) return chordRun && SameFrets(grip, sound.Frets);
        foreach (var (stringIndex, fret) in sound.Frets)
        {
            if (grip.TryGetValue(stringIndex, out var held))
            {
                if (held != fret) return false;
            }
            else if (chordRun)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameFrets(Dictionary<int, int> grip, Dictionary<int, int> frets) =>
        grip.Count == frets.Count && grip.All(kv => frets.TryGetValue(kv.Key, out var f) && f == kv.Value);

    /// <summary>The chord's own notes; a chord with none falls back to its template.</summary>
    private static Dictionary<int, int> GripOf(Arrangement arrangement, Chord chord)
    {
        if (chord.ChordNotes.Count > 0) return ChordTemplateResolver.FretsOf(chord);
        var template = ChordTemplateResolver.FindById(arrangement, chord.TemplateId);
        return template == null ? [] : new Dictionary<int, int>(template.Frets);
    }

    private readonly record struct Sound(
        FractionalPosition Position, FractionalPosition EndPosition, Dictionary<int, int> Frets, bool IsChord);
}

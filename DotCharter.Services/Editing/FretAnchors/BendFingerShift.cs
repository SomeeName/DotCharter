namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>
///     The generator's last pass: a bend wants the ring finger, backed by the fingers below it, not the pinky. When a
///     bent sound with no stated finger sits on its anchor's fret + 3 (finger 4, finding D56 of
///     <c>Validation/Rules/FretHandPositionRule</c>), the anchor moves up one fret so the bend falls on finger 3, but
///     only when every fretted sound it governs, and every pitched slide landing it governs, still fits the four-fret
///     box at the new fret, and that fret stays above the capo and on the neck. Otherwise the anchor stays: the lick
///     really needs the pinky, and D56 is a finding for the charter, not a generator slip.
/// </summary>
internal static class BendFingerShift
{
    private const int Box = 4;
    private const int Pinky = 4;

    /// <summary>Moves each anchor of <paramref name="anchors" /> (ordered by position) whose span bends on the pinky
    /// and can be played one fret higher. The last anchor governs up to <paramref name="rangeEnd" /> (inclusive).</summary>
    public static void Apply(List<FretAnchor> anchors, ChartLevel level, IReadOnlyList<ChordTemplate>? templates, int capo,
        FractionalPosition? rangeEnd)
    {
        if (anchors.Count == 0) return;
        var byId = new Dictionary<int, ChordTemplate>();
        foreach (var t in templates ?? []) byId.TryAdd(t.Id, t);

        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            var next = i + 1 < anchors.Count ? anchors[i + 1].Position : (FractionalPosition?)null;
            bool Governs(FractionalPosition p) =>
                p >= anchor.Position && (next.HasValue ? p < next.Value : !rangeEnd.HasValue || p <= rangeEnd.Value);

            var span = Span(level, byId, Governs, anchor.Fret + Box - 1);
            if (!span.PinkyBend) continue;
            var fret = anchor.Fret + 1;
            if (fret < FretLimits.MinAnchorFret(capo) || fret > FretLimits.MaxAnchorFretFor(Math.Max(Box, anchor.Width))) continue;
            if (span.Frets.All(f => f >= fret && f < fret + Box)) anchor.Fret = fret;
        }
    }

    // Every fretted fret the anchor governs (struck sounds and pitched slide landings), and whether one of its bent
    // sounds with no stated finger sits on the pinky's fret.
    private static (List<int> Frets, bool PinkyBend) Span(ChartLevel level, Dictionary<int, ChordTemplate> byId,
        Func<FractionalPosition, bool> governs, int pinkyFret)
    {
        var frets = new List<int>();
        var pinkyBend = false;
        foreach (var n in level.Notes)
        {
            Add(n, n.Position, governs, frets);
            if (governs(n.Position) && IsUnfingeredBendOn(n, n.LeftHandFinger, pinkyFret)) pinkyBend = true;
        }

        foreach (var c in level.Chords)
        {
            var template = byId.GetValueOrDefault(c.TemplateId);
            foreach (var cn in c.ChordNotes)
            {
                var finger = template != null && template.Fingers.TryGetValue(cn.StringIndex, out var f) ? f : 0;
                Add(cn, c.Position, governs, frets);
                if (governs(c.Position) && IsUnfingeredBendOn(cn, finger, pinkyFret)) pinkyBend = true;
            }
        }

        return (frets, pinkyBend);
    }

    private static void Add(IGuitarSound sound, FractionalPosition at, Func<FractionalPosition, bool> governs, List<int> frets)
    {
        if (sound.Fret > 0 && governs(at)) frets.Add(sound.Fret);
        if (sound.SlideTo is > 0 && !sound.UnpitchedSlide && governs(at + sound.Sustain)) frets.Add(sound.SlideTo.Value);
    }

    private static bool IsUnfingeredBendOn(IGuitarSound sound, int finger, int pinkyFret) =>
        sound.Fret == pinkyFret && finger is not (>= 1 and <= Pinky) && sound.BendValues.Any(b => Math.Abs(b.Step) > 0.0001);
}

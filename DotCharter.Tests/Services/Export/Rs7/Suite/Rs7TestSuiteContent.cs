namespace DotCharter.Tests.Services.Export.Rs7.Suite;

/// <summary>A cursor over beats that collects events; the content generators write through it.</summary>
internal sealed class Seq
{
    private readonly List<Ev> _events = [];

    public Seq(int start = Rs7SuiteSpec.LeadInBeats) => Beat = start;

    public int Beat { get; private set; }

    public Seq Note(int str, int fret, Tech tech = Tech.None, int gap = 1)
    {
        _events.Add(new Ev(Beat, [new Col(str, fret, -1, tech)]));
        Beat += gap;
        return this;
    }

    public Seq Chord(string name, Col[] cols, int gap = 2)
    {
        _events.Add(new Ev(Beat, cols, name));
        Beat += gap;
        return this;
    }

    public Seq Skip(int beats)
    {
        Beat += beats;
        return this;
    }

    public Ev[] Done() => [.. _events];
}

/// <summary>The chord shapes of one instrument, in package string indices.</summary>
internal sealed record ChordSet(
    Col[] Open, Col[] Fretted, Col[] Dyad, Col[] Mixed, Col[] Full, Col[] Fingers, Col[] Stock);

/// <summary>Reusable content: the chord shapes, singles on every string, technique ladders.</summary>
internal static class Rs7SuiteContent
{
    public static readonly int[] AllFrets = [0, 1, 2, 5, 12, 19, 24];
    public static readonly int[] TourFrets = [0, 2, 5, 12];

    public static Col K(int s, int fret, int finger = -1) => new(s, fret, finger);

    public static ChordSet Chords(Inst i) => i.Key switch
    {
        "g6" => new ChordSet(
            [K(0, 0), K(1, 2, 2), K(2, 2, 3)], [K(0, 3, 1), K(1, 5, 3), K(2, 5, 4)], [K(0, 0), K(1, 2, 2)],
            [K(1, 0), K(3, 2, 1)], [K(0, 0), K(1, 2, 2), K(2, 2, 3), K(3, 0), K(4, 0), K(5, 0)],
            [K(1, 3, 2), K(2, 5, 4), K(3, 4, 3)], [K(1, 0), K(2, 2, 2), K(3, 2, 3)]),
        "g7" => new ChordSet(
            [K(6, 0), K(0, 2, 1), K(1, 2, 2)], [K(6, 1, 1), K(0, 3, 3), K(1, 3, 4)], [K(6, 0), K(1, 2, 2)],
            [K(6, 3, 2), K(2, 2, 1)],
            [K(6, 0), K(0, 2, 2), K(1, 2, 3), K(2, 0), K(3, 0), K(4, 0), K(5, 0)],
            [K(6, 4, 3), K(0, 5, 4), K(1, 3, 1)], [K(0, 0), K(1, 2, 2), K(2, 2, 3)]),
        "g8" => new ChordSet(
            [K(7, 0), K(6, 0), K(0, 2, 1)], [K(7, 1, 1), K(6, 1, 1), K(0, 3, 3)], [K(7, 0), K(6, 0)],
            [K(7, 2, 1), K(6, 2, 1), K(2, 2, 2)],
            [K(7, 0), K(6, 0), K(0, 2, 1), K(1, 2, 2), K(2, 0), K(3, 0), K(4, 0), K(5, 0)],
            [K(7, 4, 3), K(6, 5, 4), K(0, 3, 1)], [K(0, 0), K(1, 2, 2), K(2, 2, 3)]),
        "b4" => new ChordSet(
            [K(0, 0), K(1, 2, 2)], [K(0, 3, 1), K(1, 5, 3)], [K(1, 0), K(2, 2, 2)], [K(2, 0), K(3, 2, 2)],
            [K(0, 0), K(1, 2, 2), K(2, 2, 3), K(3, 0)], [K(1, 3, 2), K(2, 5, 4)], [K(1, 0), K(3, 2, 2)]),
        "b5" => new ChordSet(
            [K(4, 0), K(0, 2, 1), K(1, 2, 2)], [K(4, 1, 1), K(0, 3, 3), K(1, 3, 4)], [K(4, 0), K(0, 0)],
            [K(4, 2, 1), K(2, 2, 2)], [K(4, 0), K(0, 0), K(1, 2, 2), K(2, 2, 3), K(3, 0)],
            [K(4, 4, 3), K(0, 5, 4), K(1, 3, 1)], [K(0, 0), K(1, 2, 2)]),
        _ => new ChordSet(
            [K(4, 0), K(0, 2, 1), K(5, 3, 3)], [K(4, 1, 1), K(1, 3, 3), K(5, 5, 4)], [K(4, 0), K(5, 0)],
            [K(4, 2, 1), K(2, 2, 2), K(5, 2, 3)],
            [K(4, 0), K(0, 0), K(1, 2, 2), K(2, 2, 3), K(3, 0), K(5, 3, 3)],
            [K(4, 4, 3), K(0, 5, 4), K(5, 1, 1)], [K(0, 0), K(1, 2, 2)])
    };

    public static Col[] Barre(Col[] cols, int by) =>
        cols.Select(c => c with { Fret = c.Fret + by, Finger = c.Finger >= 0 || c.Fret >= 0 ? Math.Max(c.Finger, 1) : -1 }).ToArray();

    /// <summary>Every string, lowest pitch first, one note per beat on each of <paramref name="frets" />.</summary>
    public static Seq Singles(Seq seq, Inst inst, int[] frets, int[]? strings = null)
    {
        foreach (var s in strings ?? inst.Order)
        {
            foreach (var f in frets) seq.Note(s, f);
        }

        return seq;
    }

    /// <summary>The chord ladder: open, fretted, double stop, extra string(s) plus one normal, full, with
    /// finger numbers, one repeated four times, a stock-only chord, then everything again barred up.</summary>
    public static Seq ChordLadder(Seq seq, Inst inst, bool repeatAndBarre = true)
    {
        var c = Chords(inst);
        seq.Chord("Open", c.Open).Chord("Fretted", c.Fretted).Chord("Dyad", c.Dyad).Chord("ExtraPlus1", c.Mixed)
            .Chord("Full", c.Full).Chord("Fingers", c.Fingers).Chord("Stock", c.Stock);
        if (!repeatAndBarre) return seq;
        for (var n = 0; n < 4; n++) seq.Chord("Open", c.Open, 1);
        seq.Skip(1);
        seq.Chord("OpenUp", Barre(c.Open, 5)).Chord("FrettedUp", Barre(c.Fretted, 7)).Chord("DyadUp", Barre(c.Dyad, 9))
            .Chord("ExtraPlus1Up", Barre(c.Mixed, 5)).Chord("FullUp", Barre(c.Full, 3));
        return seq;
    }

    public static Tech[] Techniques(Inst inst)
    {
        Tech[] common =
        [
            Tech.Sustain, Tech.Slide, Tech.UnpitchedSlide, Tech.Bend, Tech.Vibrato, Tech.NaturalHarmonic,
            Tech.PinchHarmonic, Tech.PalmMute, Tech.HammerOn, Tech.PullOff, Tech.Tremolo, Tech.Accent, Tech.Tap
        ];
        return inst.Bass ? [.. common, Tech.Slap, Tech.Pop] : common;
    }

    private static int FretFor(Tech t) => t switch
    {
        Tech.NaturalHarmonic => 12,
        Tech.Bend => 7,
        Tech.PullOff => 7,
        Tech.Tap => 9,
        _ => 5
    };

    /// <summary>Each technique on <paramref name="extra" /> and on a normal string, alone, three beats apiece.</summary>
    public static Ev[] TechniquesAlone(Inst inst, int extra)
    {
        var seq = new Seq();
        foreach (var t in Techniques(inst))
        {
            foreach (var s in new[] { extra, inst.Normal[1] })
            {
                var f = FretFor(t);
                if (t == Tech.HammerOn) seq.Note(s, f - 2).Note(s, f, t).Skip(1);
                else if (t == Tech.PullOff) seq.Note(s, f).Note(s, f - 2, t).Skip(1);
                else seq.Note(s, f, t, 3);
            }
        }

        return seq.Done();
    }

    /// <summary>Each technique inside a chord with the extra string(s) and inside a stock-only chord.</summary>
    public static Ev[] TechniquesInChords(Inst inst)
    {
        var seq = new Seq();
        var extras = inst.Extras.Select(e => (e, 5)).ToArray();
        foreach (var t in Techniques(inst))
        {
            var fret = FretFor(t);
            var withExtra = extras.Select(x => K(x.e, fret)).Concat([K(inst.Normal[1], fret + 2)]).ToArray();
            var normal = new[] { K(inst.Normal[0], fret), K(inst.Normal[1], fret + 2), K(inst.Normal[2], fret + 2) };
            foreach (var (label, cols) in new[] { ("Ex", withExtra), ("St", normal) })
            {
                var name = $"{label} {t}";
                Col[] Tagged(int by) =>
                    cols.Select(c => c with { Fret = t == Tech.NaturalHarmonic ? 12 : c.Fret + by, Tech = t }).ToArray();
                if (t == Tech.HammerOn) seq.Chord(name + " a", cols, 1).Chord(name, Tagged(2), 2).Skip(1);
                else if (t == Tech.PullOff) seq.Chord(name + " a", Barre(cols, 2), 1).Chord(name, Tagged(0), 2).Skip(1);
                else seq.Chord(name, Tagged(0), 3);
            }
        }

        return seq.Done();
    }

    public static Ev[] WithLevels(Ev[] events, int levels) =>
        events.Select((e, i) => e with { Level = i % levels }).ToArray();

    /// <summary>Five sections (stock, extra strings, chords, high extra-string solo, stock) for the sections chart.</summary>
    public static (Ev[] Events, Sec[] Sections) Sectioned(Inst inst)
    {
        var seq = new Seq();
        var sections = new List<Sec> { new("intro", seq.Beat) };
        foreach (var f in new[] { 0, 2, 3, 5, 0, 2, 3, 5 }) seq.Note(inst.Normal[f % 3 + 1], f);
        sections.Add(new Sec("verse", seq.Skip(4).Beat));
        foreach (var s in inst.Extras) foreach (var f in new[] { 0, 2, 3, 5 }) seq.Note(s, f);
        seq.Note(inst.Extras[0], 7).Note(inst.Extras[^1], 9);
        sections.Add(new Sec("chorus", seq.Skip(4).Beat));
        var c = Chords(inst);
        seq.Chord("Open", c.Open, 3).Chord("Fretted", c.Fretted, 3).Chord("Dyad", c.Dyad, 3).Chord("ExtraPlus1", c.Mixed, 3);
        sections.Add(new Sec("solo", seq.Skip(1).Beat));
        foreach (var s in inst.Extras)
        {
            seq.Note(s, 12, Tech.Sustain, 3).Note(s, 15, Tech.Vibrato, 3).Note(s, 17, Tech.Slide, 3).Note(s, 22, gap: 2);
        }

        sections.Add(new Sec("outro", seq.Skip(2).Beat));
        foreach (var f in new[] { 0, 3, 5, 7, 5, 3, 0 }) seq.Note(inst.Normal[1], f);
        return (WithLevels(seq.Done(), 3), [.. sections]);
    }

    /// <summary>A short part for the multi-arrangement songs: the extra strings then one chord.</summary>
    public static Ev[] Short(Inst inst)
    {
        var seq = new Seq();
        foreach (var s in inst.Order.Where(s => inst.Extras.Contains(s) || s == inst.Normal[0]))
        {
            foreach (var f in new[] { 0, 3, 5, 7, 12 }) seq.Note(s, f, gap: 2);
        }

        seq.Chord("Open", Chords(inst).Open, 4).Chord("Dyad", Chords(inst).Dyad, 4);
        return seq.Done();
    }

    /// <summary>The notes and chords every tuning chart plays: a tour of the strings and a few chords.</summary>
    public static Ev[] Tour(Inst inst)
    {
        var seq = Singles(new Seq(), inst, TourFrets);
        seq.Skip(1);
        var c = Chords(inst);
        seq.Chord("Open", c.Open).Chord("Fretted", c.Fretted).Chord("Dyad", c.Dyad).Chord("Full", c.Full);
        return seq.Done();
    }
}

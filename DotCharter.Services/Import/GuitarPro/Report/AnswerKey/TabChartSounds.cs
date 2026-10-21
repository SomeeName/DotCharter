using DotCharter.Core.Linking;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>The techniques the comparison scores, one flag each.</summary>
[Flags]
public enum SoundTechnique
{
    None = 0,
    /// <summary>An unpitched slide off the note (Rocksmith's slide-out).</summary>
    SlideOut = 1 << 0,
    SlideIn = 1 << 1,
    Bend = 1 << 2,
    HammerOn = 1 << 3,
    PullOff = 1 << 4,
    Tap = 1 << 5,
    PalmMute = 1 << 6,
    FretHandMute = 1 << 7,
    Harmonic = 1 << 8,
    PinchHarmonic = 1 << 9,
    Vibrato = 1 << 10,
    Tremolo = 1 << 11,

    /// <summary>A pitched slide that ends the note on its target fret.</summary>
    Slide = 1 << 12,

    /// <summary>
    ///     A slide linked into the next note (a legato slide). Recorded, never scored: whether a
    ///     charter keeps the slide on a linked pair is a charting choice, not an importer fault.
    /// </summary>
    LegatoSlide = 1 << 13
}

/// <summary>
///     One struck sound: a single note or one note of a chord. <see cref="Beat" /> counts beats
///     from the start of its measure; <see cref="Chord" /> is the chord it belongs to.
///     <see cref="SourceString" /> is the string the part itself plays it on when
///     <see cref="TabChartStrings" /> has re-spelled it onto another layout.
/// </summary>
public sealed record StruckSound(
    int Measure,
    double Beat,
    FractionalPosition Position,
    int String,
    int Fret,
    int? Midi,
    SoundTechnique Techniques,
    Chord? Chord,
    int? SourceString = null)
{
    /// <summary>The string of the part's own layout.</summary>
    public int TabString => SourceString ?? String;
}

/// <summary>
///     One measure of a part and the sounds struck in it. <see cref="BeatCount" /> beats of
///     <c>1/</c><see cref="Denominator" /> note each.
/// </summary>
public sealed record MeasureSounds(int Index, int BeatCount, IReadOnlyList<StruckSound> Sounds, int Denominator = 4);

/// <summary>
///     Turns a part's hardest level into struck sounds per measure of its own beat map. Linked
///     continuations (the note after a <c>LinkNext</c> on the same string) are not struck and are
///     dropped; a slide-in ornament (the shape the importer materialises a Guitar Pro slide-in
///     as, and EOF charts it: a sixteenth, one to three frets off, landing on the next note) is
///     dropped too and its landing note carries <see cref="SoundTechnique.SlideIn" />.
/// </summary>
public static class TabChartSounds
{
    /// <summary>
    ///     A slide-in origin is at most this long (beats): the importer's lead-in
    ///     (<see cref="GpPostProcessor.SlideInLeadIn" />) and a hair, for a chart's rounding.
    /// </summary>
    public static readonly double SlideInGraceBeats = (double)GpPostProcessor.SlideInLeadIn.Numerator / GpPostProcessor.SlideInLeadIn.Denominator + 0.01;

    /// <summary>The origin's end and its landing note's start may be this far apart (beats).</summary>
    public const double SlideInAbutBeats = 0.05;

    public static List<MeasureSounds> ByMeasure(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var level = arrangement.Levels.MaxBy(l => l.DifficultyIndex) ?? new ChartLevel();
        var grid = MeasureGrid.Build(beats);
        var raw = new List<(FractionalPosition Position, IGuitarSound Sound, Chord? Chord, double Sustain)>();
        foreach (var note in level.Notes.Where(n => !n.Ignore))
            raw.Add((note.Position, note, null, note.Sustain.DoubleValue));
        foreach (var chord in level.Chords.Where(c => !c.Ignore))
        {
            foreach (var cn in chord.ChordNotes.Where(n => !n.Ignore))
                raw.Add((chord.Position, cn, chord, cn.Sustain.DoubleValue));
        }

        raw.Sort((a, b) => a.Position.CompareTo(b.Position) is var c && c != 0 ? c : a.Sound.StringIndex.CompareTo(b.Sound.StringIndex));
        var landing = NextOnString(raw);

        var measures = grid.Select(m => (m.Index, m.BeatCount, m.NoteDenominator, Sounds: new List<StruckSound>())).ToList();
        var lastOnString = new Dictionary<int, (double At, double Sustain, IGuitarSound Sound)>();
        var pendingSlideIn = new HashSet<int>();
        for (var i = 0; i < raw.Count; i++)
        {
            var (position, sound, chord, sustain) = raw[i];
            var at = position.DoubleValue;
            var s = sound.StringIndex;
            var linked = lastOnString.TryGetValue(s, out var previous) && previous.Sound.LinkNext && at - previous.At <= previous.Sustain + 0.01;
            if (!linked && IsSlideInOrnament(raw, i, landing[i]))
            {
                // Not a strike: the note it lands on carries the slide-in, and is not a linked continuation of it.
                pendingSlideIn.Add(s);
                continue;
            }

            lastOnString[s] = (at, sustain, sound);
            if (linked) continue;

            var m = MeasureAt(grid, at);
            if (m < 0) continue;

            var techniques = TechniquesOf(sound);
            if (pendingSlideIn.Remove(s)) techniques |= SoundTechnique.SlideIn;
            measures[m].Sounds.Add(new StruckSound(m, at - grid[m].StartBeat, position, s, sound.Fret,
                SoundingPitch.MidiFor(arrangement, s, sound.Fret), techniques, chord));
        }

        return [.. measures.Select(m => new MeasureSounds(m.Index, m.BeatCount, m.Sounds, m.NoteDenominator))];
    }

    /// <summary>For each sound, the index of the next one on its string, or -1.</summary>
    private static int[] NextOnString(List<(FractionalPosition Position, IGuitarSound Sound, Chord? Chord, double Sustain)> raw)
    {
        var next = new int[raw.Count];
        var following = new Dictionary<int, int>();
        for (var i = raw.Count - 1; i >= 0; i--)
        {
            var s = raw[i].Sound.StringIndex;
            next[i] = following.GetValueOrDefault(s, -1);
            following[s] = i;
        }

        return next;
    }

    /// <summary>
    ///     The shape the importer gives a slide-in origin (<c>GpPostProcessor.MaterializeSlideIns</c>), and
    ///     EOF charts: a pitched, linked note of at most a sixteenth, one to three frets off its landing
    ///     fret, that the note on the landing fret follows at once. A legato slide a player holds longer
    ///     or sets further than that is a note.
    /// </summary>
    private static bool IsSlideInOrnament(List<(FractionalPosition Position, IGuitarSound Sound, Chord? Chord, double Sustain)> raw, int index, int landing)
    {
        var (position, sound, _, sustain) = raw[index];
        if (landing < 0 || !sound.LinkNext || sound.UnpitchedSlide || sound.SlideTo is not { } to || sustain > SlideInGraceBeats) return false;

        var distance = Math.Abs(to - sound.Fret);
        if (distance is < GpPostProcessor.MinSlideInFretDistance or > GpPostProcessor.MaxSlideInFretDistance) return false;

        var (next, target, _, _) = raw[landing];
        return target.Fret == to && Math.Abs(next.DoubleValue - (position.DoubleValue + sustain)) <= SlideInAbutBeats;
    }

    /// <summary>The technique flags of one sound (a slide-in is added by <see cref="ByMeasure" />).</summary>
    public static SoundTechnique TechniquesOf(IGuitarSound sound)
    {
        var t = SoundTechnique.None;
        if (sound.UnpitchedSlide) t |= SoundTechnique.SlideOut;
        else if (sound.SlideTo != null) t |= sound.LinkNext ? SoundTechnique.LegatoSlide : SoundTechnique.Slide;
        if (sound.BendValues.Count > 0) t |= SoundTechnique.Bend;
        t |= sound.Hopo switch
        {
            HopoType.HammerOn => SoundTechnique.HammerOn,
            HopoType.PullOff => SoundTechnique.PullOff,
            HopoType.Tap => SoundTechnique.Tap,
            _ => SoundTechnique.None
        };
        t |= sound.Mute switch
        {
            MuteType.Palm => SoundTechnique.PalmMute,
            MuteType.Full => SoundTechnique.FretHandMute,
            _ => SoundTechnique.None
        };
        t |= sound.Harmonic switch
        {
            HarmonicType.Normal => SoundTechnique.Harmonic,
            HarmonicType.Pinch => SoundTechnique.PinchHarmonic,
            _ => SoundTechnique.None
        };
        if (sound.Vibrato) t |= SoundTechnique.Vibrato;
        if (sound.Tremolo) t |= SoundTechnique.Tremolo;
        return t;
    }

    private static int MeasureAt(IReadOnlyList<MeasureSpan> grid, double beat)
    {
        if (grid.Count == 0 || beat < grid[0].StartBeat) return -1;

        var lo = 0;
        var hi = grid.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (grid[mid].StartBeat <= beat + 1e-9) lo = mid;
            else hi = mid - 1;
        }

        return lo;
    }
}

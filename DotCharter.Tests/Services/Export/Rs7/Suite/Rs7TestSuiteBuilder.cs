using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export.Rs7.Suite;

/// <summary>A chart spec turned into a <see cref="ChartProject" />; <paramref name="EndBeat" /> is where the
/// END phrase and the closing noguitar section sit, <paramref name="SongBeats" /> the song length.</summary>
internal sealed record BuiltChart(ChartProject Project, int SongBeats, int EndBeat)
{
    public double SongLengthMs => SongBeats * Rs7SuiteSpec.BeatMs;
}

internal static class Rs7TestSuiteBuilder
{
    public static BuiltChart Build(ChartSpec spec)
    {
        var end = spec.Parts.Max(p => p.Events.Max(e => e.Beat + Duration(e))) + 1;
        var songBeats = Math.Max(Rs7SuiteSpec.MinBeats, end + 4);
        var beats = Beats(songBeats + 1);
        var project = new ChartProject
        {
            Bpm = Rs7SuiteSpec.Bpm,
            Chart = { SongName = spec.Name, Artist = "DotCharter", AlbumName = "RS7 test suite", AlbumYear = "2026" }
        };
        foreach (var beat in beats) project.Beats.Add(beat);
        for (var i = 0; i < spec.Parts.Length; i++)
            project.Arrangements.Add(Arrangement(spec.Parts[i], ArrangementName(spec, i), end));
        return new BuiltChart(project, songBeats, end);
    }

    public static string ArrangementName(ChartSpec spec, int index)
    {
        var p = spec.Parts[index];
        var same = spec.Parts.Take(index).Count(x => x.Role == p.Role && x.Inst == p.Inst);
        return $"{p.Role} {p.Inst.Key}{(same == 0 ? "" : $" #{same + 1}")}";
    }

    public static List<BeatMarker> Beats(int count)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * Rs7SuiteSpec.BeatMs, IsMeasure = i % 4 == 0, Bpm = Rs7SuiteSpec.Bpm,
                BeatsInMeasure = 4, NoteDenominator = 4
            });
        }

        return beats;
    }

    /// <summary>Beats an event rings (the longest of its columns).</summary>
    public static int Duration(Ev e) => e.Cols.Max(c => Duration(c.Tech));

    public static int Duration(Tech t) =>
        t.HasFlag(Tech.Bend) ? 3
        : (t & (Tech.Sustain | Tech.Slide | Tech.UnpitchedSlide | Tech.Vibrato | Tech.Tremolo)) != 0 ? Rs7SuiteSpec.SustainBeats
        : 0;

    /// <summary>The chart (lowest-first) tuning offsets that sound the part's package tuning.</summary>
    public static int[] DomainTuning(Part p)
    {
        var inst = p.Inst;
        var low = inst.Bass ? StandardOpenStrings.BassLowEMidi : StandardOpenStrings.GuitarLowEMidi;
        var offsets = new int[inst.Strings];
        for (var d = 0; d < inst.Strings; d++)
        {
            var s = Rs7StringLayout.ToSng(d, inst.Strings, inst.Type);
            offsets[d] = Rs7SuiteSpec.Midi(inst, p.Tuning, s, 0) - (low + StandardOpenStrings.SemitonesAboveLowE(inst.Strings, d));
        }

        return offsets;
    }

    private static Arrangement Arrangement(Part p, string name, int end)
    {
        var arrangement = new Arrangement { ArrangementType = p.Role, Name = name, StringCount = p.Inst.Strings };
        foreach (var offset in DomainTuning(p)) arrangement.TuningPitches.Add(new TuningOffset(offset));
        AddStructure(arrangement, p, end);
        var templates = new Dictionary<string, ChordTemplate>();
        for (var level = 0; level < p.Levels; level++)
        {
            var events = p.Events.Where(e => e.Level <= level).OrderBy(e => e.Beat).ToArray();
            var chartLevel = new ChartLevel { DifficultyIndex = level };
            AddAnchors(chartLevel, p, events);
            foreach (var e in events) AddEvent(arrangement, chartLevel, p, e, templates);
            arrangement.Levels.Add(chartLevel);
        }

        return arrangement;
    }

    private static void AddStructure(Arrangement a, Part p, int end)
    {
        var sections = p.Sections ?? [new Sec("riff", Rs7SuiteSpec.LeadInBeats)];
        a.Phrases.Add("COUNT");
        a.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        a.PhraseInfos["COUNT"] = new PhraseInfo(0, false);
        for (var i = 0; i < sections.Length; i++)
        {
            a.Phrases.Add($"p{i}");
            a.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(sections[i].Beat), a.Phrases.Count - 1));
            a.EventPoints.Add(new EventPoint { Position = new FractionalPosition(sections[i].Beat), Section = sections[i].Name });
        }

        a.Phrases.Add("END");
        a.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(end), a.Phrases.Count - 1));
        a.PhraseInfos["END"] = new PhraseInfo(0, false);
        a.EventPoints.Add(new EventPoint { Position = new FractionalPosition(end), Section = "noguitar" });
    }

    private static IEnumerable<int> Frets(Col c)
    {
        yield return c.Fret;
        if (c.Tech.HasFlag(Tech.Slide) || c.Tech.HasFlag(Tech.UnpitchedSlide)) yield return c.Fret + Rs7SuiteSpec.SlideFrets;
    }

    private static void AddAnchors(ChartLevel level, Part p, Ev[] events)
    {
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 1, 4));
        var (fret, width) = (1, 4);
        foreach (var e in events)
        {
            var frets = e.Cols.SelectMany(Frets).Where(f => f > 0).ToArray();
            if (frets.Length == 0) continue;
            var (lo, hi) = (frets.Min(), frets.Max());
            if (lo >= fret && hi < fret + width) continue;
            (fret, width) = (Math.Clamp(lo, 1, 21), Math.Max(4, hi - Math.Clamp(lo, 1, 21) + 1));
            level.Anchors.Add(new FretAnchor(new FractionalPosition(e.Beat), fret, width));
        }
    }

    private static void AddEvent(
        Arrangement a, ChartLevel level, Part p, Ev e, Dictionary<string, ChordTemplate> templates)
    {
        var pos = new FractionalPosition(e.Beat);
        if (e.Chord is null)
        {
            var c = e.Cols[0];
            var note = new GuitarNote { Position = pos, StringIndex = Rs7SuiteSpec.ToDomain(p.Inst, c.String), Fret = c.Fret };
            Apply(note, c, e.Beat);
            level.Notes.Add(note);
            return;
        }

        var key = e.Chord + "|" + string.Join(',', e.Cols.Select(c => $"{c.String}:{c.Fret}:{c.Finger}"));
        if (!templates.TryGetValue(key, out var template))
        {
            template = new ChordTemplate { Id = templates.Count, Name = e.Chord };
            foreach (var c in e.Cols)
            {
                var d = Rs7SuiteSpec.ToDomain(p.Inst, c.String);
                template.Frets[d] = c.Fret;
                if (c.Finger >= 0) template.Fingers[d] = c.Finger;
            }

            templates[key] = template;
            a.ChordTemplates.Add(template);
        }

        var chord = new Chord { Position = pos, TemplateId = template.Id };
        foreach (var c in e.Cols)
        {
            var cn = new ChordNote { StringIndex = Rs7SuiteSpec.ToDomain(p.Inst, c.String), Fret = c.Fret };
            Apply(cn, c, e.Beat);
            chord.ChordNotes.Add(cn);
        }

        level.Chords.Add(chord);
    }

    private static void Apply(IGuitarSound s, Col c, int beat)
    {
        var t = c.Tech;
        var dur = Duration(t);
        if (dur > 0) s.Sustain = new FractionalPosition(dur);
        if (s is ChordNote cn && dur > 0) cn.ExplicitSustain = true;
        if (t.HasFlag(Tech.Slide) || t.HasFlag(Tech.UnpitchedSlide)) s.SlideTo = c.Fret + Rs7SuiteSpec.SlideFrets;
        s.UnpitchedSlide = t.HasFlag(Tech.UnpitchedSlide);
        if (t.HasFlag(Tech.Bend))
        {
            s.BendValues.Add(new BendValue(new FractionalPosition(beat + 1), 2.0));
            s.BendValues.Add(new BendValue(new FractionalPosition(beat + 2), 0.0));
        }

        if (t.HasFlag(Tech.Vibrato))
        {
            s.Vibrato = true;
            s.VibratoStrength = 80;
        }

        s.Tremolo = t.HasFlag(Tech.Tremolo);
        s.Accent = t.HasFlag(Tech.Accent);
        if (t.HasFlag(Tech.NaturalHarmonic)) s.Harmonic = HarmonicType.Normal;
        if (t.HasFlag(Tech.PinchHarmonic)) s.Harmonic = HarmonicType.Pinch;
        if (t.HasFlag(Tech.PalmMute)) s.Mute = MuteType.Palm;
        if (t.HasFlag(Tech.HammerOn)) s.Hopo = HopoType.HammerOn;
        if (t.HasFlag(Tech.PullOff)) s.Hopo = HopoType.PullOff;
        if (t.HasFlag(Tech.Tap)) s.Hopo = HopoType.Tap;
        if (t.HasFlag(Tech.Slap)) s.BassPicking = BassPickingType.Slap;
        if (t.HasFlag(Tech.Pop)) s.BassPicking = BassPickingType.Pop;
    }
}

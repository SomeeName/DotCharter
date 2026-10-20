using System.Globalization;
using System.Xml.Linq;

namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>
///     Reads an RS2014 instrumental XML into a <see cref="DdArrangement" /> and writes
///     what the generator changed back into the same document. Reading follows
///     Rocksmith2014.XML (MIT, Copyright (c) 2020 Tapio Malmberg): the same attribute
///     names, the same defaults, the same flag bits and the same time parse — a time is
///     cut, not rounded, to three decimals.
///     <para>
///         Writing replaces <c>phrases</c>, <c>phraseIterations</c>, <c>newLinkedDiffs</c>,
///         <c>chordTemplates</c> and <c>levels</c> and leaves every other element alone. A
///         note, chord, anchor or template that came from the file is written from a copy of
///         its own element, so an attribute the generator never reads (bend values, strum,
///         pick direction) survives; only what the generator can change is rewritten.
///     </para>
/// </summary>
internal static class DdXml
{
    public static DdArrangement Read(XElement song, int stringSlots = 6)
    {
        var levels = song.Element("levels")?.Elements("level")
            .Select(l => ReadLevel(l))
            .ToList() ?? [];
        var properties = song.Element("arrangementProperties");
        var path = properties == null ? 2 : Flag(properties, "pathLead") ? 0 : Flag(properties, "pathRhythm") ? 1 : 2;
        return new DdArrangement
        {
            StringSlots = stringSlots,
            SongLength = ParseTime(song.Element("songLength")?.Value ?? "0"),
            AverageTempo = song.Element("averageTempo") is { } tempo ? float.Parse(tempo.Value, CultureInfo.InvariantCulture) : 120f,
            Path = path,
            Beats = [.. Children(song, "ebeats", "ebeat").Select(b => new DdBeat(Time(b, "time"), Int(b, "measure", -1)))],
            Sections = [.. Children(song, "sections", "section").Select(s => new DdSection(Str(s, "name"), Time(s, "startTime"), s))],
            Events = [.. Children(song, "events", "event").Select(e => new DdEvent(Str(e, "code"), Time(e, "time"), e))],
            Phrases = [.. Children(song, "phrases", "phrase").Select(p => new DdPhrase(Str(p, "name"), Int(p, "maxDifficulty"), Flag(p, "solo"), p))],
            PhraseIterations = [.. Children(song, "phraseIterations", "phraseIteration").Select(ReadIteration)],
            NewLinkedDiffs = [.. Children(song, "newLinkedDiffs", "newLinkedDiff").Select(d => new DdNewLinkedDiff(
                Int(d, "levelBreak", -1), [.. d.Elements("nld_phrase").Select(p => Int(p, "id"))]))],
            ChordTemplates = [.. Children(song, "chordTemplates", "chordTemplate").Select(t => ReadTemplate(t, stringSlots))],
            Levels = levels,
        };
    }

    /// <summary>Replaces everything the improvements and the generator can change in
    /// <paramref name="song" />. The beats are not written: no step that runs here moves them.</summary>
    public static void Write(XElement song, DdArrangement arr)
    {
        Replace(song, "phrases", Counted("phrases", arr.Phrases.Select(WritePhrase)));
        Replace(song, "phraseIterations", Counted("phraseIterations", arr.PhraseIterations.Select(WriteIteration)));
        Replace(song, "newLinkedDiffs", Counted("newLinkedDiffs", arr.NewLinkedDiffs.Select(d => new XElement("newLinkedDiff",
            new XAttribute("levelBreak", Num(d.LevelBreak)), new XAttribute("ratio", "1.000"),
            new XAttribute("phraseCount", Num(d.PhraseIds.Count)),
            d.PhraseIds.Select(id => new XElement("nld_phrase", new XAttribute("id", Num(id))))))));
        Replace(song, "chordTemplates", Counted("chordTemplates", arr.ChordTemplates.Select(t => WriteTemplate(t, arr.StringSlots))));
        Replace(song, "levels", Counted("levels", arr.Levels.Select(WriteLevel)));
        Replace(song, "sections", Counted("sections", arr.Sections.Select(s =>
        {
            var el = s.Source != null ? new XElement(s.Source) : new XElement("section", new XAttribute("name", s.Name), new XAttribute("number", "1"));
            el.SetAttributeValue("startTime", FormatTime(s.Time));
            return el;
        })));
        Replace(song, "events", Counted("events", arr.Events.Select(e =>
        {
            var el = e.Source != null ? new XElement(e.Source) : new XElement("event");
            el.SetAttributeValue("time", FormatTime(e.Time));
            el.SetAttributeValue("code", e.Code);
            return el;
        })));
    }

    private static XElement WritePhrase(DdPhrase p)
    {
        var el = p.Source != null
            ? new XElement(p.Source)
            : new XElement("phrase", new XAttribute("disparity", "0"), new XAttribute("ignore", "0"),
                new XAttribute("maxDifficulty", "0"), new XAttribute("name", ""), new XAttribute("solo", p.IsSolo ? "1" : "0"));
        el.SetAttributeValue("maxDifficulty", Num(p.MaxDifficulty));
        el.SetAttributeValue("name", p.Name);
        return el;
    }

    // ── reading ─────────────────────────────────────────────────────────

    private static IEnumerable<XElement> Children(XElement song, string list, string item) =>
        song.Element(list)?.Elements(item) ?? [];

    private static DdPhraseIteration ReadIteration(XElement el)
    {
        int easy = 0, medium = 0, hard = 0;
        foreach (var hero in el.Element("heroLevels")?.Elements("heroLevel") ?? [])
        {
            var difficulty = Int(hero, "difficulty");
            switch (Int(hero, "hero"))
            {
                case 1: easy = difficulty; break;
                case 2: medium = difficulty; break;
                case 3: hard = difficulty; break;
            }
        }

        return new DdPhraseIteration(Time(el, "time"), Int(el, "phraseId"), new DdHeroLevels(easy, medium, hard));
    }

    private static DdChordTemplate ReadTemplate(XElement el, int slots)
    {
        var frets = Enumerable.Repeat(-1, slots).ToArray();
        var fingers = Enumerable.Repeat(-1, slots).ToArray();
        for (var i = 0; i < slots; i++)
        {
            frets[i] = Int(el, "fret" + i.ToString(CultureInfo.InvariantCulture), -1);
            fingers[i] = Int(el, "finger" + i.ToString(CultureInfo.InvariantCulture), -1);
        }

        return new DdChordTemplate(Str(el, "chordName"), Str(el, "displayName"), fingers, frets) { Source = el };
    }

    private static DdLevel ReadLevel(XElement el) => new(Int(el, "difficulty", -1))
    {
        Notes = [.. Children(el, "notes", "note").Select(ReadNote)],
        Chords = [.. Children(el, "chords", "chord").Select(ReadChord)],
        Anchors = [.. Children(el, "anchors", "anchor").Select(a => new DdAnchor(Time(a, "time"), Int(a, "fret"),
            (int)float.Parse(a.Attribute("width")?.Value ?? "4", CultureInfo.InvariantCulture), a))],
        HandShapes = [.. Children(el, "handShapes", "handShape").Select(h => new DdHandShape(Int(h, "chordId"), Time(h, "startTime"), Time(h, "endTime")))],
    };

    private static DdChord ReadChord(XElement el)
    {
        var mask = DdChordMask.None;
        if (Flag(el, "linkNext")) mask |= DdChordMask.LinkNext;
        if (Flag(el, "accent")) mask |= DdChordMask.Accent;
        if (Flag(el, "fretHandMute")) mask |= DdChordMask.FretHandMute;
        if (Flag(el, "highDensity")) mask |= DdChordMask.HighDensity;
        if (Flag(el, "ignore")) mask |= DdChordMask.Ignore;
        if (Flag(el, "palmMute")) mask |= DdChordMask.PalmMute;
        if (Flag(el, "hopo")) mask |= DdChordMask.Hopo;
        var chordNotes = el.Elements("chordNote").Select(ReadNote).ToList();
        return new DdChord
        {
            Time = Time(el, "time"),
            ChordId = Int(el, "chordId"),
            Mask = mask,
            ChordNotes = chordNotes.Count > 0 ? chordNotes : null,
            Source = el,
        };
    }

    private static DdNote ReadNote(XElement el)
    {
        var mask = DdNoteMask.None;
        (string Name, DdNoteMask Bit)[] binary =
        [
            ("linkNext", DdNoteMask.LinkNext), ("accent", DdNoteMask.Accent), ("hammerOn", DdNoteMask.HammerOn),
            ("harmonic", DdNoteMask.Harmonic), ("ignore", DdNoteMask.Ignore), ("mute", DdNoteMask.FretHandMute),
            ("palmMute", DdNoteMask.PalmMute), ("pullOff", DdNoteMask.PullOff), ("tremolo", DdNoteMask.Tremolo),
            ("harmonicPinch", DdNoteMask.PinchHarmonic), ("pickDirection", DdNoteMask.PickDirection),
        ];
        foreach (var (name, bit) in binary)
        {
            if (Flag(el, name)) mask |= bit;
        }

        // slap / pluck / rightHand are numbers where -1 means "none".
        if (Int(el, "slap", -1) != -1) mask |= DdNoteMask.Slap;
        if (Int(el, "pluck", -1) != -1) mask |= DdNoteMask.Pluck;
        if (Int(el, "rightHand", -1) != -1) mask |= DdNoteMask.RightHand;

        return new DdNote
        {
            Mask = mask,
            Time = Time(el, "time"),
            Sustain = Time(el, "sustain"),
            String = Int(el, "string"),
            Fret = Int(el, "fret"),
            LeftHand = Int(el, "leftHand", -1),
            SlideTo = Int(el, "slideTo", -1),
            SlideUnpitchTo = Int(el, "slideUnpitchTo", -1),
            Tap = Int(el, "tap"),
            Vibrato = Int(el, "vibrato"),
            MaxBend = el.Attribute("bend") is { } bend ? float.Parse(bend.Value, CultureInfo.InvariantCulture) : 0f,
            BendValues = el.Element("bendValues") is { } bends
                ? [.. bends.Elements("bendValue").Select(b => new DdBendValue(Time(b, "time"),
                    b.Attribute("step") is { } step ? float.Parse(step.Value, CultureInfo.InvariantCulture) : 0f))]
                : null,
            Source = el,
        };
    }

    // ── writing ─────────────────────────────────────────────────────────

    private static XElement WriteIteration(DdPhraseIteration pi)
    {
        var el = pi.Source != null
            ? new XElement(pi.Source.Name, pi.Source.Attributes())
            : new XElement("phraseIteration", new XAttribute("variation", ""));
        el.SetAttributeValue("time", FormatTime(pi.Time));
        el.SetAttributeValue("phraseId", Num(pi.PhraseId));
        if (pi.HeroLevels != DdHeroLevels.None)
        {
            el.Add(new XElement("heroLevels", new XAttribute("count", "3"),
                Hero(1, pi.HeroLevels.Easy), Hero(2, pi.HeroLevels.Medium), Hero(3, pi.HeroLevels.Hard)));
        }

        return el;

        static XElement Hero(int hero, int difficulty) =>
            new("heroLevel", new XAttribute("hero", Num(hero)), new XAttribute("difficulty", Num(difficulty)));
    }

    private static XElement WriteTemplate(DdChordTemplate t, int slots)
    {
        var el = t.Source != null ? new XElement(t.Source) : new XElement("chordTemplate");
        el.SetAttributeValue("chordName", t.Name);
        el.SetAttributeValue("displayName", t.DisplayName);
        for (var i = 0; i < slots; i++)
            el.SetAttributeValue("finger" + Num(i), Num(t.Fingers[i]));
        for (var i = 0; i < slots; i++)
            el.SetAttributeValue("fret" + Num(i), Num(t.Frets[i]));
        return el;
    }

    private static XElement WriteLevel(DdLevel level) => new("level", new XAttribute("difficulty", Num(level.Difficulty)),
        Counted("notes", level.Notes.Select(n => WriteNote(n, "note"))),
        Counted("chords", level.Chords.Select(WriteChord)),
        new XElement("fretHandMutes", new XAttribute("count", "0")),
        Counted("anchors", level.Anchors.Select(a =>
        {
            var el = a.Source != null ? new XElement(a.Source) : new XElement("anchor");
            el.SetAttributeValue("time", FormatTime(a.Time));
            el.SetAttributeValue("fret", Num(a.Fret));
            el.SetAttributeValue("width", a.Width.ToString("F3", CultureInfo.InvariantCulture));
            return el;
        })),
        Counted("handShapes", level.HandShapes.Select(h => new XElement("handShape",
            new XAttribute("chordId", Num(h.ChordId)), new XAttribute("endTime", FormatTime(h.EndTime)),
            new XAttribute("startTime", FormatTime(h.StartTime))))));

    private static XElement WriteChord(DdChord chord)
    {
        var el = chord.Source != null
            ? new XElement(chord.Source.Name, chord.Source.Attributes())
            : new XElement("chord");
        el.SetAttributeValue("time", FormatTime(chord.Time));
        el.SetAttributeValue("chordId", Num(chord.ChordId));
        el.SetAttributeValue("linkNext", chord.IsLinkNext ? "1" : null);
        el.SetAttributeValue("ignore", chord.IsIgnore ? "1" : null);
        foreach (var cn in chord.ChordNotes ?? [])
            el.Add(WriteNote(cn, "chordNote"));
        return el;
    }

    private static XElement WriteNote(DdNote note, string name)
    {
        var el = note.Source != null ? new XElement(note.Source) { Name = name } : new XElement(name);
        el.SetAttributeValue("time", FormatTime(note.Time));
        el.SetAttributeValue("string", Num(note.String));
        el.SetAttributeValue("fret", Num(note.Fret));
        el.SetAttributeValue("sustain", FormatTime(note.Sustain));
        el.SetAttributeValue("leftHand", note.LeftHand != -1 ? Num(note.LeftHand) : null);
        el.SetAttributeValue("slideTo", note.SlideTo != -1 ? Num(note.SlideTo) : null);
        el.SetAttributeValue("slideUnpitchTo", note.SlideUnpitchTo != -1 ? Num(note.SlideUnpitchTo) : null);
        el.SetAttributeValue("vibrato", note.Vibrato != 0 ? Num(note.Vibrato) : null);
        el.SetAttributeValue("tap", note.Tap != 0 ? Num(note.Tap) : null);
        el.SetAttributeValue("bend", note.MaxBend > 0f ? note.MaxBend.ToString("F3", CultureInfo.InvariantCulture) : null);
        el.Element("bendValues")?.Remove();
        if (note.BendValues is { Count: > 0 } bends)
        {
            el.Add(new XElement("bendValues", new XAttribute("count", Num(bends.Count)),
                bends.Select(b => new XElement("bendValue", new XAttribute("time", FormatTime(b.Time)),
                    new XAttribute("step", b.Step.ToString("F3", CultureInfo.InvariantCulture))))));
        }
        Bit(el, note, "linkNext", DdNoteMask.LinkNext);
        Bit(el, note, "accent", DdNoteMask.Accent);
        Bit(el, note, "hammerOn", DdNoteMask.HammerOn);
        Bit(el, note, "harmonic", DdNoteMask.Harmonic);
        Bit(el, note, "ignore", DdNoteMask.Ignore);
        Bit(el, note, "mute", DdNoteMask.FretHandMute);
        Bit(el, note, "palmMute", DdNoteMask.PalmMute);
        Bit(el, note, "pullOff", DdNoteMask.PullOff);
        Bit(el, note, "tremolo", DdNoteMask.Tremolo);
        Bit(el, note, "harmonicPinch", DdNoteMask.PinchHarmonic);
        el.SetAttributeValue("hopo", note.IsHopo ? "1" : null);
        // Numbered flags keep the value they were read with; one the generator turned
        // off loses its attribute (a pick direction of 0, "down", is not a set bit).
        Kept(el, note, "pickDirection", DdNoteMask.PickDirection, v => v.Length > 0 && v[0] != '0');
        Kept(el, note, "slap", DdNoteMask.Slap, v => v.Trim() != "-1");
        Kept(el, note, "pluck", DdNoteMask.Pluck, v => v.Trim() != "-1");
        Kept(el, note, "rightHand", DdNoteMask.RightHand, v => v.Trim() != "-1");
        return el;

        static void Bit(XElement el, DdNote n, string attr, DdNoteMask bit) =>
            el.SetAttributeValue(attr, (n.Mask & bit) != 0 ? "1" : null);

        static void Kept(XElement el, DdNote n, string attr, DdNoteMask bit, Func<string, bool> readsAsOn)
        {
            var current = el.Attribute(attr)?.Value;
            if ((n.Mask & bit) != 0)
            {
                if (current == null || !readsAsOn(current)) el.SetAttributeValue(attr, "1");
            }
            else if (current != null && readsAsOn(current))
            {
                el.SetAttributeValue(attr, null);
            }
        }
    }

    private static XElement Counted(string name, IEnumerable<XElement> items)
    {
        var list = items.ToList();
        return new XElement(name, new XAttribute("count", Num(list.Count)), list);
    }

    private static void Replace(XElement song, string name, XElement replacement)
    {
        var existing = song.Element(name);
        if (existing != null) existing.ReplaceWith(replacement);
        else song.Add(replacement);
    }

    // ── scalars ─────────────────────────────────────────────────────────

    /// <summary>Rocksmith2014.XML's <c>Utils.TimeCodeFromFloatString</c>: seconds to
    /// whole milliseconds, the digits past the third decimal cut off.</summary>
    public static int ParseTime(string input)
    {
        input = input.Trim();
        var dot = input.IndexOf('.');
        if (dot == -1) return int.Parse(input, CultureInfo.InvariantCulture) * 1000;
        var decimals = input.Substring(dot + 1, Math.Min(input.Length - 1 - dot, 3)).PadRight(3, '0');
        return int.Parse(string.Concat(input.AsSpan(0, dot), decimals), CultureInfo.InvariantCulture);
    }

    public static string FormatTime(int ms) =>
        ms < 0 ? $"-{-ms / 1000}.{-ms % 1000:D3}" : $"{ms / 1000}.{ms % 1000:D3}";

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int Time(XElement el, string name) => el.Attribute(name) is { } a ? ParseTime(a.Value) : 0;

    private static int Int(XElement el, string name, int def = 0) =>
        el.Attribute(name) is { } a ? int.Parse(a.Value.Trim(), CultureInfo.InvariantCulture) : def;

    private static string Str(XElement el, string name) => el.Attribute(name)?.Value ?? "";

    /// <summary>The library's <c>ParseBinary</c>: it subtracts '0' from the first
    /// character as an unsigned char, so anything but a leading '0' is on ("-1" too).</summary>
    private static bool Flag(XElement el, string name) =>
        el.Attribute(name)?.Value is { Length: > 0 } v && v[0] != '0';
}

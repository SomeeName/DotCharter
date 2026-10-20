using System.Globalization;
using System.Text.Json;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     The accuracy floors of generated FHP and chord fingering against the owner's finished charts, in the
///     DEFAULT gate (#488 → #502): the corpus is owner-local, so without this a regression only shows when someone
///     reruns <c>tools/chart-eval</c>. <c>Fixtures/chart-style-floor.json</c> is 24 windows of 48 struck positions
///     per instrument, written by <c>tools/chart-eval --fixture</c>: frets, hand-moving techniques, chord shapes with
///     the charter's fingers, hand-shape spans and the charter's anchors, at ORDINAL positions — nothing that names
///     or dates a song.
///     <para>
///         Each window runs the production pipeline exactly as <c>tools/chart-eval</c> scores it (<c>auto</c>
///         fingers, <c>--free-fingering suggested</c>): suggested fingers on every template, the walk with the free
///         grip resolver, then <see cref="ChordFingeringFollower" />. A power chord or two-fret octave follows the
///         arrangement's own 1·3 / 1·4 (#511) as chart-eval's default <c>--style prefix</c> measures it: from the
///         charter's fingering of the shapes first played EARLIER in the window, never its own. The floors are the counts measured when the
///         fixture was written; a change that LOSES one sound or chord fails here. A change that gains raises the
///         floor in the same commit (the test prints the counts); a change that trades some for more is measured on
///         the corpus and then re-floored — that is the point of a floor, not an obstacle to it.
///     </para>
/// </summary>
public sealed class ChartStyleFloorTests(ITestOutputHelper output)
{
    // Measured 2026-10-09 on the #502 tree (fixture of the same commit); fingering raised by #511's style, 571 -> 581.
    private const int GuitarFhpFloor = 998;
    private const int BassFhpFloor = 992;
    private const int GuitarFingeringFloor = 581;

    private static readonly Lazy<Totals> Measured = new(Measure);

    [Fact]
    public void GuitarFhp_HoldsItsFloor()
    {
        var t = Measured.Value;
        output.WriteLine($"guitar FHP {t.GuitarSame} of {t.GuitarSounds} ({Pct(t.GuitarSame, t.GuitarSounds)})");
        Assert.True(t.GuitarSame >= GuitarFhpFloor, $"guitar FHP {t.GuitarSame} of {t.GuitarSounds} fell below {GuitarFhpFloor}");
    }

    [Fact]
    public void BassFhp_HoldsItsFloor()
    {
        var t = Measured.Value;
        output.WriteLine($"bass FHP {t.BassSame} of {t.BassSounds} ({Pct(t.BassSame, t.BassSounds)})");
        Assert.True(t.BassSame >= BassFhpFloor, $"bass FHP {t.BassSame} of {t.BassSounds} fell below {BassFhpFloor}");
    }

    [Fact]
    public void ChordFingeringAfterFhp_HoldsItsFloor()
    {
        var t = Measured.Value;
        output.WriteLine($"chord fingering after FHP {t.FingeringExact} of {t.Chords} ({Pct(t.FingeringExact, t.Chords)})");
        Assert.True(t.FingeringExact >= GuitarFingeringFloor,
            $"chord fingering {t.FingeringExact} of {t.Chords} fell below {GuitarFingeringFloor}");
    }

    [Fact]
    public void TheFixtureIsTheShapeTheFloorsWereMeasuredOn()
    {
        // A truncated or regenerated-by-mistake fixture would make every floor pass or fail for the wrong reason.
        var t = Measured.Value;
        Assert.Equal(24 * 48, t.GuitarSounds);
        Assert.Equal(24 * 48, t.BassSounds);
        Assert.True(t.Chords > 400, $"only {t.Chords} fully fingered chords in the fixture");
    }

    private static string Pct(int a, int b) => (100.0 * a / Math.Max(1, b)).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private sealed record Totals(int GuitarSounds, int GuitarSame, int BassSounds, int BassSame, int Chords, int FingeringExact);

    private static Totals Measure()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "Editing", "FretAnchors", "Fixtures", "chart-style-floor.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int gs = 0, gSame = 0, bs = 0, bSame = 0, chords = 0, exact = 0;
        foreach (var w in doc.RootElement.GetProperty("windows").EnumerateArray())
        {
            var r = Window.Run(w);
            if (r.Bass) { bs += r.Sounds; bSame += r.Same; }
            else { gs += r.Sounds; gSame += r.Same; }
            chords += r.Chords;
            exact += r.Exact;
        }

        return new Totals(gs, gSame, bs, bSame, chords, exact);
    }

    /// <summary>One fixture window rebuilt as a level and scored the way <c>tools/chart-eval</c> scores a chart.</summary>
    private static class Window
    {
        public static (bool Bass, int Sounds, int Same, int Chords, int Exact) Run(JsonElement w)
        {
            var bass = w.GetProperty("kind").GetString() == "bass";
            var strings = w.GetProperty("strings").GetInt32();
            var arr = new Arrangement
            {
                StringCount = strings,
                Capo = w.GetProperty("capo").GetInt32(),
                ArrangementType = bass ? ArrangementType.Bass : ArrangementType.Lead
            };
            foreach (var step in w.GetProperty("tuning").EnumerateArray()) arr.TuningPitches.Add(new TuningOffset(step.GetInt32()));

            var level = new ChartLevel();
            var templates = new Dictionary<string, ChordTemplate>(StringComparer.Ordinal);
            var expected = new List<(FractionalPosition Pos, int Anchor)>();
            var chordFingers = new List<(Chord Chord, Dictionary<int, int> Fingers)>();
            // The charter's fingering of each shape first played so far (#511's style comes from these alone).
            var authored = new List<ChordTemplate>();
            var hints = new Dictionary<ChordTemplate, PairFingering?>(ReferenceEqualityComparer.Instance);
            var ordinal = 0;
            foreach (var s in w.GetProperty("sounds").EnumerateArray())
            {
                var pos = At(ordinal++);
                expected.Add((pos, s.GetProperty("a").GetInt32()));
                if (s.TryGetProperty("n", out var n))
                {
                    foreach (var token in n.GetString()!.Split(','))
                    {
                        var note = new GuitarNote { Position = pos };
                        Parse(token, pos, note);
                        level.Notes.Add(note);
                    }
                }

                if (!s.TryGetProperty("c", out var c)) continue;
                var chord = new Chord { Position = pos };
                foreach (var token in c.GetString()!.Split(','))
                {
                    var cn = new ChordNote();
                    Parse(token, pos, cn);
                    chord.ChordNotes.Add(cn);
                }

                var key = string.Join(",", chord.ChordNotes.OrderBy(x => x.StringIndex).Select(x => $"{x.StringIndex}:{x.Fret}"));
                var isNew = !templates.TryGetValue(key, out var template);
                if (isNew)
                {
                    template = new ChordTemplate { Id = templates.Count };
                    foreach (var cn in chord.ChordNotes) template.Frets[cn.StringIndex] = cn.Fret;
                    var hint = ArrangementFingeringStyle.From(arr, authored).HintFor(arr, template);
                    ChordTemplateFingerSetter.SetSuggestedFingers(template, strings, hint);
                    hints[template] = hint;
                    templates[key] = template;
                    arr.ChordTemplates.Add(template);
                }

                chord.TemplateId = template!.Id;
                level.Chords.Add(chord);
                if (!s.TryGetProperty("f", out var f)) continue;
                var fingers = f.GetString()!.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
                var byString = new Dictionary<int, int>();
                var complete = true;
                for (var i = 0; i < chord.ChordNotes.Count; i++)
                {
                    if (chord.ChordNotes[i].Fret <= 0) continue;
                    if (fingers[i] < 0) complete = false;
                    byString[chord.ChordNotes[i].StringIndex] = fingers[i];
                }

                if (complete && byString.Count > 0) chordFingers.Add((chord, byString));
                if (isNew && complete && byString.Count > 0)
                    authored.Add(new ChordTemplate { Frets = new Dictionary<int, int>(template.Frets), Fingers = byString });
            }

            foreach (var shape in w.GetProperty("shapes").EnumerateArray())
            {
                var start = AtOrdinal(shape[0].GetDouble());
                var end = AtOrdinal(shape[1].GetDouble());
                if (end > start) level.HandShapes.Add(new HandShape { Position = start, EndPosition = end });
            }

            var all = arr.ChordTemplates.ToList();
            var free = ChordFingeringFollower.FreeGripResolver(arr, null, t => hints.GetValueOrDefault(t));
            var anchors = new FretAnchorGenerationService()
                .GenerateAnchors(level, chordTemplates: all, capo: arr.Capo, bassArrangement: bass, freeGrip: free, tuning: arr)
                .OrderBy(a => a.Position).ToList();

            var same = expected.Count(e => Governing(anchors, e.Pos) == e.Anchor);

            var plan = ChordFingeringFollower.Plan(arr, level, anchors, free, new FingeringGenerationService());
            var finalId = new Dictionary<Chord, int>(ReferenceEqualityComparer.Instance);
            foreach (var (chord, _, newId) in plan.Chords) finalId[chord] = newId;
            var byId = all.Concat(plan.MintedTemplates).ToDictionary(t => t.Id);
            var exact = chordFingers.Count(cf =>
            {
                var t = byId[finalId.TryGetValue(cf.Chord, out var id) ? id : cf.Chord.TemplateId];
                return cf.Fingers.All(kv => t.Fingers.TryGetValue(kv.Key, out var g) && g == kv.Value);
            });
            return (bass, expected.Count, same, chordFingers.Count, exact);
        }

        private static FractionalPosition At(int ordinal) => new(ordinal);

        // x.5 is a slide landing or a hand-shape edge between two sounds.
        private static FractionalPosition AtOrdinal(double ordinal)
        {
            var whole = (int)Math.Floor(Math.Max(0, ordinal));
            return ordinal - whole >= 0.25 ? new FractionalPosition(whole, new Fraction(1, 2)) : new FractionalPosition(whole);
        }

        private static int? Governing(List<FretAnchor> anchors, FractionalPosition pos)
        {
            int? fret = null;
            foreach (var a in anchors)
            {
                if (a.Position > pos) break;
                fret = a.Fret;
            }

            return fret;
        }

        // "s:f" then s<to>@<landing ordinal> (pitched slide), u<to> (unpitched), h / p / t.
        private static void Parse(string token, FractionalPosition at, IGuitarSound sound)
        {
            var colon = token.IndexOf(':');
            sound.StringIndex = int.Parse(token[..colon], CultureInfo.InvariantCulture);
            var i = colon + 1;
            var j = i;
            while (j < token.Length && char.IsDigit(token[j])) j++;
            sound.Fret = int.Parse(token[i..j], CultureInfo.InvariantCulture);
            while (j < token.Length)
            {
                var kind = token[j++];
                switch (kind)
                {
                    case 's' or 'u':
                    {
                        var k = j;
                        while (k < token.Length && char.IsDigit(token[k])) k++;
                        sound.SlideTo = int.Parse(token[j..k], CultureInfo.InvariantCulture);
                        sound.UnpitchedSlide = kind == 'u';
                        j = k;
                        if (kind == 's' && j < token.Length && token[j] == '@')
                        {
                            var e = ++j;
                            while (e < token.Length && (char.IsDigit(token[e]) || token[e] is '.' or '-')) e++;
                            var landing = AtOrdinal(double.Parse(token[j..e], CultureInfo.InvariantCulture));
                            sound.Sustain = landing > at ? landing - at : new FractionalPosition(0, new Fraction(1, 4));
                            j = e;
                        }

                        break;
                    }
                    case 'h':
                        sound.Hopo = HopoType.HammerOn;
                        break;
                    case 'p':
                        sound.Hopo = HopoType.PullOff;
                        break;
                    case 't':
                        sound.Hopo = HopoType.Tap;
                        break;
                }
            }
        }
    }
}

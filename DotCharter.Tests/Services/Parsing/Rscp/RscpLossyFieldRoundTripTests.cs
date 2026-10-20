using System.Reflection;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     Five domain fields survived every OTHER format but were dropped by the
///     rscp writer, so "import a CDLC → save → reopen" quietly lost them and
///     the next export shipped the loss:
///     <list type="bullet">
///         <item><c>VibratoStrength</c> — the parser had always READ it; nothing wrote it.</item>
///         <item><c>PhraseIterations</c> — silently fell back to the derived set.</item>
///         <item><c>ArrangementProperties</c> — all 31 RS-XML technique/path flags.</item>
///         <item><c>ToneBase</c>/<c>ToneA..D</c> — the whole tone assignment.</item>
///         <item><c>FretHandMuteTemplates</c> — kept out of the regular template set, so no node carried them.</item>
///     </list>
///     Every addition is an ADDITIVE attribute/node absent at its default, so a
///     chart that never carried the field stays byte-identical (nd/gf/ss/
///     dcNotationVoices precedent) — pinned below.
/// </summary>
public sealed class RscpLossyFieldRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_lossy_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.rscp");

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private ChartProject SaveAndReload(ChartProject project)
    {
        new RscpChartSerializer().Save(ProjectPath, project);
        return new RscpChartSerializer().Load(ProjectPath);
    }

    private static Arrangement NewArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    [Fact]
    public void VibratoStrength_RoundTrips_OnNotesAndChordNotes()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.Levels[0].Notes.Add(new GuitarNote { Fret = 5, Vibrato = true, VibratoStrength = 80 });

        var chord = new Chord { TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7, Vibrato = true, VibratoStrength = 40 });
        arrangement.Levels[0].Chords.Add(chord);
        project.Arrangements.Add(arrangement);

        var level = SaveAndReload(project).Arrangements[0].Levels[0];

        Assert.Equal(80, level.Notes.Single().VibratoStrength);
        Assert.Equal(40, level.Chords.Single().ChordNotes.Single().VibratoStrength);
    }

    [Fact]
    public void ToneSlots_RoundTrip()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.ToneBase = "clean_base";
        arrangement.ToneA = "lead_a";
        arrangement.ToneD = "solo_d";
        project.Arrangements.Add(arrangement);

        var loaded = SaveAndReload(project).Arrangements[0];

        Assert.Equal("clean_base", loaded.ToneBase);
        Assert.Equal("lead_a", loaded.ToneA);
        Assert.Equal("solo_d", loaded.ToneD);
        Assert.Equal("", loaded.ToneB);
    }

    [Fact]
    public void PhraseIterations_RoundTrip()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.Phrases.Add("intro");
        arrangement.Phrases.Add("verse");
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4, Fraction.Zero), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8, Fraction.Zero), 0));
        project.Arrangements.Add(arrangement);

        var loaded = SaveAndReload(project).Arrangements[0];

        Assert.Equal(2, loaded.PhraseIterations.Count);
        Assert.Equal(1, loaded.PhraseIterations[0].PhraseId);
        Assert.Equal(4, loaded.PhraseIterations[0].Position.BeatId);
        Assert.Equal(0, loaded.PhraseIterations[1].PhraseId);
        Assert.Equal(8, loaded.PhraseIterations[1].Position.BeatId);
    }

    [Fact]
    public void ArrangementProperties_RoundTripEveryFlag()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();

        // Set every flag to the OPPOSITE of its default, so a flag missing from
        // either half of the shared map fails here rather than in the field.
        var defaults = new ArrangementProperties();
        foreach (var (_, get, set) in GetFlagAccessors())
            set(arrangement.Properties, get(defaults) == 0 ? 1 : 0);

        project.Arrangements.Add(arrangement);

        var loaded = SaveAndReload(project).Arrangements[0];

        foreach (var (name, get, _) in GetFlagAccessors())
        {
            Assert.True(get(loaded.Properties) == (get(defaults) == 0 ? 1 : 0),
                $"ArrangementProperties.{name} did not survive the round trip.");
        }
    }

    /// <summary>
    ///     <see cref="GetFlagAccessors" /> is itself a hand-maintained parallel
    ///     list, and its omissions are the invisible kind: a flag added to
    ///     <see cref="ArrangementProperties" /> and forgotten here simply is not
    ///     exercised, so the test above keeps passing while the new flag round-trips
    ///     nowhere. Reflection closes the loop — the accessor list is compared
    ///     against the TYPE, not against another list.
    /// </summary>
    [Fact]
    public void TheFlagAccessorList_NamesEveryTechniqueFlagOnTheType()
    {
        var named = GetFlagAccessors().Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var declared = typeof(ArrangementProperties)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(int) && p.GetSetMethod() is not null)
            .Select(p => p.Name)
            .ToList();

        Assert.True(declared.Count >= 30, $"Only {declared.Count} int flags were found on the type.");
        var missing = declared.Where(name => !named.Contains(name)).ToList();
        Assert.True(missing.Count == 0,
            "GetFlagAccessors() does not name these ArrangementProperties flags, so nothing round-trips them:\n  "
            + string.Join("\n  ", missing));

        var stale = named.Where(name => typeof(ArrangementProperties).GetProperty(name) is null).ToList();
        Assert.True(stale.Count == 0,
            "GetFlagAccessors() names flags that no longer exist:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    ///     The pin (<see cref="ArrangementProperties.ManualOverride" />) is the only
    ///     thing standing between a charter's hand-set technique flags and the
    ///     export's content derivation, so a one-sided writer would silently un-pin
    ///     every arrangement on the next save.
    /// </summary>
    [Fact]
    public void ArrangementProperties_ManualOverride_RoundTrips()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.Properties.ManualOverride = true;
        arrangement.Properties.Syncopation = 1;
        project.Arrangements.Add(arrangement);

        var loaded = SaveAndReload(project).Arrangements[0];

        Assert.True(loaded.Properties.ManualOverride);
        Assert.Equal(1, loaded.Properties.Syncopation);
    }

    /// <summary>Absent — and garbage — read as "derive", the same tolerance the
    /// <c>ss</c> attribute established.</summary>
    [Fact]
    public void ArrangementProperties_ManualOverride_AbsentOrGarbage_ReadsAsDerived()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.Properties.Syncopation = 1; // forces the node to exist
        project.Arrangements.Add(arrangement);

        new RscpChartSerializer().Save(ProjectPath, project);
        var doc = XDocument.Load(ProjectPath);
        var node = doc.Root!.Element("arrangements")!.Element("arrangement")!
            .Element("dcArrangementProperties")!;
        Assert.Null(node.Attribute("manual"));

        node.Add(new XAttribute("manual", "yes-please"));
        doc.Save(ProjectPath);

        Assert.False(new RscpChartSerializer().Load(ProjectPath)
            .Arrangements[0].Properties.ManualOverride);
    }

    [Fact]
    public void FretHandMuteTemplates_RoundTrip_AndStayOutOfTheRegularSet()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(new ChordTemplate { Name = "Am", Frets = { [1] = 0, [2] = 2 } });

        var mute = new ChordTemplate { Name = "mute", Frets = { [0] = 5, [1] = 5 } };
        arrangement.FretHandMuteTemplates.Add(mute);
        project.Arrangements.Add(arrangement);

        var loaded = SaveAndReload(project).Arrangements[0];

        var fhm = Assert.Single(loaded.FretHandMuteTemplates);
        Assert.Equal("mute", fhm.Name);
        Assert.Equal(5, fhm.Frets[0]);
        // The two sets stay separate — the whole reason the field exists.
        Assert.Equal("Am", Assert.Single(loaded.ChordTemplates).Name);
    }

    [Fact]
    public void ChartsWithoutTheseFields_WriteNoNewNodesOrAttributes()
    {
        // The byte-identical guarantee: a plain chart must not grow any of the
        // new markup, or every existing project file churns on the next save.
        var project = new ChartProject();
        project.Arrangements.Add(NewArrangement());

        new RscpChartSerializer().Save(ProjectPath, project);
        var arrNode = XDocument.Load(ProjectPath).Root!
            .Element("arrangements")!.Element("arrangement")!;

        Assert.Null(arrNode.Element("dcPhraseIterations"));
        Assert.Null(arrNode.Element("dcArrangementProperties"));
        Assert.Null(arrNode.Element("dcFretHandMuteTemplates"));
        foreach (var slot in new[] { "toneBase", "toneA", "toneB", "toneC", "toneD" })
            Assert.Null(arrNode.Attribute(slot));
        Assert.DoesNotContain("vibratoStrength", File.ReadAllText(ProjectPath), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownExtensionNodes_AreIgnored_NotALoadFailure()
    {
        // Forward tolerance: a file written by a NEWER DotCharter must still
        // load here, the same contract the ss attribute established.
        var project = new ChartProject();
        project.Arrangements.Add(NewArrangement());
        new RscpChartSerializer().Save(ProjectPath, project);

        var doc = XDocument.Load(ProjectPath);
        doc.Root!.Element("arrangements")!.Element("arrangement")!
            .Add(new XElement("dcSomethingFromTheFuture", new XAttribute("x", "1")));
        doc.Save(ProjectPath);

        var loaded = new RscpChartSerializer().Load(ProjectPath);
        Assert.Single(loaded.Arrangements);
    }

    private static (string Name, Func<ArrangementProperties, int> Get, Action<ArrangementProperties, int> Set)[]
        GetFlagAccessors()
    {
        // Mirrors RscpArrangementPropertyMap (internal to Services). Declared
        // here independently ON PURPOSE: if the production map ever loses an
        // entry, this test still exercises the field and fails.
        return
        [
            ("Represent", p => p.Represent, (p, v) => p.Represent = v),
            ("BonusArr", p => p.BonusArr, (p, v) => p.BonusArr = v),
            ("StandardTuning", p => p.StandardTuning, (p, v) => p.StandardTuning = v),
            ("NonStandardChords", p => p.NonStandardChords, (p, v) => p.NonStandardChords = v),
            ("BarreChords", p => p.BarreChords, (p, v) => p.BarreChords = v),
            ("PowerChords", p => p.PowerChords, (p, v) => p.PowerChords = v),
            ("DropDPower", p => p.DropDPower, (p, v) => p.DropDPower = v),
            ("OpenChords", p => p.OpenChords, (p, v) => p.OpenChords = v),
            ("FingerPicking", p => p.FingerPicking, (p, v) => p.FingerPicking = v),
            ("PickDirection", p => p.PickDirection, (p, v) => p.PickDirection = v),
            ("DoubleStops", p => p.DoubleStops, (p, v) => p.DoubleStops = v),
            ("PalmMutes", p => p.PalmMutes, (p, v) => p.PalmMutes = v),
            ("Harmonics", p => p.Harmonics, (p, v) => p.Harmonics = v),
            ("PinchHarmonics", p => p.PinchHarmonics, (p, v) => p.PinchHarmonics = v),
            ("Hopo", p => p.Hopo, (p, v) => p.Hopo = v),
            ("Tremolo", p => p.Tremolo, (p, v) => p.Tremolo = v),
            ("Slides", p => p.Slides, (p, v) => p.Slides = v),
            ("UnpitchedSlides", p => p.UnpitchedSlides, (p, v) => p.UnpitchedSlides = v),
            ("Bends", p => p.Bends, (p, v) => p.Bends = v),
            ("Tapping", p => p.Tapping, (p, v) => p.Tapping = v),
            ("Vibrato", p => p.Vibrato, (p, v) => p.Vibrato = v),
            ("FretHandMutes", p => p.FretHandMutes, (p, v) => p.FretHandMutes = v),
            ("SlapPop", p => p.SlapPop, (p, v) => p.SlapPop = v),
            ("TwoFingerPicking", p => p.TwoFingerPicking, (p, v) => p.TwoFingerPicking = v),
            ("FifthsAndOctaves", p => p.FifthsAndOctaves, (p, v) => p.FifthsAndOctaves = v),
            ("Syncopation", p => p.Syncopation, (p, v) => p.Syncopation = v),
            ("BassPick", p => p.BassPick, (p, v) => p.BassPick = v),
            ("Sustain", p => p.Sustain, (p, v) => p.Sustain = v),
            ("PathLead", p => p.PathLead, (p, v) => p.PathLead = v),
            ("PathRhythm", p => p.PathRhythm, (p, v) => p.PathRhythm = v),
            ("PathBass", p => p.PathBass, (p, v) => p.PathBass = v)
        ];
    }
}

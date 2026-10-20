using Avalonia.Input;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     Tools ▸ Hand shapes ▸ Generate arpeggio from selection (#481): the selection
///     generator with the ARPEGGIO flavour in its resolve. It replaces three steps — select,
///     Generate hand shape, tick Arpeggio in the chord-template panel — for one of the most
///     common shapes in a lead part. Also pins that a generated bar is named after its
///     template, never the literal "Chord".
/// </summary>
public sealed class HandShapeArpeggioGenerationTests
{
    private static readonly Dictionary<int, int> Shape = new() { [0] = 3, [1] = 5 };

    [Fact]
    public void TheArpeggioVariant_ResolvesAnArpeggioTemplate_AndTwoRunsWithTheSameFretsShareIt()
    {
        var h = new Harness();
        TwoRunsOfTheSameShape(h);

        h.Generator.Generate(h.Track, arpeggio: true);

        Assert.Equal(2, h.Level.HandShapes.Count);
        var template = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.True(template.IsArpeggio);
        // Minted in the canonical spelling — the flag the chord-template panel's box
        // writes and the 3D highway's arpeggio brackets read.
        Assert.True(template.Arpeggio);
        Assert.True(template.HasFrets(Shape));
        Assert.All(h.Level.HandShapes, bar => Assert.Equal(template.Id, bar.TemplateId));
        Assert.All(h.Level.HandShapes, bar => Assert.Equal(template.Name, bar.Name));

        h.History.Undo();
        Assert.Empty(h.Level.HandShapes);
        Assert.Empty(h.Arrangement.ChordTemplates);
    }

    /// <summary>An RS-XML arpeggio is spelled with <c>ForceArpeggioInRs</c> alone; a psarc
    /// one with <c>Arpeggio</c> alone. Either is the arpeggio the chart already declares,
    /// and minting a second would split one grip across two chord ids.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnArpeggioTheChartAlreadyDeclares_IsReused_InEitherSpelling(bool arpeggio, bool forceArpeggioInRs)
    {
        var h = new Harness();
        var plain = AddTemplate(h, 0, "G5", false, false);
        var declared = AddTemplate(h, 1, "G5", arpeggio, forceArpeggioInRs);
        TwoRunsOfTheSameShape(h);

        h.Generator.Generate(h.Track, arpeggio: true);

        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.All(h.Level.HandShapes, bar => Assert.Equal(declared.Id, bar.TemplateId));
        Assert.All(h.Level.HandShapes, bar => Assert.NotEqual(plain.Id, bar.TemplateId));
    }

    [Fact]
    public void ThePlainVariant_StillResolvesAPlainTemplate()
    {
        var h = new Harness();
        var declared = AddTemplate(h, 0, "G5", true, false);
        TwoRunsOfTheSameShape(h);

        h.Generator.Generate(h.Track);

        Assert.All(h.Level.HandShapes, bar => Assert.NotEqual(declared.Id, bar.TemplateId));
        var minted = Assert.Single(h.Arrangement.ChordTemplates, t => t.Id != declared.Id);
        Assert.False(minted.IsArpeggio);
    }

    [Fact]
    public void AGeneratedBar_IsNamedAfterItsTemplate_NeverChord()
    {
        var h = new Harness();
        AddTemplate(h, 0, "G5", false, false);
        TwoRunsOfTheSameShape(h);

        h.Generator.Generate(h.Track);

        Assert.All(h.Level.HandShapes, bar => Assert.Equal("G5", bar.Name));
    }

    [Fact]
    public void AGeneratedBarOnANamelessTemplate_IsLeftNameless()
    {
        var h = new Harness();
        TwoRunsOfTheSameShape(h);

        h.Generator.Generate(h.Track);

        var template = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.All(h.Level.HandShapes, bar => Assert.Equal(template.Name, bar.Name));
        Assert.All(h.Level.HandShapes, bar => Assert.NotEqual("Chord", bar.Name));
    }

    /// <summary>The Alt row of Generate hand shape (<c>Ctrl+Shift+G</c>) with the Ctrl
    /// dropped — <c>Ctrl+Shift+Alt</c> is AltGr+Shift on a German layout. Collisions,
    /// the settings row and the registration are <c>KeybindDefaultsTests</c>' job.</summary>
    [Fact]
    public void GenerateArpeggio_DefaultsToTheAltRowOfGenerateHandShape()
    {
        var config = new KeybindConfig();
        Assert.Equal(new KeyChord(Key.G, KeyModifiers.Control | KeyModifiers.Shift), config.GenerateHandShape);
        Assert.Equal(new KeyChord(Key.G, KeyModifiers.Shift | KeyModifiers.Alt), config.GenerateArpeggio);
    }

    /// <summary>Two selected runs of {3, 5} on strings 0 and 1, split by an unselected
    /// note, so the generator makes two bars.</summary>
    private static void TwoRunsOfTheSameShape(Harness h)
    {
        h.Select(h.AddNote(0, 0, 3));
        h.Select(h.AddNote(1, 1, 5));
        h.AddNote(2, 2, 7);
        h.Select(h.AddNote(4, 0, 3));
        h.Select(h.AddNote(5, 1, 5));
    }

    private static ChordTemplate AddTemplate(Harness h, int id, string name, bool arpeggio, bool forceArpeggioInRs)
    {
        var template = new ChordTemplate
        {
            Id = id, Name = name, Arpeggio = arpeggio, ForceArpeggioInRs = forceArpeggioInRs
        };
        foreach (var (stringIndex, fret) in Shape) template.Frets[stringIndex] = fret;
        h.Arrangement.ChordTemplates.Add(template);
        return template;
    }

    private sealed class Harness
    {
        private readonly ChartProject _project = new();

        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var scope = new EditingScope(State);
            var service = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            for (var i = 0; i < 32; i++)
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });

            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(Arrangement);
            State.ActiveProject = _project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            Generator = new HandShapeFromSelectionGenerator(new FingeringGenerationService(), service, Selection, logger);
            Track = new TrackEditorContext(Arrangement, Selection) { Project = _project, Level = Arrangement.Levels[0] };
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public HandShapeFromSelectionGenerator Generator { get; }
        public Arrangement Arrangement { get; }
        public TrackEditorContext Track { get; }
        public ChartLevel Level => Arrangement.Levels[0];

        public GuitarNote AddNote(int beat, int stringIndex, int fret)
        {
            var note = new GuitarNote { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret };
            Level.Notes.Add(note);
            return note;
        }

        public void Select(GuitarNote note) => Selection.SelectedNotes.Add(note);
    }
}

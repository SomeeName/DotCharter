using System.IO.Compression;
using System.Text;
using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro 7/8 keeps a bend's default middle point (offset 12, value 50) even when the destination comes
///     FIRST (offset 8, value 100), and does not draw it. Kept, the curve rose to Full at 8 % and fell back to ½ at
///     12 %; the tie then carried ½ into the held continuation, which jumped back to Full. Each surface read that
///     differently: the 2D label said "↗Full", the 3D peaks "↘1/2 ↗Full", the inspector plot +1 st. One gpif fixture
///     goes through the real importer, and the curve, the 2D labels, the 3D labels and the inspector plot must all
///     read ONE held full bend across the tie.
/// </summary>
public sealed class GpBendMiddleAfterDestinationTests
{
    private const int LeadString = 2;
    private const double OnsetMs = 1_000;
    private const double MsPerBeat = 500;

    /// <summary>
    ///     <paramref name="vibratoOnTheTie" />: the tied note carries vibrato, so the tie stays two linked notes (as in
    ///     the reported chart) and the tie's carry is exercised; without it the import merges the tie into one note.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMiddlePointAfterTheDestination_ImportsAsOneHeldFullBend_OnEverySurface(bool vibratoOnTheTie)
    {
        var notes = ImportedLeadNotes(vibratoOnTheTie);
        Assert.Equal(vibratoOnTheTie ? 2 : 1, notes.Count);
        Assert.All(notes.SkipLast(1), n => Assert.True(n.LinkNext));

        // The curve, in beats from the onset: 0, Full at 8 % of the bent quarter, then Full to the end.
        var curve = notes.SelectMany(n => n.BendValues)
            .Select(b => (Beat: Math.Round(b.Position.DoubleValue - notes[0].Position.DoubleValue, 3), b.Step)).ToList();
        Assert.Equal([(0.0, 0.0), (0.08, 2.0)], curve.Take(2));
        Assert.All(curve.Skip(2), p => Assert.Equal(2.0, p.Step));

        // The 2D timeline: the shared rule over the inputs the highway note-head renderer passes (a linked note
        // starts at the pitch the note before it ended on).
        var labels2D = new List<string>();
        for (var i = 0; i < notes.Count; i++)
        {
            float? carriedIn = i > 0 && notes[i - 1].LinkNext ? (float)notes[i - 1].BendValues[^1].Step : null;
            labels2D.AddRange(Labels2D(notes[i], carriedIn));
        }

        Assert.Equal(["↗Full"], labels2D);

        // The 3D highway: the real link pass carries the first tail's end into the tie, then the peak labels.
        Assert.Equal(["↗Full"], Labels3D(notes));

        // The inspector plot: one rise to +2 st, held.
        var plot = Plot(notes[0]);
        Assert.Equal([(0, 0.0), (1, 2.0)], plot.Take(2));
        Assert.All(plot.Skip(2), p => Assert.Equal(2.0, p.Step));
    }

    private static List<GuitarNote> ImportedLeadNotes(bool vibratoOnTheTie)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpbend833-{Guid.NewGuid():N}.gp");
        try
        {
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
            {
                writer.Write(Score(vibratoOnTheTie));
            }

            var level = Assert.Single(new GpImporter().ImportTracksWithMetadata(path, [0]).Arrangements).Levels[^1];
            return [.. level.Notes.Where(n => n.StringIndex == LeadString).OrderBy(n => n.Position)];
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    private static string[] Labels2D(GuitarNote note, float? carriedIn)
    {
        Span<float> steps = [.. note.BendValues.Select(b => (float)b.Step)];
        Span<BendGesture> gestures = new BendGesture[steps.Length];
        BendGestures.Classify(steps, note.BendValues[0].Position.CompareTo(note.Position) == 0, carriedIn, gestures);
        var labels = new List<string>();
        foreach (var g in gestures)
        {
            if (g.Kind != BendGestureKind.None) labels.Add(BendGestures.Text(g));
        }

        return [.. labels];
    }

    private static string[] Labels3D(List<GuitarNote> notes)
    {
        static double Ms(FractionalPosition p) => OnsetMs + p.DoubleValue * MsPerBeat;

        var bends = new List<RenderBendValue>();
        var render = new RenderNote[notes.Count];
        for (var i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            var flags = NoteFlags.None;
            if (n.BendValues.Count > 0) flags |= NoteFlags.HasBends;
            if (n.LinkNext) flags |= NoteFlags.LinkNext;
            if (i > 0 && notes[i - 1].LinkNext) flags |= NoteFlags.LinkPrevious;
            render[i] = new RenderNote
            {
                Time = Ms(n.Position), Sustain = n.Sustain.DoubleValue * MsPerBeat, StringIndex = (byte)n.StringIndex,
                Fret = (byte)n.Fret, Flags = flags, BendOffset = bends.Count, BendCount = n.BendValues.Count
            };
            bends.AddRange(n.BendValues.Select(b => new RenderBendValue { Time = Ms(b.Position), Step = (float)b.Step }));
        }

        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 13, Width = 4 }],
            AnchorCount = 1,
            BendValues = [.. bends],
            BendValueCount = bends.Count,
            Notes = render,
            NoteCount = render.Length
        };
        snapshot.JoinLinkedTails();
        var context = new RenderContext
        {
            Snapshot = snapshot,
            TimeMs = 0,
            BoardWidth = 10f,
            QuadBatcher = new QuadBatcher(null!), // never flushed; nothing reaches a device
            TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.MaxScrollSpeed),
            FadeEndZ = HighwaySpeedPolicy.MaxDrawDistanceZ,
            FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.MaxDrawDistanceZ),
            HorizonMs = HighwaySpeedPolicy.HorizonMs(HighwaySpeedPolicy.MaxScrollSpeed, HighwaySpeedPolicy.MaxDrawDistanceZ)
        };
        SustainRenderer.Render(context);
        return [.. Enumerable.Range(0, context.Deferred3DLabelCount).Select(i => context.Deferred3DLabels[i].Text)];
    }

    private static (int Slot, double Step)[] Plot(GuitarNote note)
    {
        var editor = new BendEditorViewModel(() => note.BendValues, _ => { }, () => note.Position,
            () => note.Position + note.Sustain, () => { }, FakeLocalization.English());
        return [.. editor.Points.Select(p => (p.TimeSlot, p.Step))];
    }

    private static string Note(int id, string tie, string extra, string bend) => $"""
        <Note id="{id}">{tie}{extra}<Properties>
          <Property name="String"><String>{LeadString}</String></Property>
          <Property name="Fret"><Fret>15</Fret></Property>
          <Property name="Bended"><Enable /></Property>
          {bend}
        </Properties></Note>
        """;

    private static string Bend(int originValue, int middleOffset, int middleValue, int destinationOffset,
        int destinationValue) => $"""
        <Property name="BendOriginOffset"><Float>0</Float></Property>
        <Property name="BendOriginValue"><Float>{originValue}</Float></Property>
        <Property name="BendMiddleOffset1"><Float>{middleOffset}</Float></Property>
        <Property name="BendMiddleOffset2"><Float>{middleOffset}</Float></Property>
        <Property name="BendMiddleValue"><Float>{middleValue}</Float></Property>
        <Property name="BendDestinationOffset"><Float>{destinationOffset}</Float></Property>
        <Property name="BendDestinationValue"><Float>{destinationValue}</Float></Property>
        """;

    /// <summary>A quarter bent with the reported property set, tied to a quarter held at Full, then a rest.</summary>
    private static string Score(bool vibratoOnTheTie) => $"""
        <GPIF>
          <Score><Title>Middle after destination</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks><Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
            <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
          </Properties></Staff></Staves></Track></Tracks>
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>0 1 2</Beats></Voice></Voices>
          <Beats>
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            <Beat id="2"><Rhythm ref="1" /></Beat>
          </Beats>
          <Rhythms>
            <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
            <Rhythm id="1"><NoteValue>Half</NoteValue></Rhythm>
          </Rhythms>
          <Notes>
          {Note(0, """<Tie origin="true" destination="false" />""", "", Bend(0, 12, 50, 8, 100))}
          {Note(1, """<Tie origin="false" destination="true" />""", vibratoOnTheTie ? "<Vibrato>Slight</Vibrato>" : "",
              Bend(100, 50, 100, 100, 100))}
          </Notes>
        </GPIF>
        """;
}

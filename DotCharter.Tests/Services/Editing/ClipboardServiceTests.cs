namespace DotCharter.Tests.Services.Editing;

public class ClipboardServiceTests
{
    private static GuitarNote Note(int beatId, int stringIdx = 0, int fret = 5) => new() { Position = new FractionalPosition(beatId), StringIndex = stringIdx, Fret = fret };

    [Fact]
    public void HasData_IsFalseInitially()
    {
        var svc = new ClipboardService(new FakeAppLogger());
        Assert.False(svc.HasData);
    }

    [Fact]
    public void Copy_WithEmptyInput_ClearsClipboardAndLogsTrace()
    {
        var logger = new FakeAppLogger();
        var svc = new ClipboardService(logger);

        svc.Copy(Array.Empty<GuitarNote>(), Array.Empty<Chord>(), Array.Empty<HandShape>(),
            Array.Empty<EventPoint>(), Array.Empty<ToneChange>(), Array.Empty<FretAnchor>());

        Assert.False(svc.HasData);
        Assert.True(logger.HasEntryContaining("TRACE", "clipboard cleared"));
    }

    [Fact]
    public void Copy_ThenPaste_ShiftsNotesByOffset()
    {
        var logger = new FakeAppLogger();
        var svc = new ClipboardService(logger);
        var notes = new[] { Note(5), Note(8) };

        svc.Copy(notes, Array.Empty<Chord>(), Array.Empty<HandShape>(),
            Array.Empty<EventPoint>(), Array.Empty<ToneChange>(), Array.Empty<FretAnchor>());

        Assert.True(svc.HasData);

        var payload = svc.GetPastedData(new FractionalPosition(10));
        Assert.NotNull(payload);
        Assert.Equal(2, payload.Notes.Count);

        // Base position was beat 5; target beat 10 → offset = +5.
        Assert.Equal(10, payload.Notes[0].Position.BeatId);
        Assert.Equal(13, payload.Notes[1].Position.BeatId);
    }

    [Fact]
    public void Copy_LogsCountsForEachKind()
    {
        var logger = new FakeAppLogger();
        var svc = new ClipboardService(logger);

        svc.Copy(new[] { Note(0), Note(1) },
            Array.Empty<Chord>(), Array.Empty<HandShape>(),
            new[] { new EventPoint { Position = new FractionalPosition(0), Phrase = "p" } },
            Array.Empty<ToneChange>(),
            Array.Empty<FretAnchor>());

        Assert.Contains(logger.Entries,
            e => e.Level == "INFO" && e.Message.Contains("notes=2") && e.Message.Contains("events=1"));
    }

    [Fact]
    public void GetPastedData_WithEmptyClipboard_ReturnsNull()
    {
        var svc = new ClipboardService(new FakeAppLogger());
        Assert.Null(svc.GetPastedData(new FractionalPosition(4)));
    }

    [Fact]
    public void Copy_ClonesNotes_SoSubsequentMutationsDoNotAffectPayload()
    {
        var svc = new ClipboardService(new FakeAppLogger());
        var source = Note(3, fret: 5);

        svc.Copy(new[] { source }, Array.Empty<Chord>(), Array.Empty<HandShape>(),
            Array.Empty<EventPoint>(), Array.Empty<ToneChange>(), Array.Empty<FretAnchor>());

        source.Fret = 99;

        var payload = svc.GetPastedData(new FractionalPosition(3));
        Assert.NotNull(payload);
        Assert.Equal(5, payload.Notes[0].Fret);
    }

    [Fact]
    public void Copy_ThenPaste_ShiftsToneChangesByOffset()
    {
        var svc = new ClipboardService(new FakeAppLogger());
        var tone = new ToneChange { Position = new FractionalPosition(4), ToneName = "lead" };

        svc.Copy(Array.Empty<GuitarNote>(), Array.Empty<Chord>(), Array.Empty<HandShape>(),
            Array.Empty<EventPoint>(), new[] { tone }, Array.Empty<FretAnchor>());

        var payload = svc.GetPastedData(new FractionalPosition(9));
        Assert.NotNull(payload);
        Assert.Single(payload.ToneChanges);
        Assert.Equal(9, payload.ToneChanges[0].Position.BeatId);
        Assert.Equal("lead", payload.ToneChanges[0].ToneName);
    }
}

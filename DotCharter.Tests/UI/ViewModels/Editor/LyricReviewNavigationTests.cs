using System.Windows.Input;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Editing.Lyrics;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Import.Lyrics;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class LyricReviewNavigationTests
{
    [Fact]
    public void ReviewNavigationSelectsFlaggedSyllablesAndWrapsWithoutClearingThem()
    {
        var (inspector, editor, selection) = Build();
        var lyrics = editor.ActiveVocalTrack!.Phrases.Single().Lyrics;
        var reasons = typeof(LyricEvent).GetProperty("ReviewReasons");
        Assert.NotNull(reasons);
        reasons.SetValue(lyrics[0], Enum.ToObject(reasons.PropertyType, 1));
        reasons.SetValue(lyrics[2], Enum.ToObject(reasons.PropertyType, 1));
        var next = Command(inspector, "NextReviewCommand");
        var previous = Command(inspector, "PreviousReviewCommand");

        next.Execute(null);
        Assert.Same(lyrics[0], selection.SelectedLyric);
        next.Execute(null);
        Assert.Same(lyrics[2], selection.SelectedLyric);
        next.Execute(null);
        Assert.Same(lyrics[0], selection.SelectedLyric);
        previous.Execute(null);
        Assert.Same(lyrics[2], selection.SelectedLyric);
        Assert.Equal("LowScore", reasons.GetValue(lyrics[0])!.ToString());
    }

    [Fact]
    public void ReviewCommandsAreOffWithNothingToReviewAndTheCountSaysWhy()
    {
        var (inspector, editor, selection) = Build(out var history);
        var lyrics = editor.ActiveVocalTrack!.Phrases.Single().Lyrics;
        var text = FakeLocalization.English();

        Assert.False(inspector.NextReviewCommand.CanExecute(null));
        Assert.False(inspector.PreviousReviewCommand.CanExecute(null));
        Assert.False(inspector.MarkReviewedCommand.CanExecute(null));
        Assert.Equal(text["Inspector_Lyrics_ReviewNone"], inspector.ReviewCountText);

        lyrics[0].ReviewReasons = LyricReviewReason.LowScore;
        lyrics[2].ReviewReasons = LyricReviewReason.Gap;
        selection.SelectedLyric = lyrics[0];

        Assert.True(inspector.NextReviewCommand.CanExecute(null));
        Assert.True(inspector.PreviousReviewCommand.CanExecute(null));
        Assert.True(inspector.MarkReviewedCommand.CanExecute(null));
        Assert.Equal(text.Format("Inspector_Lyrics_ReviewCount", 2), inspector.ReviewCountText);

        inspector.MarkReviewedCommand.Execute(null);

        Assert.Equal(LyricReviewReason.None, lyrics[0].ReviewReasons);
        Assert.False(inspector.MarkReviewedCommand.CanExecute(null));
        Assert.Equal(text.Format("Inspector_Lyrics_ReviewCount", 1), inspector.ReviewCountText);

        history.Undo();

        Assert.Equal(LyricReviewReason.LowScore, lyrics[0].ReviewReasons);
        Assert.Equal(text.Format("Inspector_Lyrics_ReviewCount", 2), inspector.ReviewCountText);
        Assert.True(inspector.MarkReviewedCommand.CanExecute(null));
    }

    private static ICommand Command(LyricsInspectorViewModel inspector, string name)
    {
        var property = inspector.GetType().GetProperty(name);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<ICommand>(property.GetValue(inspector));
    }

    internal static (LyricsInspectorViewModel Inspector, ChartEditorViewModel Editor, SelectionService Selection) Build() =>
        Build(out _);

    internal static (LyricsInspectorViewModel Inspector, ChartEditorViewModel Editor, SelectionService Selection) Build(
        out HistoryService history, FakeLocalization? localization = null)
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        history = new HistoryService(logger);
        var selection = new SelectionService();
        var project = new ChartProject();
        for (var i = 0; i < 24; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        var track = new VocalTrack();
        var phrase = new LyricPhrase();
        for (var i = 0; i < 3; i++)
            phrase.Lyrics.Add(new LyricEvent { Text = "la", Position = new FractionalPosition(i + 2), EndPosition = new FractionalPosition(i + 3) });
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);
        state.ActiveProject = project;
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state,
            new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger),
            new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = project.Chart,
            ActiveVocalTrack = track
        };
        var inspector = new LyricsInspectorViewModel(editor, history, state, logger, new FakeFileStorageService(),
            new LyricsFileService(new ExportTargetWriter(), new LrcLyricsParser()),
            new LyricAutoSyncRunner(new NoopSync(), null, null, state), localization ?? FakeLocalization.English());
        return (inspector, editor, selection);
    }

    private sealed class NoopSync : ILyricAutoSyncService
    {
        public string ModelDirectory => "";
        public bool IsModelInstalled => false;
        public Task<LyricAutoSyncOutcome> SyncAsync(string lyricsText, string audioPath, IList<BeatMarker> beats,
            LyricAutoSyncOptions? options = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

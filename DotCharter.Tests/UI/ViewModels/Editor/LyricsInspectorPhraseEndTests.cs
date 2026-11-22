using DotCharter.Core.Domain.Vocals;
using DotCharter.Rendering.Data;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Lyrics;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Import.Lyrics;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Vocals;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class LyricsInspectorPhraseEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotcharter-phrase-end-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportAfterBoundaryEdit_UsesTheEditedLineBreaks(bool split)
    {
        var rig = Build();
        rig.Selection.SelectedLyric = rig.First.Lyrics[split ? 0 : 1];

        Edit(rig, split, false);

        Assert.Equal(split ? ["hel+", "e+", "Sa", "yo+"] : ["hel", "e", "Sa", "yo+"], Export(rig.Project));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearingTheEnd_JoinsTheNextLineAndRefreshesTheTimeline(bool keybind)
    {
        var rig = Build();
        var lyric = rig.First.Lyrics[^1];
        rig.Selection.SelectedLyric = lyric;
        var redraws = 0;
        rig.Editor.ChartDataChanged += () => redraws++;

        Edit(rig, false, keybind);

        Assert.Same(rig.First, Assert.Single(rig.Track.Phrases));
        Assert.Equal(["hel", "e", "Sa", "yo"], rig.First.Lyrics.Select(l => l.Text));
        Assert.Equal(new FractionalPosition(1), rig.First.Position);
        Assert.Equal(new FractionalPosition(10), rig.First.EndPosition);
        Assert.Equal([false, false, false, true], rig.First.Lyrics.Select(l => l.IsPhraseEnd));
        Assert.False(rig.Inspector.SelectedLyricIsPhraseEnd);
        Assert.Same(lyric, rig.Selection.SelectedLyric);
        Assert.True(redraws > 0);
        Assert.Equal(["hel", "e", "Sa", "yo+"], Export(rig.Project));
        AssertTimeline(rig, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingTheEnd_SplitsAfterTheSelectedSyllable(bool keybind)
    {
        var rig = Build();
        var lyric = rig.First.Lyrics[0];
        var following = rig.First.Lyrics[1];
        rig.Selection.SelectedLyric = lyric;

        Edit(rig, true, keybind);

        Assert.Equal(3, rig.Track.Phrases.Count);
        Assert.Same(lyric, Assert.Single(rig.First.Lyrics));
        Assert.Equal(new FractionalPosition(1), rig.First.Position);
        Assert.Equal(lyric.EndPosition, rig.First.EndPosition);
        var split = rig.Track.Phrases[1];
        Assert.Same(following, Assert.Single(split.Lyrics));
        Assert.Equal(following.Position, split.Position);
        Assert.Equal(new FractionalPosition(5), split.EndPosition);
        Assert.Same(rig.Second, rig.Track.Phrases[2]);
        Assert.True(lyric.IsPhraseEnd);
        Assert.True(following.IsPhraseEnd);
        Assert.True(rig.Inspector.SelectedLyricIsPhraseEnd);
        Assert.Equal(["hel+", "e+", "Sa", "yo+"], Export(rig.Project));
        AssertTimeline(rig, 1, 1, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearingTheFinalEnd_KeepsTheBoundaryAndExplainsWhy(bool keybind)
    {
        var rig = Build();
        var lyric = rig.Second.Lyrics[^1];
        rig.Selection.SelectedLyric = lyric;
        var node = rig.History.CurrentNodeId;

        Edit(rig, false, keybind);

        Assert.True(lyric.IsPhraseEnd);
        Assert.True(rig.Inspector.SelectedLyricIsPhraseEnd);
        Assert.Equal(2, rig.Track.Phrases.Count);
        Assert.Equal(node, rig.History.CurrentNodeId);
        Assert.Equal(FakeLocalization.English()["Status_LyricPhraseEnd_NoNextLine"], rig.State.MainDisplayMessage);
        Assert.Equal(["hel", "e+", "Sa", "yo+"], Export(rig.Project));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndoRedo_RestoresTheSameContainersCollectionsAndSyllablesInOneStep(bool split)
    {
        var rig = Build();
        var phrases = rig.Track.Phrases;
        var firstLyrics = rig.First.Lyrics;
        var secondLyrics = rig.Second.Lyrics;
        var first = firstLyrics.ToArray();
        var second = secondLyrics.ToArray();
        var selected = first[split ? 0 : 1];
        rig.Selection.SelectedLyric = selected;
        var node = rig.History.CurrentNodeId;
        Edit(rig, split, true);
        var edited = rig.Track.Phrases.ToArray();

        for (var cycle = 0; cycle < 2; cycle++)
        {
            rig.History.Undo();
            Assert.Equal(node, rig.History.CurrentNodeId);
            Assert.Same(phrases, rig.Track.Phrases);
            Assert.Same(phrases, rig.Editor.LyricPhrases);
            Assert.Same(rig.First, phrases[0]);
            Assert.Same(rig.Second, phrases[1]);
            Assert.Equal(2, phrases.Count);
            Assert.Same(firstLyrics, rig.First.Lyrics);
            Assert.Same(secondLyrics, rig.Second.Lyrics);
            Assert.Equal(first, rig.First.Lyrics);
            Assert.Equal(second, rig.Second.Lyrics);
            Assert.Equal(new FractionalPosition(5), rig.First.EndPosition);
            Assert.Same(selected, rig.Selection.SelectedLyric);
            Assert.Equal(!split, rig.Inspector.SelectedLyricIsPhraseEnd);
            Assert.Equal(["hel", "e+", "Sa", "yo+"], Export(rig.Project));
            AssertTimeline(rig, 2, 2);

            rig.History.Redo();
            Assert.Equal(edited, rig.Track.Phrases);
            Assert.Same(selected, rig.Selection.SelectedLyric);
            Assert.Equal(split, rig.Inspector.SelectedLyricIsPhraseEnd);
            Assert.Single(rig.History.GetUndoSteps());
            Assert.Equal(split ? ["hel+", "e+", "Sa", "yo+"] : ["hel", "e", "Sa", "yo+"], Export(rig.Project));
            AssertTimeline(rig, split ? [1, 1, 2] : [4]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditedBoundariesAndSpans_SurviveAProjectRoundTrip(bool split)
    {
        var rig = Build();
        rig.Selection.SelectedLyric = rig.First.Lyrics[split ? 0 : 1];
        Edit(rig, split, false);
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "project.dcproj");
        var serializer = new RscpChartSerializer();

        serializer.Save(path, rig.Project);
        var loaded = serializer.Load(path);

        var phrases = loaded.VocalTracks.Single().Phrases;
        Assert.Equal(split ? 3 : 1, phrases.Count);
        Assert.Equal(split ? [1, 1, 2] : [4], phrases.Select(p => p.Lyrics.Count));
        Assert.Equal(new FractionalPosition(1), phrases[0].Position);
        Assert.Equal(new FractionalPosition(split ? 3 : 10), phrases[0].EndPosition);
        if (split)
        {
            Assert.Equal(new FractionalPosition(3), phrases[1].Position);
            Assert.Equal(new FractionalPosition(5), phrases[1].EndPosition);
        }
        foreach (var phrase in phrases)
        {
            Assert.Equal(Enumerable.Range(0, phrase.Lyrics.Count).Select(i => i == phrase.Lyrics.Count - 1),
                phrase.Lyrics.Select(l => l.IsPhraseEnd));
        }
        Assert.Equal(split ? ["hel+", "e+", "Sa", "yo+"] : ["hel", "e", "Sa", "yo+"], Export(loaded));
    }

    [Fact]
    public void SelectionInAnotherTrack_EditsItsOwningContainers()
    {
        var rig = Build();
        var other = new VocalTrack { Phrases = [rig.First, rig.Second] };
        rig.Track.Phrases.Clear();
        rig.Project.VocalTracks.Add(other);
        rig.Selection.SelectedLyric = rig.First.Lyrics[^1];

        rig.Inspector.ToggleSelectedPhraseEnd();

        Assert.Single(other.Phrases);
        Assert.Empty(rig.Track.Phrases);
        Assert.Equal(4, rig.First.Lyrics.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingTheEndOnAWordPart_ClearsTheJoinInTheSameUndoStep(bool keybind)
    {
        var rig = Build();
        var hel = rig.First.Lyrics[0];
        hel.IsWordPart = true;
        rig.Selection.SelectedLyric = hel;

        Edit(rig, true, keybind);

        Assert.False(hel.IsWordPart);
        Assert.False(rig.Inspector.SelectedLyricIsWordPart);
        Assert.Equal(["hel+", "e+", "Sa", "yo+"], Export(rig.Project));
        rig.History.Undo();
        Assert.True(hel.IsWordPart);
        Assert.Equal(2, rig.Track.Phrases.Count);
        Assert.Equal(["hel-", "e+", "Sa", "yo+"], Export(rig.Project));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JoiningAWordPartThatEndsItsLine_JoinsTheNextLineInTheSameUndoStep(bool keybind)
    {
        var rig = Build();
        var e = rig.First.Lyrics[^1];
        rig.Selection.SelectedLyric = e;

        if (keybind) rig.Inspector.ToggleSelectedWordPart();
        else rig.Inspector.SelectedLyricIsWordPart = true;

        Assert.Same(rig.First, Assert.Single(rig.Track.Phrases));
        Assert.True(e.IsWordPart);
        Assert.False(e.IsPhraseEnd);
        Assert.False(rig.Inspector.SelectedLyricIsPhraseEnd);
        Assert.Equal(["hel", "e-", "Sa", "yo+"], Export(rig.Project));
        rig.History.Undo();
        Assert.Equal(2, rig.Track.Phrases.Count);
        Assert.False(e.IsWordPart);
        Assert.True(e.IsPhraseEnd);
        Assert.Equal(["hel", "e+", "Sa", "yo+"], Export(rig.Project));
    }

    [Fact]
    public void TypingATrailingHyphenOnALineEnd_JoinsTheNextLineAndStoresNoDash()
    {
        var rig = Build();
        var e = rig.First.Lyrics[^1];
        rig.Selection.SelectedLyric = e;

        rig.Inspector.SelectedLyricText = "eh-";
        rig.Inspector.CommitSelectedLyricText();

        Assert.Equal("eh", e.Text);
        Assert.True(e.IsWordPart);
        Assert.False(e.IsPhraseEnd);
        Assert.True(rig.Inspector.SelectedLyricIsWordPart);
        Assert.Equal("eh", rig.Inspector.SelectedLyricText);
        Assert.Same(rig.First, Assert.Single(rig.Track.Phrases));
        Assert.Equal(["hel", "eh-", "Sa", "yo+"], Export(rig.Project));
        rig.History.Undo();
        Assert.Equal("e", e.Text);
        Assert.False(e.IsWordPart);
        Assert.Equal(2, rig.Track.Phrases.Count);
        Assert.Equal(["hel", "e+", "Sa", "yo+"], Export(rig.Project));
        rig.History.Redo();
        Assert.Equal(["hel", "eh-", "Sa", "yo+"], Export(rig.Project));
    }

    [Fact]
    public void TypingATrailingHyphenOnTheLastLineEnd_KeepsTheLineEndAndBothLinesRoundTrip()
    {
        var rig = Build();
        var yo = rig.Second.Lyrics[^1];
        rig.Selection.SelectedLyric = yo;

        rig.Inspector.SelectedLyricText = "yo-";
        rig.Inspector.CommitSelectedLyricText();

        Assert.Equal("yo", yo.Text);
        Assert.False(yo.IsWordPart);
        Assert.True(yo.IsPhraseEnd);
        Assert.Equal(["hel", "e+", "Sa", "yo+"], Export(rig.Project));
        var read = new RsXmlVocalsReader().ReadDocument(new RsXmlVocalsWriter().Write(rig.Project), "vocals.xml",
            rig.Project.Beats).VocalTracks.Single();
        Assert.Equal(2, read.Phrases.Count);
    }

    [Fact]
    public void TypingAHyphenInsideAWord_StaysTextAndChangesNoLineUntilCommitted()
    {
        var rig = Build();
        var e = rig.First.Lyrics[^1];
        rig.Selection.SelectedLyric = e;
        var node = rig.History.CurrentNodeId;

        foreach (var draft in new[] { "x", "x-", "x-r", "x-ra", "x-ray" })
        {
            rig.Inspector.SelectedLyricText = draft;
            Assert.Equal("e", e.Text);
            Assert.Equal(2, rig.Track.Phrases.Count);
            Assert.Equal(node, rig.History.CurrentNodeId);
        }
        rig.Inspector.CommitSelectedLyricText();

        Assert.Equal("x-ray", e.Text);
        Assert.False(e.IsWordPart);
        Assert.True(e.IsPhraseEnd);
        Assert.Equal(2, rig.Track.Phrases.Count);
        Assert.Equal("x-ray", rig.Inspector.SelectedLyricText);
        rig.History.Undo();
        Assert.Equal(node, rig.History.CurrentNodeId);
        Assert.Equal("e", e.Text);
    }

    [Fact]
    public void CommittingATypedTrailingHyphen_JoinsTheNextLineInOneEntry()
    {
        var rig = Build();
        var e = rig.First.Lyrics[^1];
        rig.Selection.SelectedLyric = e;
        var node = rig.History.CurrentNodeId;

        foreach (var draft in new[] { "h", "he", "hel", "hel-" })
            rig.Inspector.SelectedLyricText = draft;
        Assert.Equal(2, rig.Track.Phrases.Count);
        rig.Inspector.CommitSelectedLyricText();

        Assert.Equal("hel", e.Text);
        Assert.True(e.IsWordPart);
        Assert.Same(rig.First, Assert.Single(rig.Track.Phrases));
        Assert.Equal("hel", rig.Inspector.SelectedLyricText);
        rig.History.Undo();
        Assert.Equal(node, rig.History.CurrentNodeId);
        Assert.Equal(2, rig.Track.Phrases.Count);
        Assert.Equal("e", e.Text);
    }

    [Fact]
    public void ADraftPendingWhenTheSelectionMoves_IsCommittedToItsOwnSyllable()
    {
        var rig = Build();
        var e = rig.First.Lyrics[^1];
        rig.Selection.SelectedLyric = e;

        rig.Inspector.SelectedLyricText = "eh";
        rig.Selection.SelectedLyric = rig.Second.Lyrics[0];

        Assert.Equal("eh", e.Text);
        Assert.Equal("Sa", rig.Inspector.SelectedLyricText);
        Assert.Equal("Sa", rig.Second.Lyrics[0].Text);
    }

    private static void Edit(Rig rig, bool value, bool keybind)
    {
        if (keybind) rig.Inspector.ToggleSelectedPhraseEnd();
        else rig.Inspector.SelectedLyricIsPhraseEnd = value;
    }

    private static string[] Export(ChartProject project) => new RsXmlVocalsWriter().Write(project).Root!
        .Elements("vocal").Select(v => v.Attribute("lyric")!.Value).ToArray();

    private static void AssertTimeline(Rig rig, params int[] counts)
    {
        var snapshot = new FrameSnapshot();
        var context = new SnapshotUpdateContext
        {
            ActiveBeats = rig.Project.Beats,
            LyricPhrases = rig.Editor.LyricPhrases,
            SelectedLyric = rig.Selection.SelectedLyric
        };
        FrameSnapshot.BuildSnapshot(0, 64000, 0, snapshot, context, []);
        Assert.Equal(counts.Length, snapshot.LyricPhraseCount);
        Assert.Equal(counts, snapshot.LyricPhrases.Take(snapshot.LyricPhraseCount).Select(p => p.WordCount));
        Assert.Single(snapshot.LyricWords.Take(snapshot.LyricWordCount), w => w.IsSelected);
    }

    private static Rig Build()
    {
        var logger = new FakeAppLogger();
        var project = new ChartProject();
        for (var i = 0; i < 128; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var first = new LyricPhrase
        {
            Position = new FractionalPosition(1), EndPosition = new FractionalPosition(5),
            Lyrics = [Syllable(2, "hel", false), Syllable(3, "e", true)]
        };
        var second = new LyricPhrase
        {
            Position = new FractionalPosition(6), EndPosition = new FractionalPosition(10),
            Lyrics = [Syllable(6, "Sa", false), Syllable(7, "yo", true)]
        };
        var track = new VocalTrack { Phrases = [first, second] };
        project.VocalTracks.Add(track);
        var state = new ProjectState { ActiveProject = project };
        var history = new HistoryService(logger, new EditingScope(state));
        var selection = new SelectionService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state,
            new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger),
            new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = project.Chart, ActiveVocalTrack = track
        };
        var inspector = new LyricsInspectorViewModel(editor, history, state, logger, new FakeFileStorageService(),
            new LyricsFileService(new ExportTargetWriter(), new LrcLyricsParser()), new UnusedAutoSync(), FakeLocalization.English());
        return new Rig(inspector, editor, selection, history, state, project, track, first, second);
    }

    private static LyricEvent Syllable(int beat, string text, bool end) => new()
    {
        Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 1),
        Text = text, IsPhraseEnd = end, Note = 60
    };

    private sealed record Rig(LyricsInspectorViewModel Inspector, ChartEditorViewModel Editor,
        SelectionService Selection, HistoryService History, ProjectState State, ChartProject Project,
        VocalTrack Track, LyricPhrase First, LyricPhrase Second);

    private sealed class UnusedAutoSync : ILyricAutoSyncRunner
    {
        public Task<LyricAutoSyncRunResult> RunAsync(string rawLyrics, IList<BeatMarker> liveBeats,
            Action<int> onModelDownloadStarting, IProgress<string> progress) => throw new NotSupportedException();
    }
}

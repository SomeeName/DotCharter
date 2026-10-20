using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     A bulk lyric replacement (LRC/UltraStar import, auto-sync) must not leave the
///     lyric SELECTION pointing into the phrases it just threw away.
///     <para>
///         <c>SetVocalPhrasesAction</c> clears the track's phrase collection and refills
///         it with new objects, so a <c>SelectedLyric</c> from before the import is
///         orphaned — and it is not inert. The inspector keeps showing the orphan's
///         text/word-part/phrase-end/pitch, and each of those editors then records a
///         real undo node against a detached object: the edit reports success, burns an
///         undo slot, and changes nothing on screen or on disk.
///         <c>SelectionService.ClearSelections</c> already covers the project-swap and
///         snapshot-restore paths for this reason; a phrase replacement goes through
///         neither.
///     </para>
/// </summary>
public sealed class LyricsInspectorStaleSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-lyricsel-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task An_lrc_import_drops_a_selection_it_orphaned()
    {
        var rig = Build();

        // The user had a syllable selected from the track they are about to replace.
        var stale = rig.Track.Phrases[0].Lyrics[0];
        rig.Selection.SelectedLyric = stale;
        Assert.True(rig.Inspector.HasSelectedLyric);

        await rig.Inspector.ImportLrcCommand.ExecuteAsync(null);

        // The import landed…
        Assert.DoesNotContain(rig.Track.Phrases.SelectMany(p => p.Lyrics), l => ReferenceEquals(l, stale));
        // …and took the selection with it.
        Assert.Null(rig.Selection.SelectedLyric);
        Assert.False(rig.Inspector.HasSelectedLyric);
    }

    /// <summary>
    ///     The point of clearing it: an edit against the orphan used to be accepted and
    ///     recorded. With the selection dropped, the inspector's editors have nothing to
    ///     write to and no history node is produced.
    /// </summary>
    [Fact]
    public async Task Editing_after_an_import_cannot_record_a_node_against_an_orphan()
    {
        var rig = Build();
        var stale = rig.Track.Phrases[0].Lyrics[0];
        rig.Selection.SelectedLyric = stale;

        await rig.Inspector.ImportLrcCommand.ExecuteAsync(null);

        rig.Inspector.SelectedLyricText = "typo-fixed";
        rig.Inspector.ToggleSelectedWordPart();
        rig.Inspector.ToggleSelectedPhraseEnd();

        Assert.Equal("hold", stale.Text); // the detached object is untouched
        Assert.False(stale.IsWordPart);
        Assert.False(stale.IsPhraseEnd);

        // …and none of those three gestures pushed a node, so ONE undo is still the
        // import. Before the fix each of them recorded a rename/flag node against the
        // orphan, so the user had to press Ctrl+Z four times to get their lyrics back.
        rig.History.Undo();
        Assert.Same(stale, rig.Track.Phrases.Single().Lyrics.Single());
    }

    /// <summary>
    ///     A selection the replacement did NOT orphan is left alone — clearing
    ///     unconditionally would throw away a still-valid pick.
    /// </summary>
    [Fact]
    public async Task A_selection_the_import_kept_is_not_cleared()
    {
        var rig = Build();

        // Select a lyric in a DIFFERENT vocal track: the import only replaces the
        // active one's phrases, so this pick stays valid.
        var other = new LyricEvent { Text = "elsewhere" };
        var otherPhrase = new LyricPhrase();
        otherPhrase.Lyrics.Add(other);
        var otherTrack = new VocalTrack { Name = "Backing" };
        otherTrack.Phrases.Add(otherPhrase);
        rig.Editor.VocalTracks.Add(otherTrack);
        rig.Selection.SelectedLyric = other;

        await rig.Inspector.ImportLrcCommand.ExecuteAsync(null);

        Assert.Same(other, rig.Selection.SelectedLyric);
    }

    // ---- rig ---------------------------------------------------------------

    private sealed record Rig(
        LyricsInspectorViewModel Inspector,
        ChartEditorViewModel Editor,
        SelectionService Selection,
        HistoryService History,
        VocalTrack Track);

    private Rig Build()
    {
        Directory.CreateDirectory(_root);
        var lrc = Path.Combine(_root, "song.lrc");
        File.WriteAllText(lrc, "[00:01.00]hello world\n[00:03.00]second line\n");

        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var history = new HistoryService(logger);
        var selection = new SelectionService();

        var project = new ChartProject();
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0
            });
        }

        // One vocal track with one syllable, so there is something to select and
        // something for the import to replace.
        var track = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase();
        phrase.Lyrics.Add(new LyricEvent { Text = "hold" });
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        state.ActiveProject = project;

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state,
            new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection,
            new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = project.Chart,
            ActiveVocalTrack = track
        };

        var storage = new FakeFileStorageService { NextOpenFile = lrc };
        var inspector = new LyricsInspectorViewModel(editor, history, state, logger, storage,
            new LrcLyricsParser(), new NoopAutoSync(), FakeLocalization.English());

        return new Rig(inspector, editor, selection, history, track);
    }

    private sealed class NoopAutoSync : ILyricAutoSyncService
    {
        public string ModelDirectory => "";
        public bool IsModelInstalled => false;

        public Task<LyricAutoSyncOutcome> SyncAsync(string lyricsText, string audioPath,
            IList<BeatMarker> beats, LyricAutoSyncOptions? options = null,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("these tests never reach auto-sync");
    }
}

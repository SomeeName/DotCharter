using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Lyrics;
using DotCharter.Services.Import.Lyrics;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     One line of two syllables ("la lo") on a <see cref="ChartEditHarness" />, the lyrics inspector
///     over it with the first syllable selected, and a draft typed into its text box and NOT committed
///     — what a focused box holds when Ctrl+S, an export or a close arrives.
/// </summary>
internal sealed class LyricDraftRig
{
    public LyricDraftRig(ChartEditHarness harness, string draft, IFileStorageService? storage = null)
    {
        Syllable = Lyric(1, "la", false);
        Phrase = new LyricPhrase
        {
            Position = new FractionalPosition(1), EndPosition = new FractionalPosition(3),
            Lyrics = [Syllable, Lyric(2, "lo", true)]
        };
        Track = new VocalTrack { Name = "Vocals", Phrases = [Phrase] };
        harness.Project.VocalTracks.Add(Track);
        harness.Editor.ActiveVocalTrack = Track;
        Inspector = NewInspector(harness.Editor, harness.History, harness.State, storage);
        harness.Editor.Selection.SelectedLyric = Syllable;
        Inspector.SelectedLyricText = draft;
        Assert.Equal("la", Syllable.Text);
    }

    public LyricsInspectorViewModel Inspector { get; }
    public LyricEvent Syllable { get; }
    public LyricPhrase Phrase { get; }
    public VocalTrack Track { get; }

    /// <summary>A lyrics inspector over a real editor; nothing in these tests auto-syncs.</summary>
    public static LyricsInspectorViewModel NewInspector(ChartEditorViewModel editor, IHistoryService history,
        ProjectState state, IFileStorageService? storage = null, IConfigurationService? config = null) =>
        new(editor, history, state, new FakeAppLogger(), storage ?? new FakeFileStorageService(),
            new LyricsFileService(new ExportTargetWriter(), new LrcLyricsParser()), new UnusedAutoSync(),
            FakeLocalization.English(), config);

    private static LyricEvent Lyric(int beat, string text, bool end) => new()
    {
        Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 1),
        Text = text, IsPhraseEnd = end, Note = 60
    };

    private sealed class UnusedAutoSync : ILyricAutoSyncRunner
    {
        public Task<LyricAutoSyncRunResult> RunAsync(string rawLyrics, IList<BeatMarker> liveBeats,
            Action<int> onModelDownloadStarting, IProgress<string> progress) => throw new NotSupportedException();
    }
}

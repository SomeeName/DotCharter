using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Core.Utilities;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The timeline's lyric text draft. Only a commit writes chart state.</summary>
public sealed class LyricInlineEditorViewModel(ChartEditDialogServices edit) : ObservableObject
{
    private ChartProject? _project;
    private VocalTrack? _track;
    private LyricEvent? _editing;
    private FractionalPosition _position;
    private bool _isOpen;
    private double _placementMs;
    private string _text = "";

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public double PlacementMs
    {
        get => _placementMs;
        private set => SetProperty(ref _placementMs, value);
    }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public static (string Text, bool IsWordPart) ParseText(string input)
    {
        var text = input.Trim();
        var wordPart = text.EndsWith('-');
        if (wordPart) text = text[..^1].TrimEnd();
        return (text, wordPart && text.Length > 0);
    }

    public static LyricPhrase? FindPhrase(IEnumerable<LyricPhrase> phrases, FractionalPosition position) =>
        phrases.FirstOrDefault(p => p.Position <= position && position <= p.EndPosition);

    public void Begin(double timeMs, LyricEvent? lyric)
    {
        Cancel();
        var editor = edit.Editor;
        _project = edit.State.ActiveProject;
        _track = editor.ActiveVocalTrack;
        if (_project == null || editor.Beats.Count == 0) return;
        if (lyric != null && (_track == null || !_track.Phrases.Any(p => p.Lyrics.Contains(lyric)))) return;
        _position = lyric?.Position ?? editor.SnapToGrid(Math.Max(0, timeMs));
        _editing = lyric ?? _track?.Phrases.SelectMany(p => p.Lyrics).FirstOrDefault(l => l.Position == _position);
        Text = _editing == null ? "" : _editing.Text + (_editing.IsWordPart ? "-" : "");
        PlacementMs = _position.GetPositionMs(editor.Beats);
        if (_editing != null) Select(_editing);
        IsOpen = true;
    }

    /// <summary>Commits once, optionally opening the chronological neighbour for Tab navigation.</summary>
    public bool Commit(int direction = 0)
    {
        if (!IsOpen) return false;
        var editor = edit.Editor;
        if (!ReferenceEquals(_project, edit.State.ActiveProject) ||
            !ReferenceEquals(_track, editor.ActiveVocalTrack) ||
            (_track != null && !_project!.VocalTracks.Contains(_track)) ||
            (_editing != null && !_track!.Phrases.Any(p => p.Lyrics.Contains(_editing))))
        {
            Cancel();
            return true;
        }

        var (text, wordPart) = ParseText(Text);
        if (text.Length == 0)
        {
            if (_editing != null) return false;
            Cancel();
            return true;
        }

        var track = _track ?? new VocalTrack { Name = edit.Localization["InlineLyric_TrackName"] };
        var lyric = _editing;
        IChartAction action;
        if (lyric != null)
        {
            action = LyricWordPartEdit.Create([track.Phrases], lyric, text, wordPart);
        }
        else
        {
            (lyric, action) = PlanPlacement(track, text, wordPart);
        }

        if (action.HasEffect && !edit.History.Do(action)) return false;
        Select(lyric);
        var ordered = track.Phrases.SelectMany(p => p.Lyrics).OrderBy(l => l.Position).ToList();
        var index = ordered.IndexOf(lyric) + Math.Sign(direction);
        Cancel();
        editor.RequestRedraw();
        if (direction != 0 && index >= 0 && index < ordered.Count) Begin(0, ordered[index]);
        return true;
    }

    private (LyricEvent Lyric, IChartAction Action) PlanPlacement(VocalTrack track, string text, bool wordPart)
    {
        var phrase = FindPhrase(track.Phrases, _position);
        var next = track.Phrases.SelectMany(p => p.Lyrics).Where(l => l.Position > _position)
            .OrderBy(l => l.Position).FirstOrDefault();
        var end = TimelineMath.PositionFromMs(PlacementMs + 250, edit.Editor.Beats);
        if (next != null && next.Position < end) end = next.Position;
        // A word part placed after a line's last syllable joins the next line, as the word-part
        // flag does; with no next line the line end stays and the word part goes (LyricLineRule).
        var endsLine = phrase == null || phrase.Lyrics.All(l => l.Position < _position);
        var joins = endsLine && wordPart && (phrase == null
            ? track.Phrases.Any(p => p.Position > _position)
            : track.Phrases.IndexOf(phrase) + 1 < track.Phrases.Count);
        var lyric = new LyricEvent { Position = _position, EndPosition = end, Text = text };
        LyricLineRule.Apply(lyric, wordPart, endsLine && !joins);
        var actions = new List<IChartAction>();
        if (_track == null) actions.Add(new AddActiveVocalTrackAction(edit.Editor, _project!.VocalTracks, track));
        if (phrase == null)
        {
            phrase = new LyricPhrase { Position = _position, EndPosition = end, Lyrics = [lyric] };
            actions.Add(new AddElementsAction<LyricPhrase>(track.Phrases, [phrase], p => p.Position));
        }
        else
        {
            actions.Add(new AddElementsAction<LyricEvent>(phrase.Lyrics, [lyric], l => l.Position));
            var last = phrase.Lyrics.LastOrDefault();
            if (lyric.IsPhraseEnd && last is { IsPhraseEnd: true })
                actions.Add(new RenameLyricAction(last, last.Text, last.Text, last.IsWordPart, last.IsWordPart, true, false));
            if (phrase.EndPosition < end)
                actions.Add(new ResizeLyricPhraseAction(phrase, phrase.EndPosition, end));
        }

        if (joins) actions.Add(new JoinNextLyricLineAction(track.Phrases, lyric));
        return (lyric, new CompositeAction(actions) { LinkPolicyOverride = LinkPolicy.Retain });
    }

    public void Cancel()
    {
        IsOpen = false;
        _project = null;
        _track = null;
        _editing = null;
    }

    private void Select(LyricEvent lyric)
    {
        edit.Editor.Selection.ClearSelections();
        edit.Editor.Selection.SelectedLyric = lyric;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The Song tab's information card (inspector design D): title, artist, album, year and the key (its
///     accidentals and the Minor toggle) typed or picked in place and STAGED, written together by the footer's
///     "Apply song details" as ONE undo node (the same <see cref="SetSongMetadataAction" /> File ▸ Song
///     metadata… saves through), or put back by Revert.
///     <list type="bullet">
///         <item>A re-read on a history change moves the baseline: a field the user has not touched follows the
///         chart, a field with an unapplied edit keeps it, so an undo elsewhere never drops typing.</item>
///         <item>Another project drops the draft (<see cref="ReadNewProject" />): it was typed for the project that
///         left. It is a pending draft (<see cref="ApplyPending" />): the one unsaved-changes prompt before a close or
///         a project switch counts it and its Save applies it; a plain Save leaves it staged.</item>
///     </list>
/// </summary>
public sealed partial class SongDetailsDraft : ObservableObject
{
    private readonly Func<ChartProject?> _project;
    private readonly IHistoryService _history;
    private Baseline _baseline = Baseline.Empty;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDirty))] private string _title = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDirty))] private string _artist = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDirty))] private string _album = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDirty))] private string _year = "";

    /// <summary>The staged key: −7 (seven flats) … +7 (seven sharps).</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDirty))] private int _keyAccidentals;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDirty))] private bool _keyIsMinor;
    [ObservableProperty] private bool _hasProject;

    public SongDetailsDraft(Func<ChartProject?> project, IHistoryService history)
    {
        _project = project;
        _history = history;
        Reload();
    }

    /// <summary>Some field differs from the chart: Apply and Revert are live.</summary>
    public bool IsDirty => HasProject && Current != _baseline;

    private Baseline Current => new(Title, Artist, Album, Year, KeyAccidentals, KeyIsMinor);

    /// <summary>Re-reads the chart; an untouched field follows it, an edited one keeps the user's text.</summary>
    public void Reload() => Read(keepEdits: true);

    /// <summary>Another project is open: every field reads it, an unapplied edit of the last one is dropped.</summary>
    public void ReadNewProject() => Read(keepEdits: false);

    /// <summary>The editor's pending-draft Apply (the close or replace prompt's Save): one undo node.</summary>
    public void ApplyPending()
    {
        if (IsDirty) Apply();
    }

    private void Read(bool keepEdits)
    {
        var chart = _project()?.Chart;
        HasProject = chart is not null;
        var next = chart is null
            ? Baseline.Empty
            : new Baseline(chart.SongName, chart.Artist, chart.AlbumName, chart.AlbumYear, Math.Clamp(chart.KeyAccidentals, -7, 7),
                chart.KeyIsMinor);
        var old = keepEdits ? _baseline : Current;
        _baseline = next;
        if (Title == old.Title) Title = next.Title;
        if (Artist == old.Artist) Artist = next.Artist;
        if (Album == old.Album) Album = next.Album;
        if (Year == old.Year) Year = next.Year;
        if ((KeyAccidentals, KeyIsMinor) == (old.KeyAccidentals, old.KeyIsMinor))
        {
            KeyAccidentals = next.KeyAccidentals;
            KeyIsMinor = next.KeyIsMinor;
        }

        OnPropertyChanged(nameof(IsDirty));
        NotifyVerbs();
    }

    partial void OnTitleChanged(string value) => NotifyVerbs();

    partial void OnArtistChanged(string value) => NotifyVerbs();

    partial void OnAlbumChanged(string value) => NotifyVerbs();

    partial void OnYearChanged(string value) => NotifyVerbs();

    partial void OnKeyAccidentalsChanged(int value) => NotifyVerbs();

    partial void OnKeyIsMinorChanged(bool value) => NotifyVerbs();

    private void NotifyVerbs()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Writes every staged field as one undo node.</summary>
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Apply()
    {
        var chart = _project()?.Chart;
        if (chart is null) return;
        var before = SongMetadataFields.From(chart);
        var after = before with
        {
            SongName = Title.Trim(),
            Artist = Artist.Trim(),
            AlbumName = Album.Trim(),
            AlbumYear = Year.Trim(),
            KeyAccidentals = KeyAccidentals,
            KeyIsMinor = KeyIsMinor
        };
        var action = new SetSongMetadataAction(chart, before, after);
        if (action.HasEffect) _history.Do(action);
        // a no-op Apply (only whitespace differed) still settles every field on the chart's answer.
        Read(keepEdits: false);
    }

    /// <summary>Drops every unapplied edit.</summary>
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert()
    {
        Title = _baseline.Title;
        Artist = _baseline.Artist;
        Album = _baseline.Album;
        Year = _baseline.Year;
        KeyAccidentals = _baseline.KeyAccidentals;
        KeyIsMinor = _baseline.KeyIsMinor;
    }

    private sealed record Baseline(string Title, string Artist, string Album, string Year, int KeyAccidentals, bool KeyIsMinor)
    {
        public static readonly Baseline Empty = new("", "", "", "", 0, false);
    }
}

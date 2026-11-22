using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     Which level every note and chord of a project sits on, for the staged-edit engine's "is this sound still in the
///     project". Built once and kept while the project's SHAPE holds: it goes stale when a level's notes
///     or chords gain, lose or swap a sound (their <c>CollectionChanged</c>), or when an arrangement, a level or one of
///     those collections is replaced (<see cref="IsCurrentFor" /> compares them by reference). An in-place edit, which is
///     most of the history, keeps it, so a history change under a draft costs a walk over the levels, not the sounds.
/// </summary>
internal sealed class ProjectSoundIndex : IDisposable
{
    private readonly ChartProject? _project;
    private readonly List<ChartLevel> _levels = [];
    private readonly List<(ObservableCollection<GuitarNote> Notes, ObservableCollection<Chord> Chords)> _shape = [];
    private readonly Dictionary<ITimedElement, ChartLevel> _levelOf = new(ReferenceEqualityComparer.Instance);
    private bool _stale;

    public ProjectSoundIndex(ChartProject? project)
    {
        _project = project;
        foreach (var level in project?.Arrangements.SelectMany(a => a.Levels) ?? [])
        {
            _levels.Add(level);
            _shape.Add((level.Notes, level.Chords));
            level.Notes.CollectionChanged += OnSoundsChanged;
            level.Chords.CollectionChanged += OnSoundsChanged;
            foreach (var note in level.Notes) _levelOf.TryAdd(note, level);
            foreach (var chord in level.Chords) _levelOf.TryAdd(chord, level);
        }
    }

    /// <summary>Every level of every arrangement, in project order.</summary>
    public IReadOnlyList<ChartLevel> Levels => _levels;

    /// <summary>The level <paramref name="sound" /> sits on, or null when it left the project.</summary>
    public ChartLevel? LevelOf(ITimedElement sound) => _levelOf.GetValueOrDefault(sound);

    public bool Contains(ITimedElement sound) => _levelOf.ContainsKey(sound);

    /// <summary>True while no sound was added, removed or replaced and the project's levels are the ones indexed.</summary>
    public bool IsCurrentFor(ChartProject? project)
    {
        if (_stale || !ReferenceEquals(project, _project)) return false;
        var i = 0;
        foreach (var level in project?.Arrangements.SelectMany(a => a.Levels) ?? [])
        {
            if (i >= _levels.Count || !ReferenceEquals(level, _levels[i]) || !ReferenceEquals(level.Notes, _shape[i].Notes)
                || !ReferenceEquals(level.Chords, _shape[i].Chords))
            {
                return false;
            }

            i++;
        }

        return i == _levels.Count;
    }

    private void OnSoundsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _stale = true;

    public void Dispose()
    {
        foreach (var (notes, chords) in _shape)
        {
            notes.CollectionChanged -= OnSoundsChanged;
            chords.CollectionChanged -= OnSoundsChanged;
        }
    }
}

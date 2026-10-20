// RigDockUnit: the capture library's SEARCH — the filtered projection of Captures the amp face lists, and the rule for when the library combo is redundant.

using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class RigDockUnit
{
    private string _captureFilter = string.Empty;
    private ObservableCollection<RigCaptureRow>? _filteredCaptures;

    /// <summary>
    ///     The search text over the capture library ("why
    ///     not have this searchable"). A library of forty captures is not a
    ///     list to scroll; it is a list to type into. Case-insensitive, matched
    ///     against the row's title; empty shows everything.
    /// </summary>
    public string CaptureFilter
    {
        get => _captureFilter;
        set
        {
            if (!SetProperty(ref _captureFilter, value ?? string.Empty)) return;
            RefilterCaptures();
        }
    }

    /// <summary>The rows the list shows: <see cref="Captures" /> through
    /// <see cref="CaptureFilter" />. Reconciled in place, never re-created, so
    /// the list's containers survive a keystroke.</summary>
    public ObservableCollection<RigCaptureRow> FilteredCaptures
    {
        get
        {
            if (_filteredCaptures is null)
            {
                _filteredCaptures = [];
                Captures.CollectionChanged += OnCapturesChanged;
                RefilterCaptures();
            }
            return _filteredCaptures;
        }
    }

    /// <summary>True when the filter hides every capture — the list then says
    /// so instead of going silently blank.</summary>
    public bool HasNoCaptureMatches => HasCaptures && FilteredCaptures.Count == 0;

    /// <summary>
    ///     The "From library" combo in the model bar duplicates the capture
    ///     list beside the head — same library, same rows. It shows only when
    ///     there is no list to show (a pedal's IR picker, an amp with library
    ///     choices but no capture column), so a capture is never offered twice.
    /// </summary>
    public bool ShowsLibraryCombo => FileParam is { HasLibraryChoices: true } && !HasCaptures;

    private void OnCapturesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefilterCaptures();
        OnPropertyChanged(nameof(ShowsLibraryCombo));
    }

    private void RefilterCaptures()
    {
        if (_filteredCaptures is null) return;
        var wanted = MatchingCaptures(Captures, _captureFilter);

        // Reconcile: drop rows that are not wanted, then insert the missing ones at
        // their library position, so surviving rows keep their containers.
        for (var i = _filteredCaptures.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_filteredCaptures[i])) _filteredCaptures.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < _filteredCaptures.Count && ReferenceEquals(_filteredCaptures[i], wanted[i])) continue;
            _filteredCaptures.Insert(i, wanted[i]);
        }
        OnPropertyChanged(nameof(HasNoCaptureMatches));
    }

    /// <summary>The pure rule: every row whose title contains the filter,
    /// library order kept. Public static so the default gate can pin it
    /// without a library.</summary>
    public static List<RigCaptureRow> MatchingCaptures(IEnumerable<RigCaptureRow> rows, string? filter)
    {
        var needle = (filter ?? string.Empty).Trim();
        var list = new List<RigCaptureRow>();
        foreach (var row in rows)
        {
            if (needle.Length == 0 || row.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                list.Add(row);
        }
        return list;
    }
}

// RigCaptureRow: one capture in the dock's capture column, lit while its target param plays it.
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     One capture in the amp face's CAPTURE column. Clicking it pushes the
///     file into the SAME string param the Browse… button writes, so a capture
///     loaded from here and one picked off disk take one code path.
/// </summary>
public sealed partial class RigCaptureRow : ObservableObject
{
    private readonly PedalStringParamViewModel _target;

    public RigCaptureRow(NamCaptureEntry entry, PedalStringParamViewModel target)
    {
        _target = target;
        FilePath = entry.FilePath;
        Title = entry.Manifest.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(entry.FilePath);
        // Proper nouns off the manifest ("NAM", "AIDA-X"), deliberately not
        // localized — the same call the faceplate's format chip already makes.
        FormatLabel = entry.Manifest.Format.ToUpperInvariant();
        WatchTarget(target);
    }

    public string FilePath { get; }
    public string Title { get; }
    public string FormatLabel { get; }

    /// <summary>True when this capture is the one the unit is currently
    /// playing — the column's selection mark.</summary>
    public bool IsActive =>
        string.Equals(_target.Value, FilePath, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Load() => _target.LoadFile(FilePath);

    /// <summary>Every row follows the param's value, so loading B (from this column,
    /// Browse…, a preset or an undo) also clears A's mark. Weakly held: the
    /// rows are rebuilt with the dock, the param outlives them. A row built over no
    /// target (the pure filter tests) watches nothing.</summary>
    private void WatchTarget(PedalStringParamViewModel? target)
    {
        if (target is null) return;
        var weak = new WeakReference<RigCaptureRow>(this);
        PropertyChangedEventHandler? changed = null;
        changed = (_, e) =>
        {
            if (!weak.TryGetTarget(out var row)) target.PropertyChanged -= changed;
            else if (e.PropertyName == nameof(PedalStringParamViewModel.Value)) row.OnPropertyChanged(nameof(IsActive));
        };
        target.PropertyChanged += changed;
    }
}

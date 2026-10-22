using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DotCharter.Core.Domain.Events;

public enum ChartAnnotationKind
{
    Note,
    Reminder,
    Mark
}

/// <summary>
///     A charter's inline annotation on the timeline: a free-text note,
///     reminder or mark bound to a time position. Persisted in the .dcproj
///     project (see RscpWriter / RscpHeaderParser). Property-change
///     notification keeps the Notes panel live while items are resolved.
/// </summary>
public class ChartAnnotation : INotifyPropertyChanged
{
    private string _text = "";
    private bool _resolved;

    public double TimeMs { get; set; }
    public ChartAnnotationKind Kind { get; set; } = ChartAnnotationKind.Note;

    /// <summary>Display label for list panels ("m:ss.fff").</summary>
    public string TimeLabel => TimeSpan.FromMilliseconds(Math.Max(0, TimeMs)).ToString(@"m\:ss\.fff");

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Checked off by the charter once the reminder is handled.</summary>
    public bool Resolved
    {
        get => _resolved;
        set
        {
            if (_resolved == value) return;
            _resolved = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

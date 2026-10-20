using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The vocal track's name and lyric colour. One instance per open; Save is one undo node.</summary>
public sealed class VocalTrackSettingsViewModel : DialogViewModel<bool>, IVocalTrackSettingsScreen,
    IDialogSetup<VocalTrackSettingsRequest>
{
    private readonly IChartEditorSurface _editor;
    private readonly ProjectState _state;
    private readonly IHistoryService _history;
    private VocalTrack? _newTrack;
    private string _name = "Vocals";
    private string _trackColor = "#FFFFFF";

    public VocalTrackSettingsViewModel(IChartEditorSurface editor, ProjectState state, IHistoryService history,
        ILocalizationService localization)
    {
        _editor = editor;
        _state = state;
        _history = history;
        ColorSwatches =
        [
            new Swatch(this, "#FFFFFF", localization["Dialog_LyricColor_White"]),
            new Swatch(this, "#A78BFA", localization["Dialog_LyricColor_Purple"]),
            new Swatch(this, "#4ADE80", localization["Dialog_LyricColor_Green"]),
            new Swatch(this, "#F0A54A", localization["Dialog_LyricColor_Amber"])
        ];
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
        SyncSwatches();
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? "");
    }

    public string TrackColor
    {
        get => _trackColor;
        set
        {
            if (SetProperty(ref _trackColor, value ?? "")) SyncSwatches();
        }
    }

    public IReadOnlyList<ILyricColorSwatch> ColorSwatches { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>Edits the active track, or starts a new one named after the next free number.</summary>
    public void Configure(VocalTrackSettingsRequest request)
    {
        if (request.CreateNew)
        {
            var count = _state.ActiveProject?.VocalTracks.Count ?? 0;
            _newTrack = new VocalTrack { Name = $"Vocals {count + 1}" };
            Name = _newTrack.Name;
            TrackColor = "#FFFFFF";
            return;
        }

        if (_editor.ActiveVocalTrack is not { } active) return;
        Name = active.Name;
        TrackColor = ToHex(active.ColorA, active.ColorR, active.ColorG, active.ColorB);
    }

    private void Save()
    {
        var (a, r, g, b) = ParseHex(TrackColor);
        var style = new StyleFields(Name, r, g, b, a);
        if (_newTrack != null)
        {
            // The new track is not in the chart yet: style it directly, and the
            // ADD (with the active-track switch) is the undoable act.
            style.ApplyTo(_newTrack);
            if (_state.ActiveProject is { } project)
                _history.Do(new AddVocalTrackAction(_editor, project.VocalTracks, _newTrack));
        }
        else if (_editor.ActiveVocalTrack is { } active)
        {
            _history.Do(new SetStyleAction(active, StyleFields.From(active), style));
        }

        Close(true);
    }

    /// <summary>Alpha is ignored: the swatches are opaque and the picker owns transparency.</summary>
    private void SyncSwatches()
    {
        var (_, r, g, b) = ParseHex(TrackColor);
        foreach (var swatch in ColorSwatches.Cast<Swatch>())
        {
            var (_, sr, sg, sb) = ParseHex(swatch.Color);
            swatch.IsSelected = sr == r && sg == g && sb == b;
        }
    }

    private static string ToHex(byte a, byte r, byte g, byte b) =>
        a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{a:X2}{r:X2}{g:X2}{b:X2}";

    /// <summary><c>#RRGGBB</c> or <c>#AARRGGBB</c>; anything else reads as opaque white.</summary>
    private static (byte A, byte R, byte G, byte B) ParseHex(string hex)
    {
        var digits = hex.TrimStart('#');
        if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return (255, 255, 255, 255);
        return digits.Length switch
        {
            6 => (255, (byte)(value >> 16), (byte)(value >> 8), (byte)value),
            8 => ((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value),
            _ => (255, 255, 255, 255)
        };
    }

    private sealed class Swatch : ObservableObject, ILyricColorSwatch
    {
        private bool _isSelected;

        public Swatch(VocalTrackSettingsViewModel owner, string color, string name)
        {
            Color = color;
            Name = name;
            PickCommand = new RelayCommand(() => owner.TrackColor = color);
        }

        public string Name { get; }

        public string Color { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public ICommand PickCommand { get; }
    }

    private sealed record StyleFields(string Name, byte R, byte G, byte B, byte A)
    {
        public static StyleFields From(VocalTrack track) =>
            new(track.Name, track.ColorR, track.ColorG, track.ColorB, track.ColorA);

        public void ApplyTo(VocalTrack track)
        {
            track.Name = Name;
            track.ColorR = R;
            track.ColorG = G;
            track.ColorB = B;
            track.ColorA = A;
        }
    }

    private sealed class SetStyleAction(VocalTrack track, StyleFields before, StyleFields after) : IChartAction
    {
        public bool HasEffect => before != after;

        public void Execute() => after.ApplyTo(track);

        public void Undo() => before.ApplyTo(track);
    }

    /// <summary>The add and the active-track switch are one node; undo restores the previous pick.</summary>
    private sealed class AddVocalTrackAction(IChartEditorSurface editor, IList<VocalTrack> tracks, VocalTrack track)
        : IChartAction
    {
        private readonly VocalTrack? _previousActive = editor.ActiveVocalTrack;

        public void Execute()
        {
            tracks.Add(track);
            editor.ActiveVocalTrack = track;
        }

        public void Undo()
        {
            tracks.Remove(track);
            editor.ActiveVocalTrack = _previousActive;
        }
    }
}

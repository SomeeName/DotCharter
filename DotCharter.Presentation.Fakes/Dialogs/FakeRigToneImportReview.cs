using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IRigToneImportReview" /> for the gallery and the view's smoke test.</summary>
public sealed class FakeRigToneImportReview : ObservableObject, IRigToneImportReview
{
    private bool _replaceEdited;

    private FakeRigToneImportReview(IReadOnlyList<IRigToneImportRow> rows, IReadOnlyList<string> losses, string refused)
    {
        Rows = rows;
        Losses = losses;
        RefusedReason = refused;
    }

    /// <summary>A NAM capture played the amp, a pedal was left out, a module had no counterpart.</summary>
    public static FakeRigToneImportReview WithLosses() => new(
        [
            new Row("Pre-pedal 1", "Noise Gate", "Noise Gate", "100 %", true, true),
            new Row("Pre-pedal 2", "Overdrive", "Overdrive", "97 %", true, true),
            new Row("Amplifier", "AMTW-22", "NAM", "", true, false),
            new Row("Cabinet", "Marshall 1960TV (Ribbon, Cone)", "Cabinet + Mic", "100 %", true, true),
            new Row("Left out", "Studio Verb", "Hall Reverb", "92 %", false, true)
        ],
        [
            "NAM capture 'My Plexi' → AMTW-22 (an approximation at default knobs).",
            "Looper: no Rocksmith gear becomes this module, so it is left out.",
            "Hall Reverb is left out."
        ],
        "");

    public IReadOnlyList<IRigToneImportRow> Rows { get; }
    public IReadOnlyList<string> Losses { get; }
    public bool HasLosses => Losses.Count > 0;
    public string Summary => "Knob match: 74 %";
    public string RefusedReason { get; }
    public bool IsRefused => RefusedReason.Length > 0;
    public bool IsBusy => false;
    public bool CanReplace => true;
    public string ReplaceLabel => "Replace 'Clean verse'";

    public bool ReplaceEdited
    {
        get => _replaceEdited;
        set
        {
            if (SetProperty(ref _replaceEdited, value)) OnPropertyChanged(nameof(AddAsNew));
        }
    }

    public bool AddAsNew { get => !_replaceEdited; set => ReplaceEdited = !value; }
    public ICommand AcceptCommand { get; } = new RelayCommand(() => { });
    public ICommand CancelCommand { get; } = new RelayCommand(() => { });

    private sealed class Row(string slot, string gear, string from, string match, bool include, bool canToggle) : ObservableObject, IRigToneImportRow
    {
        private bool _include = include;

        public string Slot { get; } = slot;
        public string Gear { get; } = gear;
        public string From { get; } = from;
        public string Match { get; } = match;
        public bool Include { get => _include; set => SetProperty(ref _include, value); }
        public bool CanToggle { get; } = canToggle;
    }
}

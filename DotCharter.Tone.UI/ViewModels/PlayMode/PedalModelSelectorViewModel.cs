using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>Which model-picker presentation a labelled param gets: the amp
/// voicing opens a grouped popup (24 models, American/British/…); cab/od/dist
/// get a flat compact selector.</summary>
public enum PedalModelSelectorKind
{
    Flat,
    AmpVoicing,
    CabModel
}

/// <summary>One selectable model on a picker: its 0-based index (== the param
/// value) and its ABI label.</summary>
public sealed class PedalModelOption
{
    public PedalModelOption(int index, string label)
    {
        Index = index;
        Label = label;
    }

    public int Index { get; }
    public string Label { get; }

    /// <summary>Picks THIS model (set by the owning selector) — bound directly by
    /// the amp popup's option button so there's no ElementName/ancestor binding
    /// across the popup boundary.</summary>
    public System.Windows.Input.ICommand? SelectCommand { get; set; }
}

/// <summary>A named group of models in the amp popup (e.g. "British").</summary>
public sealed class PedalModelGroupRow
{
    public PedalModelGroupRow(string header, IReadOnlyList<PedalModelOption> options)
    {
        Header = header;
        Options = options;
    }

    public string Header { get; }
    public IReadOnlyList<PedalModelOption> Options { get; }
}

/// <summary>
/// A model picker over a labelled DSP param (ABI v24 value labels). Wraps the
/// underlying <see cref="PedalParamViewModel"/>: the flat <see cref="Options"/>
/// drive a compact selector (cab/od/dist), the <see cref="Groups"/> drive the
/// amp face's grouped popup. Selecting an option writes the param's value
/// (index == value − min) live through the existing param setter, and an
/// external value change (preset apply, ‹ ›) reflects back into
/// <see cref="SelectedOption"/> / <see cref="CurrentLabel"/>. Grouping is the
/// pure <see cref="DspModelGrouping"/> index table; the label STRINGS come from
/// the ABI. Headless-testable (no Avalonia).
/// </summary>
public sealed partial class PedalModelSelectorViewModel : ObservableObject
{
    private readonly PedalParamViewModel _param;
    private bool _suppress;

    public PedalModelSelectorViewModel(PedalParamViewModel param, PedalModelSelectorKind kind,
        ILocalizationService localization)
    {
        _param = param;
        Kind = kind;
        DisplayName = param.DisplayName;

        var labels = param.ValueLabels ?? [];
        Options = new ObservableCollection<PedalModelOption>(
            labels.Select((l, i) => new PedalModelOption(i, l)));
        foreach (var o in Options)
            o.SelectCommand = new RelayCommand(() => Select(o));
        Groups = BuildGroups(kind, Options, localization);
        IsGrouped = kind == PedalModelSelectorKind.AmpVoicing && Groups.Count > 1;

        _selectedOption = Options.FirstOrDefault(o => o.Index == param.SelectedIndex);
        _param.PropertyChanged += OnParamChanged;
    }

    /// <summary>The picker presentation (drives grouped vs flat).</summary>
    public PedalModelSelectorKind Kind { get; }

    /// <summary>The param's display name ("Voicing" / "Model").</summary>
    public string DisplayName { get; }

    /// <summary>The underlying native param id ("voicing", "mode", "capture"…) —
    /// the identity a reachability test can match a face's rendered selectors
    /// against the module's params by.</summary>
    public string ParamId => _param.Id;

    /// <summary>Every model as a flat list — the compact selector's item source.</summary>
    public ObservableCollection<PedalModelOption> Options { get; }

    /// <summary>Models bucketed into named groups (amp popup); a single unnamed
    /// group for a flat picker.</summary>
    public IReadOnlyList<PedalModelGroupRow> Groups { get; }

    /// <summary>True when the amp popup should show group headers.</summary>
    public bool IsGrouped { get; }

    /// <summary>The current model's label ("Plexi 100", "4x12 Scooped", …).</summary>
    public string CurrentLabel => _param.CurrentLabel;

    /// <summary>Whether the amp face's model popup is open.</summary>
    [ObservableProperty] private bool _isPopupOpen;

    // `TogglePopupCommand` is DELETED (0.58): there is no model POPUP any more.
    // `AmpFxRigView` picks a model with a plain `ComboBox` over `ModelRail
    // .Options`/`.SelectedOption`, and the selection commits through
    // `OnSelectedOptionChanged` below.

    [ObservableProperty] private PedalModelOption? _selectedOption;

    partial void OnSelectedOptionChanged(PedalModelOption? value)
    {
        if (_suppress || value == null) return;
        _param.SelectedIndex = value.Index; // writes native
    }

    /// <summary>Picks a model (the grouped popup's button command), then closes
    /// the popup.</summary>
    [RelayCommand]
    private void Select(PedalModelOption? option)
    {
        if (option == null) return;
        _param.SelectedIndex = option.Index;
        IsPopupOpen = false;
    }

    private void OnParamChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PedalParamViewModel.Value)
            or nameof(PedalParamViewModel.SelectedIndex)))
        {
            return;
        }

        _suppress = true;
        SelectedOption = Options.FirstOrDefault(o => o.Index == _param.SelectedIndex);
        _suppress = false;
        OnPropertyChanged(nameof(CurrentLabel));
    }

    private static IReadOnlyList<PedalModelGroupRow> BuildGroups(
        PedalModelSelectorKind kind, IReadOnlyList<PedalModelOption> options, ILocalizationService localization)
    {
        switch (kind)
        {
            case PedalModelSelectorKind.AmpVoicing:
                return DspModelGrouping.AmpGroupOrder
                    .Select(g => new PedalModelGroupRow(
                        localization[DspModelGrouping.AmpGroupResourceKey(g)],
                        options.Where(o => DspModelGrouping.AmpVoicingGroup(o.Index) == g).ToList()))
                    .Where(r => r.Options.Count > 0)
                    .ToList();

            case PedalModelSelectorKind.CabModel:
                return Enum.GetValues<DspModelGrouping.CabModelGroup>()
                    .Select(g => new PedalModelGroupRow(
                        localization[DspModelGrouping.CabGroupResourceKey(g)],
                        options.Where(o => DspModelGrouping.CabGroup(o.Index) == g).ToList()))
                    .Where(r => r.Options.Count > 0)
                    .ToList();

            default:
                return new[] { new PedalModelGroupRow(string.Empty, options) };
        }
    }
}

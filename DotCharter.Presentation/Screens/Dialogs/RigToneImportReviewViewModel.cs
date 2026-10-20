using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>What the review converts: the player's rig, the key the tone gets, the edited tone it could replace and the module names.</summary>
/// <param name="Rig">The player's rig, snapshotted once.</param>
/// <param name="Key">The key the tone is converted under.</param>
/// <param name="ReplaceName">The edited tone's name, or null when there is none to replace.</param>
/// <param name="ModuleName">A module type id's display name.</param>
public sealed record RigToneImportSource(DcRigPreset Rig, string Key, string? ReplaceName, Func<string, string> ModuleName);

/// <summary>
///     The Tone designer's "from tone rig" review over one rig snapshot: runs
///     <see cref="RigToRsToneConverter" /> off the UI thread (latest wins), shows the chain and the
///     losses, re-runs when a row is unticked, and hands the tone to <c>accept</c> only while it is
///     not refused. Writes nothing itself.
/// </summary>
public sealed class RigToneImportReviewViewModel : ObservableObject, IRigToneImportReview
{
    private readonly ILocalizationService _loc;
    private readonly DcRigPreset _rig;
    private readonly string _key;
    private readonly Func<string, string> _moduleName;
    private readonly Func<Func<RigToRsToneResult>, Task<RigToRsToneResult>> _run;
    private readonly Action<RsTone, bool> _accept;
    private readonly HashSet<int> _leftOut = [];
    private readonly RelayCommand _acceptCommand;
    private RigToRsToneResult? _result;
    private IReadOnlyList<IRigToneImportRow> _rows = [];
    private IReadOnlyList<string> _losses = [];
    private string _summary = "";
    private string _refusedReason = "";
    private bool _isBusy;
    private bool _replaceEdited;
    private int _version;

    /// <param name="loc">The strings.</param>
    /// <param name="source">The rig snapshot and what it is converted as.</param>
    /// <param name="run">Runs a conversion off the UI thread (a test runs it inline).</param>
    /// <param name="accept">Takes the tone and whether it replaces the edited one.</param>
    /// <param name="cancel">Closes the review.</param>
    public RigToneImportReviewViewModel(ILocalizationService loc, RigToneImportSource source,
        Func<Func<RigToRsToneResult>, Task<RigToRsToneResult>> run, Action<RsTone, bool> accept, Action cancel)
    {
        var replaceName = source.ReplaceName;
        _loc = loc;
        _rig = source.Rig;
        _key = source.Key;
        _moduleName = source.ModuleName;
        _run = run;
        _accept = accept;
        CanReplace = replaceName is not null;
        ReplaceLabel = replaceName is null ? "" : loc.Format("RigImport_Replace", replaceName);
        _acceptCommand = new RelayCommand(Accept, () => !_isBusy && _result?.Tone is not null);
        CancelCommand = new RelayCommand(cancel);
        LastRun = ConvertAsync();
    }

    /// <summary>The sentence for one kind of loss; its arguments are the module, the gear and the detail.</summary>
    public static string LossKey(RigToneLossKind kind) => "RigImport_Loss_" + kind;

    /// <summary>The name of a rail that can hold more gear than Rocksmith's four slots.</summary>
    public static string RailKey(RsGearRole rail) => "RigImport_Rail_" + rail;

    /// <summary>The rails <see cref="RailKey" /> names.</summary>
    public static IReadOnlyList<RsGearRole> Rails { get; } = [RsGearRole.PrePedal, RsGearRole.Rack, RsGearRole.PostPedal];

    /// <summary>The last conversion started (a test awaits it).</summary>
    internal Task LastRun { get; private set; }

    public IReadOnlyList<IRigToneImportRow> Rows { get => _rows; private set => SetProperty(ref _rows, value); }

    public IReadOnlyList<string> Losses
    {
        get => _losses;
        private set
        {
            if (SetProperty(ref _losses, value)) OnPropertyChanged(nameof(HasLosses));
        }
    }

    public bool HasLosses => _losses.Count > 0;

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string RefusedReason
    {
        get => _refusedReason;
        private set
        {
            if (SetProperty(ref _refusedReason, value)) OnPropertyChanged(nameof(IsRefused));
        }
    }

    public bool IsRefused => _refusedReason.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) _acceptCommand.NotifyCanExecuteChanged();
        }
    }

    public bool CanReplace { get; }

    public string ReplaceLabel { get; }

    public bool ReplaceEdited
    {
        get => _replaceEdited;
        set
        {
            if (!SetProperty(ref _replaceEdited, value && CanReplace)) return;
            OnPropertyChanged(nameof(AddAsNew));
        }
    }

    public bool AddAsNew
    {
        get => !_replaceEdited;
        set => ReplaceEdited = !value;
    }

    public ICommand AcceptCommand => _acceptCommand;

    public ICommand CancelCommand { get; }

    /// <summary>The last conversion's result; null before the first one lands.</summary>
    internal RigToRsToneResult? Result => _result;

    /// <summary>Leaves the gear that starts at rig module <paramref name="moduleIndex" /> out (or back in) and converts again.</summary>
    internal void SetIncluded(int moduleIndex, bool include)
    {
        if (include ? !_leftOut.Remove(moduleIndex) : !_leftOut.Add(moduleIndex)) return;
        LastRun = ConvertAsync();
    }

    private async Task ConvertAsync()
    {
        var version = ++_version;
        IsBusy = true;
        var leftOut = new HashSet<int>(_leftOut);
        var result = await _run(() => RigToRsToneConverter.Convert(_rig, _key, _key, leftOut));
        if (version != _version) return;
        Show(result);
        IsBusy = false;
    }

    private void Show(RigToRsToneResult result)
    {
        _result = result;
        Rows = [.. result.Slots.Select(s => new Row(this, s))];
        Losses = [.. result.Losses.Select(DescribeLoss)];
        Summary = _loc.Format("RigImport_Match", Percent(result.Closeness));
        RefusedReason = result.IsRefused
            ? _loc.Format("RigImport_Refused", string.Join(", ", result.Overflows.Select(o => _loc.Format("RigImport_RailOver", _loc[RailKey(o.Rail)], o.Count))))
            : "";
        _acceptCommand.NotifyCanExecuteChanged();
    }

    private void Accept()
    {
        if (_isBusy || _result?.Tone is not { } tone) return;
        _accept(tone, _replaceEdited);
    }

    private string DescribeLoss(RigToneLoss loss)
    {
        var module = loss.ModuleIndex >= 0 ? _moduleName(loss.Module) : "";
        var gear = loss.GearKey is { } key && RsGearCatalog.Shared.TryGetGear(key, out var data) ? data.Name : loss.GearKey ?? "";
        return _loc.Format(LossKey(loss.Kind), module, gear, loss.Detail ?? module);
    }

    private string SlotName(RsGearRole role, int index) => role switch
    {
        RsGearRole.Amp => _loc["Dialog_Tones_SlotAmp"],
        RsGearRole.Cabinet => _loc["Dialog_Tones_SlotCabinet"],
        RsGearRole.PrePedal => _loc.Format("Dialog_Tones_SlotPre", index),
        RsGearRole.PostPedal => _loc.Format("Dialog_Tones_SlotLoop", index),
        _ => _loc.Format("Dialog_Tones_SlotRack", index)
    };

    private static string Percent(float closeness) => (closeness * 100f).ToString("0", CultureInfo.CurrentCulture);

    private sealed class Row(RigToneImportReviewViewModel owner, RigToneSlot slot) : ObservableObject, IRigToneImportRow
    {
        public string Slot { get; } = slot.Included ? owner.SlotName(slot.Role, slot.SlotIndex) : owner._loc["RigImport_SlotLeftOut"];

        public string Gear { get; } = slot.GearName;

        public string From { get; } = string.Join(" + ", slot.Modules.Select(owner._moduleName));

        public string Match { get; } = slot.IsStandIn ? "" : Percent(slot.Closeness) + " %";

        public bool CanToggle { get; } = !slot.IsStandIn && slot.ModuleIndex >= 0;

        public bool Include
        {
            get => slot.Included;
            set
            {
                if (value == slot.Included || !CanToggle) return;
                owner.SetIncluded(slot.ModuleIndex, value);
            }
        }
    }
}

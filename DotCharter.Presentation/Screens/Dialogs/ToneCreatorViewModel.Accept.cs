// The AI tone creator's Accept: where the proposal goes (the edited tone, or a new one in a free slot), the optional tone change at the playhead, and the ONE undo step.
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Tones;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

public sealed partial class ToneCreatorViewModel
{
    private string? _editedKey;
    private string? _editedName;
    private bool _isSaveAsNew;
    private string _newToneName = "";
    private bool _placeAtPlayhead;

    public bool IsReplaceEdited
    {
        get => !_isSaveAsNew;
        set
        {
            if (value && !CanReplaceEdited)
            {
                OnPropertyChanged();
                return;
            }

            _isSaveAsNew = !value;
            RaiseTarget();
        }
    }

    public bool IsSaveAsNew
    {
        get => _isSaveAsNew;
        set => IsReplaceEdited = !value;
    }

    public bool CanReplaceEdited => _editedKey is not null;

    public string ReplaceEditedText => _editedKey is null
        ? _edit.Localization["Dialog_ToneCreator_ReplaceNone"]
        : _edit.Localization.Format("Dialog_ToneCreator_Replace", _editedName ?? _editedKey);

    public string NewToneName
    {
        get => _newToneName;
        set
        {
            if (SetProperty(ref _newToneName, value ?? "")) RaiseTarget();
        }
    }

    public string NewToneSlotText
    {
        get
        {
            if (_arrangement is null) return "";
            var slot = ToneCreatorAccept.FreeSlot(_arrangement);
            return slot is null
                ? _edit.Localization.Format("Dialog_ToneCreator_NoFreeSlot", _arrangement.Name)
                : _edit.Localization.Format("Dialog_ToneCreator_Slot", SlotLabel(slot), _arrangement.Name);
        }
    }

    public bool PlaceAtPlayhead
    {
        get => _placeAtPlayhead;
        set
        {
            if (SetProperty(ref _placeAtPlayhead, value && _arrangement is not null)) RaiseTarget();
        }
    }

    public string PlaceAtPlayheadText => _edit.Localization.Format("Dialog_ToneCreator_Place", Clock(_edit.State.CurrentTimeMs));

    public string AcceptUnavailableReason
    {
        get
        {
            if (_proposal is null) return _edit.Localization["Dialog_ToneCreator_CreateFirst"];
            var plan = Plan();
            return plan?.Refusal switch
            {
                null or ToneCreatorRefusal.None => "",
                ToneCreatorRefusal.NoFreeSlot => _edit.Localization.Format("Dialog_ToneCreator_NoFreeSlot", _arrangement!.Name),
                ToneCreatorRefusal.NoEditedTone => _edit.Localization["Dialog_ToneCreator_ReplaceNone"],
                _ => _edit.Localization.Format("Dialog_ToneCreator_Invalid", plan.Detail)
            };
        }
    }

    public bool HasAcceptUnavailableReason => AcceptUnavailableReason.Length > 0;

    /// <summary>The name the model is asked to give the tone: the edited tone's on Replace, the new name otherwise.</summary>
    private string TargetName => !_isSaveAsNew && _editedName is { Length: > 0 } edited ? edited
        : string.IsNullOrWhiteSpace(_newToneName) ? "AI tone" : _newToneName.Trim();

    private ToneCreatorAcceptPlan? Plan() => _project is null || _arrangement is null || _proposal is null
        ? null
        : ToneCreatorAccept.Build(_project, _arrangement, _proposal.Tone,
            _isSaveAsNew ? ToneCreatorTarget.NewTone : ToneCreatorTarget.ReplaceEdited, _editedKey, _newToneName,
            _placeAtPlayhead ? _edit.State.CurrentTimeMs : null);

    /// <summary>Writes the proposal as ONE undo step and answers the key; a refused plan only says why.</summary>
    private void Accept()
    {
        if (Plan() is not { IsReady: true } plan)
        {
            Status = AcceptUnavailableReason;
            return;
        }

        _edit.History.Do(plan.Action!);
        _edit.Logger.Info(AppLogCategory.Project,
            $"AI tone creator accepted into '{plan.Key}' ({(_isSaveAsNew ? "new tone" : "replaced")}, slot={plan.Slot ?? "-"}, placed={_placeAtPlayhead}).");
        Finish(plan.Key);
    }

    private string SlotLabel(string slot) => slot switch
    {
        "Base" => _edit.Localization["Dialog_Tones_ToneBase"],
        _ => _edit.Localization["Dialog_Tones_Slot" + slot]
    };

    private void RaiseTarget()
    {
        OnPropertyChanged(nameof(IsReplaceEdited));
        OnPropertyChanged(nameof(IsSaveAsNew));
        OnPropertyChanged(nameof(NewToneSlotText));
        OnPropertyChanged(nameof(AcceptUnavailableReason));
        OnPropertyChanged(nameof(HasAcceptUnavailableReason));
        ((RelayCommand)AcceptCommand).NotifyCanExecuteChanged();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(HasProposal));
        OnPropertyChanged(nameof(ProposalRows));
        OnPropertyChanged(nameof(Rationale));
        OnPropertyChanged(nameof(EvidenceNote));
        OnPropertyChanged(nameof(CanAudition));
        OnPropertyChanged(nameof(AuditionUnavailableReason));
        OnPropertyChanged(nameof(CanReplaceEdited));
        OnPropertyChanged(nameof(ReplaceEditedText));
        OnPropertyChanged(nameof(NewToneName));
        _refine.NotifyCanExecuteChanged();
        RaiseTarget();
    }

    private static string Clock(double ms)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 100}");
    }
}

using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Edit ▸ Paste special. One instance per open; the choices live in <see cref="DialogMemory" />.</summary>
public sealed class PasteSpecialViewModel : DialogViewModel<bool>, IPasteSpecialScreen, ICancelableDialog
{
    private readonly IGlobalEditingCoordinator _editing;
    private readonly IEditorTransport _transport;
    private readonly ProjectState _state;
    private readonly DialogMemory _memory;
    private readonly IAppLogger _logger;

    public PasteSpecialViewModel(IGlobalEditingCoordinator editing, IEditorTransport transport, ProjectState state,
        DialogMemory memory, IAppLogger logger)
    {
        _editing = editing;
        _transport = transport;
        _state = state;
        _memory = memory;
        _logger = logger;
        ApplyCommand = new RelayCommand(Apply);
        CancelCommand = new RelayCommand(() => TryCancel());
        logger.Info(AppLogCategory.Editing, $"Paste Special dialog opened (mode={Mode}).");
    }

    public PasteSpecialMode Mode
    {
        get => _memory.PasteMode;
        set
        {
            if (_memory.PasteMode == value) return;
            _memory.PasteMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPreserve));
            OnPropertyChanged(nameof(IsOverwriteSameType));
            OnPropertyChanged(nameof(IsOverwriteAll));
        }
    }

    public bool IsPreserve
    {
        get => Mode == PasteSpecialMode.Preserve;
        set
        {
            if (value) Mode = PasteSpecialMode.Preserve;
        }
    }

    public bool IsOverwriteSameType
    {
        get => Mode == PasteSpecialMode.OverwriteSameType;
        set
        {
            if (value) Mode = PasteSpecialMode.OverwriteSameType;
        }
    }

    public bool IsOverwriteAll
    {
        get => Mode == PasteSpecialMode.OverwriteAll;
        set
        {
            if (value) Mode = PasteSpecialMode.OverwriteAll;
        }
    }

    public bool UseMousePosition
    {
        get => _memory.PasteAtMouse;
        set
        {
            if (_memory.PasteAtMouse == value) return;
            _memory.PasteAtMouse = value;
            OnPropertyChanged();
        }
    }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    public bool TryCancel()
    {
        if (!IsClosed) _logger.Info(AppLogCategory.Editing, "Paste Special dialog canceled.");
        Close(false);
        return true;
    }

    private void Apply()
    {
        var timeMs = UseMousePosition ? _state.LastPointerTimeMs : _state.CurrentTimeMs;
        _logger.Info(AppLogCategory.Editing,
            $"Paste Special applied: mode={Mode}, useMouse={UseMousePosition}, timeMs={timeMs:0.###}.");
        _editing.PasteSpecial(timeMs, _transport.GridDivisor, Mode);
        Close(true);
    }
}

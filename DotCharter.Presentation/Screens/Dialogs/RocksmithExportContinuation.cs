namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>One operation's lifetime and edit-free shown baseline; prefill has no export owner, and its token ends with the dialog.</summary>
internal sealed class RocksmithExportContinuation
{
    private readonly RocksmithExportViewModel _screen;
    private readonly Func<bool>? _owner;

    public RocksmithExportContinuation(RocksmithExportViewModel screen, CancellationToken token = default, Func<bool>? owner = null)
    {
        _screen = screen;
        Token = token;
        _owner = owner;
        Shown = IsCurrent ? (screen.AudioVolume, screen.PreviewVolume) : default;
    }

    public CancellationToken Token { get; }
    public (double Audio, double Preview) Shown { get; private set; }
    public bool IsOwner => !_screen.IsDisposed && !_screen.Result.IsCompleted && (_owner?.Invoke() ?? true);
    public bool IsCurrent => IsOwner && !Token.IsCancellationRequested;

    public void RefreshShownWhileBuilding()
    {
        if (_owner is not null && IsCurrent && _screen.IsBuilding) Shown = (_screen.AudioVolume, _screen.PreviewVolume);
    }
}

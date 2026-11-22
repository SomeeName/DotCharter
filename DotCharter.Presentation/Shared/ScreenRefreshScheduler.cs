using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Shared;

/// <summary>Coalesces visible work and keeps hidden work pending for activation.</summary>
internal sealed class ScreenRefreshScheduler(IUiDispatcher dispatcher, Action refresh)
{
    private bool _pending;
    private bool _posted;
    private bool _shown = true; // Standalone callers are active; view hosts report lifecycle.

    public void Request()
    {
        _pending = true;
        if (!_shown || _posted) return;
        _posted = true;
        dispatcher.Post(Drain);
    }

    public void Acknowledge() => _pending = false;

    public void OnShown()
    {
        _shown = true;
        if (!_pending) return;
        _pending = false;
        refresh();
    }

    public void OnHidden() => _shown = false;

    private void Drain()
    {
        _posted = false;
        if (!_shown || !_pending) return;
        _pending = false;
        refresh();
    }
}

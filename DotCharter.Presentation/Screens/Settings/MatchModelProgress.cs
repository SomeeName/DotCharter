using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The installer reports its later steps from a pool thread (after its awaits), and the
///     handler writes bound properties: off the UI thread they are posted. A step that lands
///     after the download returned is dropped, so the caller's own final write stays last.
/// </summary>
internal sealed class StepGate(IUiDispatcher? ui)
{
    private volatile bool _closed;

    public void Close() => _closed = true;

    public Action<string> Wrap(Action<string> onStep)
    {
        if (ui is null) return onStep;
        return step =>
        {
            if (ui.CheckAccess())
            {
                onStep(step);
                return;
            }

            ui.Post(() =>
            {
                if (!_closed) onStep(step);
            });
        };
    }
}

/// <summary>Reports on the calling thread, so the caller's own write always lands last.</summary>
internal sealed class SynchronousProgress(Action<string> handler) : IProgress<string>
{
    public void Report(string value) => handler(value);
}
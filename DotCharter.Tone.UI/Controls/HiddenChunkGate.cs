using Avalonia;
using Avalonia.Controls;

namespace DotCharter.Tone.UI.Controls;

/// <summary>
///     The state every <see cref="IChunkedPanel" /> shares: how many chunks have
///     been granted, and whether the panel has been SHOWN — after which it never
///     limits itself again.
/// </summary>
/// <remarks>
///     <para>
///         While the panel is not effectively visible and has never been shown, a
///         measure lays out only the first <see cref="Limit" /> children — one
///         chunk to begin with — and <see cref="Grant" /> raises that by one chunk.
///         The moment the panel becomes visible the limit is gone for good, and the
///         panel is invalidated so the pass that shows it measures it in full: its
///         last measure was a hidden, limited one that nothing else on the way down
///         is guaranteed to redo.
///     </para>
///     <para>
///         A chunk size of 0 or less turns the gate off: the panel lays out every
///         child, hidden or not, exactly as its base panel does.
///     </para>
/// </remarks>
internal sealed class HiddenChunkGate
{
    private readonly Panel _panel;
    private readonly Func<int> _chunkSize;
    private int _chunks = 1;
    private bool _shown;

    public HiddenChunkGate(Panel panel, Func<int> chunkSize)
    {
        _panel = panel;
        _chunkSize = chunkSize;
        _ = new EffectiveVisibilityWatcher(panel, OnEffectiveVisibilityChanged);
    }

    /// <summary>The panel limits its layout: chunking on, never shown, not visible now.</summary>
    public bool IsChunking => _chunkSize() > 0 && !_shown && !_panel.IsEffectivelyVisible;

    /// <summary>How many children a measure lays out; unbounded unless chunking.</summary>
    public int Limit => IsChunking ? _chunks * _chunkSize() : int.MaxValue;

    /// <summary>How many children the current measure lays out.</summary>
    public int Count => Math.Min(Limit, _panel.Children.Count);

    public bool HasBacklog => IsChunking && _panel.Children.Count > Limit;

    public bool Withholds(Control child) => IsChunking && _panel.Children.IndexOf(child) >= Limit;

    public bool Grant()
    {
        if (!HasBacklog) return false;
        _chunks++;
        _panel.InvalidateMeasure();
        return true;
    }

    /// <summary>The hidden arrange: only what was measured — arranging a child that
    /// was not would measure it here, all at once. The real positions come with
    /// the first show.</summary>
    public void ArrangeMeasured()
    {
        for (var i = 0; i < Count; i++)
            _panel.Children[i].Arrange(new Rect(_panel.Children[i].DesiredSize));
    }

    private void OnEffectiveVisibilityChanged(bool visible)
    {
        if (!visible || _shown) return;
        _shown = true;
        _panel.InvalidateMeasure();
        _panel.InvalidateArrange();
    }
}

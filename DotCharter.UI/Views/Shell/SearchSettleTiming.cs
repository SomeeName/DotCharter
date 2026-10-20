using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// What a search keystroke COSTS on the user's box: from the first keystroke of a burst
///     to the first idle tick after its coalesced filter pass, through
///     <see cref="SurfaceShowTiming" /> so it lands in the same log line as a tab or a dialog.
///     <para>
///         The pass is posted at <see cref="DispatcherPriority.Background" /> by the same keystroke
///         (<c>CoalescedFilterPass.Request</c>), before or after this hook — the binding's order is
///         not ours. So the record takes TWO Background hops: the first runs after the pass at the
///         latest, the second after the layout and render the pass caused. Keys typed while one
///         timing is pending join its burst. Without a sink nothing is recorded or posted.
///     </para>
/// </summary>
public sealed class SearchSettleTiming
{
    private readonly string _surface;
    private readonly ItemsControl _list;
    private readonly TextBox _box;
    private string _text = "";
    private long _burstStart;
    private int _keys;
    private bool _pending;

    public SearchSettleTiming(string surface, TextBox box, ItemsControl list)
    {
        _surface = surface;
        _list = list;
        _box = box;
        box.TextChanged += (_, _) => Keystroke();
    }

    private void Keystroke()
    {
        // The binding's first value (null to "") is not a keystroke, and runs no pass.
        var text = _box.Text ?? "";
        if (text == _text) return;
        _text = text;
        if (SurfaceShowTiming.Sink is null) return;
        _keys++;
        if (_pending) return;
        _pending = true;
        _burstStart = Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(() => Dispatcher.UIThread.Post(Record, DispatcherPriority.Background), DispatcherPriority.Background);
    }

    private void Record()
    {
        var keys = _keys;
        _keys = 0;
        _pending = false;
        SurfaceShowTiming.Record(Name(_surface, keys, _list.ItemCount), PopoverOpenTiming.MsSince(_burstStart));
    }

    /// <summary>"Library search (3 keys, 71 rows)" — the log line's surface name.</summary>
    public static string Name(string surface, int keys, int rows) =>
        $"{surface} search ({keys} {(keys == 1 ? "key" : "keys")}, {rows} rows)";
}

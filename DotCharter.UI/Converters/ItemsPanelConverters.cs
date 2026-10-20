using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     Picks a list's panel by how many rows it holds: stacked while it is short,
/// virtualized once it is long.
/// </summary>
/// <remarks>
///     <para>
///         A <see cref="VirtualizingStackPanel" /> realizes only the rows inside its
///         EFFECTIVE viewport, and a control behind <c>IsVisible=false</c> has none.
///         So a virtualized list on a hidden surface can never be prewarmed: every
///         prewarm and every background rewarm measures it to zero rows, and the
///         click that shows it builds them. Measured on the Inspector's Song tab
///         over a real chart (nine section names): ~160 ms of the click, after the
///         rest of the tab had been warmed.
///     </para>
///     <para>
///         A short list therefore stacks — all of its rows are built off the click
///         like any other content — and only a list long enough for realizing all
///         of it to cost more than the click keeps virtualizing.
///     </para>
/// </remarks>
public static class ItemsPanelConverters
{
    /// <summary>Up to this many rows a list stacks; past it, it virtualizes.</summary>
    public const int StackedRowLimit = 24;

    private static readonly ITemplate<Panel?> Stacked = new FuncTemplate<Panel?>(() => new StackPanel());
    private static readonly ITemplate<Panel?> Virtualized = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());

    /// <summary>Binds a list's <c>ItemsPanel</c> to its source's <c>Count</c>.</summary>
    public static readonly FuncValueConverter<int, ITemplate<Panel?>> StackedWhenShort = new(ForCount);

    public static ITemplate<Panel?> ForCount(int count) => count <= StackedRowLimit ? Stacked : Virtualized;
}

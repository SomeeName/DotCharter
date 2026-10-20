using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.Tone.UI;

/// <summary>
///     Maps a view model instance to its view by naming convention and
///     instantiates the view. Registered once as an application-scoped
///     <see cref="IDataTemplate"/> in <c>App.axaml</c> so every
///     <see cref="ContentControl"/> (including floated dock host windows, which
///     never see window-scoped templates) can resolve a VM to its view.
///
///     <para><b>View-model ↔ view registration convention</b> (the single rule
///     the whole UI relies on):</para>
///     <list type="bullet">
///       <item>A view model's full type name has <c>ViewModel</c> replaced with
///         <c>View</c> to name its view. Because the replace is textual and
///         global, it rewrites the <c>ViewModels</c> namespace segment to
///         <c>Views</c> too, e.g.
///         <c>DotCharter.UI.ViewModels.Editor.SheetMusicPaneViewModel</c> →
///         <c>DotCharter.UI.Views.Editor.SheetMusicPaneView</c>. So keep the VM
///         under a <c>ViewModels</c> sub-namespace and the view under the
///         mirrored <c>Views</c> sub-namespace with the same leaf name modulo
///         the <c>Model</c> suffix.</item>
///       <item>The VM MUST derive from <see cref="ViewModelBase"/> — that is what
///         <see cref="Match"/> keys on. A plain object is left for other
///         templates to handle.</item>
///       <item>The view MUST have a public parameterless constructor
///         (all XAML <c>UserControl</c>/<c>Window</c> views do).</item>
///       <item>For dockable panes, <c>Dockable.Context</c> must be the VIEW MODEL,
///         never a control instance — the dock host materializes the view through
///         this locator. A control in Context crashes on float/redock
///         ("already has a visual parent"). See <c>DotCharter.UI/AGENTS.md</c>.</item>
///     </list>
///
///     Resolution failures never throw: an unmatched or unconstructable view
///     yields a diagnostic <see cref="TextBlock"/> placeholder so a single
///     mis-registered pane degrades to a visible label instead of tearing down
///     the window.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    // VM type → resolved view type (or null when no matching view exists).
    // Build runs on every dockable materialization / ContentControl swap, so the
    // reflective name lookup is memoized. Concurrent because floated HostWindows
    // can build off their own threads.
    private static readonly ConcurrentDictionary<Type, Type?> ViewTypeCache = new();

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var viewType = ResolveViewType(param.GetType());
        if (viewType is null)
            return NotFound(param.GetType().FullName);

        try
        {
            return Activator.CreateInstance(viewType) as Control
                   ?? NotFound(viewType.FullName);
        }
        catch (Exception ex)
        {
            // Missing parameterless ctor, XAML load failure, etc. — surface it
            // as a placeholder rather than propagating out of a data template.
            return NotFound($"{viewType.FullName} ({ex.GetType().Name})");
        }
    }

    public bool Match(object? data) => data is ViewModelBase;

    /// <summary>
    ///     Resolves the view <see cref="Type"/> for a view-model type by the
    ///     naming convention, or <c>null</c> if none is found. Pure and cached —
    ///     exposed internally so the convention can be unit-tested without
    ///     spinning up Avalonia to instantiate a control.
    /// </summary>
    internal static Type? ResolveViewType(Type viewModelType)
    {
        return ViewTypeCache.GetOrAdd(viewModelType, static vm =>
        {
            var fullName = vm.FullName;
            if (string.IsNullOrEmpty(fullName) || !fullName.Contains("ViewModel", StringComparison.Ordinal))
                return null;

            var viewName = fullName.Replace("ViewModel", "View", StringComparison.Ordinal);
            // Look in the VM's own assembly first (correct even if this locator
            // ever moves), then fall back to the default probe.
            return vm.Assembly.GetType(viewName) ?? Type.GetType(viewName);
        });
    }

    private static TextBlock NotFound(string? name) => new() { Text = "Not Found: " + name };
}

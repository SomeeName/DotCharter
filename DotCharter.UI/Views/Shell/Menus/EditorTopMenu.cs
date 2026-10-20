using Avalonia.Controls;

namespace DotCharter.UI.Views.Shell.Menus;

/// <summary>
///     One top-level menu of the editor's bar (<see cref="MainMenuView" />): the
/// header and every row under it, in its own <c>.axaml</c>. The bar
///     held all eight menus in one file until it reached its size budget.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="StyleKeyOverride" /> is load-bearing.</b> Type selectors
///         match on the STYLE key, so a menu keyed as its own class still gets a
///         template but does not match <c>MenuItem.FireMenuTop</c> or
///         <c>ShellChromeStyles</c>' <c>MenuItem</c> rules: measured, every
///         header lost its padding, type size and colour. Keyed as
///         <see cref="MenuItem" />, every rule applies exactly as it does to the
///         inline items (<c>Perf/MainMenuDropdownUiPerfSmokeTests</c> checks the
///         header's padding).
///     </para>
///     <para>
///         Each menu file is its own namescope. None of them declares an
///         <c>x:Name</c>; the bar finds its menus through
///         <see cref="MainMenuView.TopLevelMenus" />, never by name.
///     </para>
/// </remarks>
public abstract class EditorTopMenu : MenuItem
{
    protected override Type StyleKeyOverride => typeof(MenuItem);
}

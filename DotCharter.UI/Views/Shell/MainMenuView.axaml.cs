using Avalonia.Controls;

namespace DotCharter.UI.Views.Shell;

public partial class MainMenuView : UserControl
{
    public MainMenuView() => InitializeComponent();

    /// <summary>
    ///     The bar's top-level headers, in declaration order — what
    ///     <see cref="MenuFlyoutPrewarmer" /> drains after the first frame so no
    ///     dropdown pays its whole realization on the user's first click.
    /// </summary>
    /// <remarks>
    ///     Empty when the bar has not been built yet, which is a no-op for the
    ///     drain rather than an error.
    /// </remarks>
    public IReadOnlyList<MenuItem> TopLevelMenus() =>
        this.FindControl<EditorMenu>("EditorMenuBar") is { } menu
            ? [.. menu.Items.OfType<MenuItem>()]
            : [];
}

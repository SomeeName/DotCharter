using DotCharter.Services.Infrastructure.Features;

namespace DotCharter.UI.Views.Shell.Menus;

/// <summary>The editor bar's File menu (see <see cref="EditorTopMenu" />).</summary>
public partial class FileMenu : EditorTopMenu
{
    public FileMenu()
    {
        InitializeComponent();
        ContestedMenu.Prune(this);
    }

    /// <summary>Package-reading rows go once the shell's view model says what the build reads.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ViewModels.Shell.MainWindowViewModel vm)
            ContestedMenu.Prune(this, ContestedFeatures.Enabled, vm.Project.PackageFeatures);
    }
}

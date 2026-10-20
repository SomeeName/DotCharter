using Avalonia.Controls;

namespace DotCharter.RigLab;

public sealed partial class RigLabWindow : Window
{
    public RigLabWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // The ASIO control panel parents to this window.
        if (DataContext is RigLabShellViewModel shell)
            shell.Audio.WindowHandle = () => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }
}

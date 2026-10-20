using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DotCharter.UI.Views.Editor;

public partial class StemMixerView : UserControl
{
    public StemMixerView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

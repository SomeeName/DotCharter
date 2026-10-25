using DotCharter.Presentation.Contracts.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The editor's panes: singletons, because the editor has one inspector and one chat pane.</summary>
public static class EditorModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ShapesPanelParts>();
        services.AddSingleton<IShapePanelScreen, ShapesPanelViewModel>();
        services.AddSingleton<AiChatServices>();
        services.AddSingleton<AiChatViewModel>();
        // the Issues tab reaches the chat lazily, so building the inspector does not build the chat pane.
        services.AddSingleton<Func<IAiChatFindingsDoor?>>(sp => () => sp.GetService<AiChatViewModel>());
    }
}

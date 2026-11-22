using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.State;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The editor's panes: singletons, because the editor has one inspector and one chat pane.</summary>
public static class EditorModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ShapesPanelParts>(sp => new ShapesPanelParts(sp.GetRequiredService<IChordShapePlacementService>(),
            sp.GetRequiredService<IEditorTransport>(), sp.GetRequiredService<IDialogs>())
        {
            Naming = arrangement => ChordNamingOptions.For(sp.GetRequiredService<IConfigurationService>().LoadAppConfig().ChordNamingStyle,
                arrangement, sp.GetRequiredService<ProjectState>().ActiveProject?.Chart)
        });
        services.AddSingleton<IShapePanelScreen, ShapesPanelViewModel>();
        services.AddSingleton<AiChatServices>();
        services.AddSingleton<AiChatViewModel>();
        // the Issues tab reaches the chat lazily, so building the inspector does not build the chat pane.
        services.AddSingleton<Func<IAiChatFindingsDoor?>>(sp => () => sp.GetService<AiChatViewModel>());
    }
}

using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>The chord panel with the app's dialog host for its Edit shape door, and the timeline marks the Edit shape dialog sets.</summary>
internal static class ChordShapeEditingModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IChordUseMarks, ChordUseMarks>();
        services.AddSingleton<ChordTemplateEditorViewModel>(sp =>
        {
            var panel = ActivatorUtilities.CreateInstance<ChordTemplateEditorViewModel>(sp);
            panel.ShapeEditor.Dialogs = sp.GetRequiredService<IDialogs>();
            return panel;
        });
    }
}

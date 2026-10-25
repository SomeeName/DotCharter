using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     The chord panel's door to the Edit shape dialog: the shape on screen, opened from the chord the
///     panel shows, with the scope chosen inside the dialog before anything is applied. The dialog
///     host is set once by the composition root.
/// </summary>
public sealed class ChordShapeEditOpener(IChartEditorSurface editor, Func<ChordTemplate?> shape, Func<Chord?> origin)
{
    private IAsyncRelayCommand? _open;

    /// <summary>The app's dialog host; null until the composition root sets it, which disables the command.</summary>
    internal IDialogs? Dialogs { get; set; }

    /// <summary>The shown shape is one of the active arrangement's, so it can be edited as a shape.</summary>
    public bool CanOpen => Dialogs is not null && shape() is { } template
                           && editor.ActiveArrangement is { } arrangement
                           && ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, template);

    /// <summary>Opens the dialog on the shown shape.</summary>
    public ICommand OpenCommand => _open ??= new AsyncRelayCommand(OpenAsync);

    private async Task OpenAsync()
    {
        if (!CanOpen) return;
        await Dialogs!.ShowAsync(new ShapeEditRequest(shape()!, origin()));
    }
}

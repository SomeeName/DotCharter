using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.UI.Input;

/// <summary>Opens the fret-hand position editor with the pointer's captured row target.</summary>
public interface IFhpEditLauncher
{
    void EditFhpInPlace(object? viewport, FhpRequest request);
}

using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Tones;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The designer's estimate-from-audio run and the dialogs it opens over itself (the AI tone creator), as one part.</summary>
public sealed class ToneDesignerTools(ToneGenerationRun estimate, IDialogs dialogs)
{
    public ToneGenerationRun Estimate { get; } = estimate;

    public IDialogs Dialogs { get; } = dialogs;
}

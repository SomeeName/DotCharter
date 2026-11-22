
namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     What the chord-template collaborators need of the panel: the shown shape, the capo, the
///     per-string rows and the one write path (<c>ApplyTemplateEdit</c>). A narrow seam, so the
///     collaborators never hold the view model itself.
/// </summary>
internal interface IChordEditSurface
{
    ChordTemplate? Template { get; }

    int Capo { get; }

    bool IsUpdatingUi { get; set; }

    IReadOnlyList<StringFingeringInput> StringInputs { get; }

    /// <summary>True after an accepted edit; false leaves the caller's draft pending.</summary>
    bool ApplyTemplateEdit(ChordTemplate newState, ChordTemplateEditKind? kind = null);
}

/// <summary>The finger-draft half of the panel's state, on top of <see cref="IChordEditSurface" />.</summary>
internal interface IChordFingerSurface : IChordEditSurface
{
    bool HasPendingFingerEdits { get; set; }

    bool CanSaveFingering { get; }

    ChordTemplate? DraftTemplate { set; }
}

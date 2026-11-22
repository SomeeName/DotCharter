using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>One section row of the fit-to-recording result, ticked when its confidence suggests it. A section
///     without notes has nothing to be judged on: it carries its reason instead of a score and cannot be ticked.</summary>
public sealed class AlignSectionRow : ObservableObject, IAlignSectionRow
{
    private bool _isApplied;

    public AlignSectionRow(RecordingAlignmentSectionResult section, string title, ILocalizationService loc)
    {
        Index = section.Index;
        Title = title;
        CanApply = section.HasNotes && section.FreeBeats > 0;
        var measured = CanApply && double.IsFinite(section.Confidence);
        Confidence = measured ? section.Confidence : 0;
        ConfidenceText = measured ? PercentText.Format(section.Confidence, 0) : "";
        IsLow = CanApply && !section.SuggestApply;
        ShiftText = CanApply
            ? loc.Format("Dialog_AlignToRecording_Shift", Math.Round(section.MeanShiftMs).ToString("0", System.Globalization.CultureInfo.CurrentCulture))
            : "";
        SkipReason = section.HasNotes ? "" : loc["Dialog_AlignToRecording_NoNotes"];
        _isApplied = CanApply && section.SuggestApply;
    }

    public int Index { get; }

    public string Title { get; }

    public double Confidence { get; }

    public string ConfidenceText { get; }

    public bool IsLow { get; }

    public string ShiftText { get; }

    public bool IsApplied
    {
        get => _isApplied;
        set => SetProperty(ref _isApplied, value && CanApply);
    }

    public bool CanApply { get; }

    public string SkipReason { get; }
}

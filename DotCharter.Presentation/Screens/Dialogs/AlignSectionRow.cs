using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>One section row of the align-to-recording result, ticked when its confidence suggests it.</summary>
public sealed class AlignSectionRow : ObservableObject, IAlignSectionRow
{
    private bool _isApplied;

    public AlignSectionRow(RecordingAlignmentSectionResult section, string title, ILocalizationService loc)
    {
        Index = section.Index;
        Title = title;
        var measured = double.IsFinite(section.Confidence);
        Confidence = measured ? section.Confidence : 0;
        ConfidenceText = measured ? PercentText.Format(section.Confidence, 0) : loc["Dialog_AlignToRecording_NoNotes"];
        IsLow = !section.SuggestApply;
        ShiftText = loc.Format("Dialog_AlignToRecording_Shift", Math.Round(section.MeanShiftMs).ToString("0", System.Globalization.CultureInfo.CurrentCulture));
        CanApply = section.FreeBeats > 0;
        _isApplied = section.SuggestApply;
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
}

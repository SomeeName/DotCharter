using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     The Tone designer: the project's own Rocksmith tone definitions (a list, and an
///     editor over the selected one), each arrangement's Base / A–D tone slots, and
///     tones imported from another package. Edits are buffered and land as ONE undo
///     step per tone; slot assignments land when the dialog closes. Preview routes the
///     edited tone through the live rig and puts the player's own rig back after.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once closed through Close.
/// </summary>
public interface IToneDesignerScreen : IDialogScreen<bool>
{
    IReadOnlyList<IProjectTone> ProjectTones { get; }

    bool HasProjectTones { get; }

    IProjectTone? SelectedTone { get; set; }

    bool HasSelectedTone { get; }

    /// <summary>The selected tone's editor; its fields mean nothing while <see cref="HasSelectedTone" /> is false.</summary>
    IToneEditor Editor { get; }

    /// <summary>Why Preview, Duplicate and Delete are greyed; empty while all three are live.</summary>
    string ToneActionUnavailableReason { get; }

    bool HasToneActionUnavailableReason { get; }

    /// <summary>A native rig AND a selected tone.</summary>
    bool CanPreviewNow { get; }

    bool IsPreviewOn { get; set; }

    /// <summary>Where Preview is heard: the DotCharter rig (default) or Rocksmith through tonelab.</summary>
    IToneDesignerPreviewTargets PreviewTargets { get; }

    IReadOnlyList<IToneArrangementRow> Arrangements { get; }

    bool HasArrangements { get; }

    /// <summary>The project's tone keys, suggested in every slot box.</summary>
    IReadOnlyList<string> AuthoredToneKeys { get; }

    IReadOnlyList<IImportedTone> ImportedTones { get; }

    bool HasImportedTones { get; }

    /// <summary>What the last import, add or generate did.</summary>
    string ImportStatus { get; }

    ICommand AddToneCommand { get; }

    ICommand DuplicateToneCommand { get; }

    ICommand DeleteToneCommand { get; }

    /// <summary>One tone per PLACED tone change on the active arrangement, from the audio it governs.</summary>
    ICommand GenerateTonesFromAudioCommand { get; }

    ICommand ImportTonesFromPsarcCommand { get; }

    /// <summary>Reads a DLC Builder tone file (.json, .xml or a .rs2dlc project) straight into the project's tones.</summary>
    ICommand ImportToneFileCommand { get; }

    /// <summary>Opens the AI tone creator (<see cref="IToneCreatorScreen" />) over the selected tone; what it accepts lands as ONE undo step.</summary>
    ICommand CreateWithAiCommand { get; }

    /// <summary>Whether this build reads game packages; when false the import button is ABSENT.</summary>
    bool CanImportFromPackages { get; }

    /// <summary>Copies an imported tone (the parameter, an <see cref="IImportedTone" />) into the project.</summary>
    ICommand AddImportedToProjectCommand { get; }

    /// <summary>Opens <see cref="RigImport" />: the closest Rocksmith tone for the rig being played, every loss named.</summary>
    ICommand ImportFromToneRigCommand { get; }

    /// <summary>The open "from tone rig" review over the dialog, or null while none is open.</summary>
    IRigToneImportReview? RigImport { get; }

    /// <summary>Commits the pending edit and the slot assignments, stops a preview, and closes.</summary>
    ICommand CloseCommand { get; }
}

/// <summary>The selected tone's fields, gear and knobs.</summary>
public interface IToneEditor : INotifyPropertyChanged
{
    /// <summary>An invalid or duplicate key is flagged and never committed.</summary>
    string EditKey { get; set; }

    string EditName { get; set; }

    string KeyError { get; }

    bool HasKeyError { get; }

    /// <summary>The Volume as DLC Builder shows it: the attenuation as a positive number (12.0 for -12 dB; the stored value is the negative dB). Setting clamps to the range.</summary>
    double VolumeAttenuation { get; set; }

    double VolumeAttenuationMin { get; }

    double VolumeAttenuationMax { get; }

    /// <summary>"None" first, then every descriptor Rocksmith knows.</summary>
    IReadOnlyList<IToneChoice> DescriptorOptions { get; }

    IToneChoice Descriptor1 { get; set; }

    IToneChoice Descriptor2 { get; set; }

    IToneChoice Descriptor3 { get; set; }

    /// <summary>Amp, Cabinet, Pre 1–4, Loop 1–4, Rack 1–4.</summary>
    IReadOnlyList<IToneGearSlot> GearSlots { get; }

    IToneGearSlot? SelectedGearSlot { get; set; }
}

/// <summary>
/// The Preview target switch. Rocksmith is selectable only while a tonelab session is
/// ready; otherwise it is shown disabled with <see cref="InGameUnavailableReason" />.
/// <see cref="Status" /> is the last in-game send's result (an error, or the added latency).
/// </summary>
public interface IToneDesignerPreviewTargets : INotifyPropertyChanged
{
    bool IsClone { get; set; }

    /// <summary>Refused (stays false) while <see cref="CanUseInGame" /> is false.</summary>
    bool IsInGame { get; set; }

    bool CanUseInGame { get; }

    string InGameUnavailableReason { get; }

    bool HasInGameUnavailableReason { get; }

    /// <summary>Looks for a running Rocksmith with tonelab again (the newest when there are several).</summary>
    System.Windows.Input.ICommand ReconnectCommand { get; }

    /// <summary>A launcher is present (an optional plugin): <see cref="LaunchCommand" /> starts Rocksmith with tonelab.</summary>
    bool CanLaunch { get; }

    System.Windows.Input.ICommand LaunchCommand { get; }

    /// <summary>While Rocksmith is chosen and DotCharter is on ASIO: the game needs the device, so say what to do.</summary>
    string AsioNote { get; }

    bool HasAsioNote { get; }

    string Status { get; }

    bool HasStatus { get; }
}

public interface IToneChoice
{
    string DisplayName { get; }
}

/// <summary>A choice a type-to-filter picker can also find by something other than its name (a gear's catalogue key).
/// A control capability, not a view binding: the picker reads it, no XAML binds it.</summary>
public interface ISearchableChoice : IToneChoice
{
    string SearchKey { get; }
}

public interface IProjectTone : INotifyPropertyChanged
{
    string Key { get; }

    string Name { get; }
}

public interface IImportedTone
{
    string Key { get; }

    string Name { get; }
}

/// <summary>One instrumental arrangement's tone slots, committed when the dialog closes. Each
/// <c>Is…Undefined</c> marks its slot in place: it names a key the project has no tone for, so the
/// game plays its default tone there.</summary>
public interface IToneArrangementRow : INotifyPropertyChanged
{
    string Name { get; }

    string TypeLabel { get; }

    string ToneBase { get; set; }

    string ToneA { get; set; }

    string ToneB { get; set; }

    string ToneC { get; set; }

    string ToneD { get; set; }

    bool IsToneBaseUndefined { get; }

    bool IsToneAUndefined { get; }

    bool IsToneBUndefined { get; }

    bool IsToneCUndefined { get; }

    bool IsToneDUndefined { get; }
}

/// <summary>One gear slot: the gear family's picker and the knobs of the gear picked.</summary>
public interface IToneGearSlot : INotifyPropertyChanged
{
    string Label { get; }

    IReadOnlyList<IToneChoice> Options { get; }

    IToneChoice SelectedOption { get; set; }

    bool HasGear { get; }

    string GearDisplayName { get; }

    IReadOnlyList<IToneKnob> Knobs { get; }

    /// <summary>Gear with no catalogue knobs: its values are carried verbatim.</summary>
    bool ShowNoKnobsHint { get; }
}

/// <summary>One knob: a slider, or a picker for an enum knob.</summary>
public interface IToneKnob : INotifyPropertyChanged
{
    string DisplayName { get; }

    double Minimum { get; }

    double Maximum { get; }

    double Step { get; }

    bool IsEnum { get; }

    IReadOnlyList<string> EnumOptions { get; }

    double Value { get; set; }

    int EnumIndex { get; set; }

    /// <summary>"62%", "-3.5 dB", "1.2 kHz", or the enum's label.</summary>
    string ValueText { get; }
}

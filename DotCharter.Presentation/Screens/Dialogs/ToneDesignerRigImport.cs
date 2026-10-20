using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Services.Editing.Tones;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>What the rig import needs from the designer that owns it.</summary>
internal sealed record ToneDesignerRigImportHost(
    Func<ProjectToneItem?> Selected,
    Func<IEnumerable<string>> Keys,
    Func<Arrangement?> Arrangement,
    Action CommitPending,
    Action<string> Written,
    Action<string> Status);

/// <summary>
///     The Tone designer's "From tone rig…": snapshots the player's rig (their own, even while
///     Preview holds the chain) and opens the review. Accept writes the tone as ONE undo step.
/// </summary>
internal sealed class ToneDesignerRigImport(ChartEditDialogServices edit, TonePreviewTargets preview, ChartProject? project,
    ToneDesignerRigImportHost host)
{
    private RelayCommand? _command;

    public event EventHandler? ReviewChanged;

    public ICommand Command => _command ??= new RelayCommand(Open);

    public RigToneImportReviewViewModel? Review { get; private set; }

    public Func<Func<RigToRsToneResult>, Task<RigToRsToneResult>> Runner { get; set; } = work => Task.Run(work);

    private void Open()
    {
        if (project is null) return;
        var clone = preview.Clone;
        if (!clone.IsAvailable)
        {
            host.Status(edit.Localization["Dialog_Tones_RigImportUnavailable"]);
            return;
        }

        host.CommitPending();
        var target = host.Selected();
        var key = RsToneAuthoring.MakeUniqueKey(RigToneImport.NewKeyStem, host.Keys());
        Set(new RigToneImportReviewViewModel(edit.Localization,
            new RigToneImportSource(clone.CurrentRig(), key, target?.Name, clone.ModuleName), Runner, (tone, replace) => Accept(tone, replace ? target : null), () => Set(null)));
    }

    private void Accept(RsTone tone, ProjectToneItem? replace)
    {
        if (project is null) return;
        var loc = edit.Localization;
        try
        {
            var definition = RigToneImport.Apply(project, replace is null ? host.Arrangement() : null, edit.History, tone, replace?.Key);
            Set(null);
            host.Written(definition.Key);
            host.Status(loc.Format(replace is null ? "Dialog_Tones_RigImportAdded" : "Dialog_Tones_RigImportReplaced", definition.Key));
            edit.Logger.Info(AppLogCategory.Project, $"Tone designer wrote '{definition.Key}' from the tone rig (replace={replace is not null}).");
        }
        catch (DotCharter.Services.Ai.Generate.RsToneCatalogException ex)
        {
            edit.Logger.Warning(AppLogCategory.Project, $"Tone from the rig refused by the catalogue gate: {ex.Message}");
            host.Status(loc.Format("Dialog_Tones_ImportFailed", ex.Message));
        }
    }

    private void Set(RigToneImportReviewViewModel? review)
    {
        Review = review;
        ReviewChanged?.Invoke(this, EventArgs.Empty);
    }
}

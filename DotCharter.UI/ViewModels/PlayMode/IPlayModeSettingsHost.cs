namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     What the Play Mode settings modal (<see cref="PlayModeSettingsViewModel" />)
/// needs from the Play Mode that hosts it — the run's own state, read
///     through here rather than copied, so there is never a second
///     <c>_runSideEffectsApplied</c> or a second practice speed to drift.
///     <c>PlayModeViewModel</c> implements it and attaches itself with
///     <see cref="PlayModeSettingsViewModel.AttachHost" />.
/// </summary>
public interface IPlayModeSettingsHost
{
    /// <summary>True while a run's side effects are live (the run chain's
    /// <c>_runSideEffectsApplied</c>). Gates the mid-run re-arm on close: outside a
    /// run the transport's gameplay toggles are not the modal's to touch.</summary>
    bool RunSideEffectsApplied { get; }

    /// <summary>The player's practice speed, which the modal's Gameplay section
    /// binds through Play Mode and re-applies to a run already going.</summary>
    int PracticeSpeedPercent { get; }

    /// <summary>The two Play-Mode-owned rows the Gameplay section's reset puts
    /// back; each setter persists itself.</summary>
    bool UseInGameTones { get; set; }

    /// <inheritdoc cref="UseInGameTones" />
    bool ShowGameplayToneHud { get; set; }

    /// <summary>The capture peak (0..1) of the guitar input — the ONE tap the
    /// menu backdrop, Get Ready and this modal's meter all read.</summary>
    float ReadBackdropInputPeak();
}

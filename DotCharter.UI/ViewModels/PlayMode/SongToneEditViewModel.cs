using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Audio.Dsp.Tones;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>One of the song's tones on the Get Ready screen: its name as the chart
/// names it, and whether it plays the player's saved edit.</summary>
/// <param name="Name">The tone's name as the chart names it.</param>
/// <param name="IsEdited">The tone plays the player's saved edit.</param>
/// <param name="AccessibleName">What a screen reader announces for the chip's edit button,
/// resolved by the screen that builds the chip.</param>
public sealed record SongToneChip(string Name, bool IsEdited, string AccessibleName)
{
    public override string ToString() => Name;
}

/// <summary>
///     Editing one of a song's in-game tones on the rig and saving it for that
///     song — the bar over the rig while a song tone is being edited.
///     <para>
///         <b>The rig itself knows nothing about this.</b> The coordinator puts the
///         tone on the rig as a third driver (<see cref="RsToneDriver.SongToneEdit" />,
///         which snapshots and restores the player's own board), the rig is the
///         ordinary rig, and this bar is the only part that knows there is a song:
///         Save captures what the rig now holds and stores it for the song
///         (<see cref="SongToneEditStore" />, DotCharter's data — the song's
///         package is never written), Reset drops the edit so the tone converts
///         from its gear again, and Discard (or closing the rig any other way,
///         through <see cref="End" />) gives the player's board back without
///         storing anything.
///     </para>
/// </summary>
public sealed partial class SongToneEditViewModel : ObservableObject
{
    private readonly RsTonePlaybackCoordinator _coordinator;
    private readonly ILocalizationService _localization;
    private readonly Func<DcRigPreset?> _captureRig;
    private readonly Func<string?> _songIdentity;
    private readonly Func<Task> _refreshTones;
    private readonly Action _closeRig;

    /// <summary>Test seams: the store writes. Default to <see cref="SongToneEditStore.Default" />.</summary>
    internal Func<string, string, string, DcRigPreset, SongToneEditWriteResult> SaveEdit { get; set; } =
        (song, key, name, rig) => SongToneEditStore.Default.Save(song, key, name, rig);

    /// <inheritdoc cref="SaveEdit" />
    internal Func<string, string, SongToneEditWriteResult> RemoveEdit { get; set; } =
        (song, key) => SongToneEditStore.Default.Remove(song, key);

    /// <param name="coordinator">Owns the rig while the tone is edited.</param>
    /// <param name="captureRig">The rig as it stands now (the pedalboard's snapshot).</param>
    /// <param name="songIdentity">The loaded song's identity (<c>PlaySongItem.SongIdentity</c>).</param>
    /// <param name="refreshTones">Re-prepares the song's tones after its edits changed.</param>
    /// <param name="closeRig">Closes the rig overlay.</param>
    /// <param name="localization">The bar's status lines.</param>
    public SongToneEditViewModel(
        RsTonePlaybackCoordinator coordinator,
        Func<DcRigPreset?> captureRig,
        Func<string?> songIdentity,
        Func<Task> refreshTones,
        Action closeRig,
        ILocalizationService localization)
    {
        _coordinator = coordinator;
        _localization = localization;
        _captureRig = captureRig;
        _songIdentity = songIdentity;
        _refreshTones = refreshTones;
        _closeRig = closeRig;
    }

    /// <summary>A song tone is on the rig — the bar is showing.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>The tone being edited, as the chart names it.</summary>
    [ObservableProperty] private string _toneName = "";

    /// <summary>The tone plays a saved edit — Reset has something to undo.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool _hasEdit;

    /// <summary>A write is running; every action waits for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ResetCommand), nameof(DiscardCommand))]
    private bool _isBusy;

    /// <summary>The last action's outcome, in words; empty when there is none.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>The status reports a failure.</summary>
    [ObservableProperty] private bool _isStatusError;

    /// <summary>
    ///     Puts <paramref name="tone" /> on the rig for editing. False — and the bar
    ///     stays hidden — when the coordinator refused it (a run owns the rig, the
    ///     tones are still converting, no native rig, an own chart).
    /// </summary>
    public bool Begin(string tone, bool isEdited)
    {
        if (IsBusy || !_coordinator.BeginToneEdit(tone)) return false;
        ToneName = tone;
        HasEdit = isEdited;
        SetStatus("", false);
        IsActive = true;
        return true;
    }

    /// <summary>Gives the player's board back without storing anything. Called when
    /// the rig closes by ANY route (✕, Esc, Discard). Idempotent.</summary>
    public void End()
    {
        _coordinator.EndToneEdit();
        if (!IsActive) return;
        IsActive = false;
        SetStatus("", false);
    }

    private bool CanAct() => !IsBusy;

    private bool CanReset() => !IsBusy && HasEdit;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveAsync()
    {
        if (!IsActive) return;
        var loc = _localization;
        if (_songIdentity() is not { Length: > 0 } song)
        {
            SetStatus(loc.Format("PlayMode_SongToneEdit_Failed", "—"), true);
            return;
        }

        if (_captureRig() is not { Modules.Count: > 0 } rig)
        {
            SetStatus(loc["PlayMode_SongToneEdit_EmptyRig"], true);
            return;
        }

        var tone = ToneName;
        var key = _coordinator.ResolvePackageToneKey(tone) ?? tone.Trim();
        var result = await WriteAsync(() => SaveEdit(song, key, tone, rig));
        if (result.Outcome != SongToneEditWriteOutcome.Written)
        {
            ReportFailure(result);
            return;
        }

        HasEdit = true;
        SetStatus(loc["PlayMode_SongToneEdit_Saved"], false);
        await _refreshTones();
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        if (!IsActive || _songIdentity() is not { Length: > 0 } song) return;

        var tone = ToneName;
        var key = _coordinator.ResolvePackageToneKey(tone) ?? tone.Trim();
        var result = await WriteAsync(() => RemoveEdit(song, key));
        if (!result.Ok)
        {
            ReportFailure(result);
            return;
        }

        HasEdit = false;
        await _refreshTones();
        // The re-prepare published the CONVERSION again; put it on the rig.
        _coordinator.ReapplyEditedTone();
        SetStatus(_localization["PlayMode_SongToneEdit_ResetDone"], false);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Discard()
    {
        End();
        _closeRig();
    }

    private async Task<SongToneEditWriteResult> WriteAsync(Func<SongToneEditWriteResult> write)
    {
        IsBusy = true;
        SetStatus(_localization["PlayMode_SongToneEdit_Saving"], false);
        try
        {
            // File I/O never runs on the UI thread (UiThreadIoDeferralTests).
            return await Task.Run(write);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportFailure(SongToneEditWriteResult result) =>
        SetStatus(_localization.Format("PlayMode_SongToneEdit_Failed", result.Detail ?? "—"), true);

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        IsStatusError = isError;
    }
}

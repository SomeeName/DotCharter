// PlayModeViewModel: the per-song tone preference (in-game tones / my tone / none) and the Rocksmith tone conversion state the pre-play screens print.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Rendering.Data;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The per-song TONE preference and the in-game tone state: which of the
///     three sources a run uses, the saved user presets in the picker, and the
///     Rocksmith tone conversion summary the pre-play and gameplay surfaces
///     print (including its approximations).
///     <para>
///         Part of <see cref="PlayModeViewModel" />. Injected dependencies and
///         plain private fields live in the ROOT file; see
///         <c>PlayModeViewModel.cs</c> for the full field rule and
///         why the split exists.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    // ---- In-game tones (0.16 RS-tone feature, Phase 3) -----------------------

    /// <summary>App-wide persisted toggle: rebuild the loaded song's Rocksmith
    /// tones on the in-house rig and switch them live during the run, instead of
    /// the manual pedalboard. Off by default; no effect on songs without tones or
    /// builds without the native DSP.</summary>
    [ObservableProperty] private bool _useInGameTones;

    partial void OnUseInGameTonesChanged(bool value)
    {
        var config = _configService.LoadAppConfig();
        config.UseInGameTones = value;
        _configService.SaveAppConfig(config);

        // The toggle is LIVE mid-run (flipped from the gameplay-settings overlay
        // while Playing/Paused): ON takes over the rig at the current playhead,
        // OFF restores the player's manual chain. Both coordinator calls are
        // idempotent. Order matters twice: the config save above must come
        // first (BeginRun re-reads AppConfig.UseInGameTones), and BeginRun must
        // run BEFORE RefreshInGameToneState below — the refresh starts a new
        // async prepare, and BeginRun skips while one is in flight.
        if (CurrentScreen is PlayModeScreen.Playing or PlayModeScreen.Paused)
        {
            if (value && HasInGameTones) _rsToneCoordinator?.BeginRun();
            else if (!value) _rsToneCoordinator?.EndRun();
        }

        RefreshInGameToneState(); // re-evaluate the badge for the loaded song
    }

    /// <summary>Show the active tone name in the gameplay HUD + flash it on a
    /// switch (the 3D highway corner label). On by default; independent of
    /// <see cref="UseInGameTones"/>. Persisted immediately and mirrored to the
    /// render channel so a flip live-applies.</summary>
    [ObservableProperty] private bool _showGameplayToneHud = true;

    partial void OnShowGameplayToneHudChanged(bool value)
    {
        GameplayToneHud.Enabled = value;
        var config = _configService.LoadAppConfig();
        config.ShowGameplayToneHud = value;
        _configService.SaveAppConfig(config);
    }

    /// <summary>True when the loaded arrangement exposes ≥1 parseable gear tone —
    /// gates the whole in-game-tones card (hidden for own charts / RS1 imports).</summary>
    [ObservableProperty] private bool _hasInGameTones;

    /// <summary>False when the build has no native DSP rig (NullDspChainService).</summary>
    [ObservableProperty] private bool _inGameTonesDspAvailable = true;

    /// <summary>"3 tones rebuilt on your rig" — shown when the toggle is on.</summary>
    [ObservableProperty] private string _inGameToneSummary = "";

    /// <summary>At least one effect couldn't be reproduced 1:1 (informational).</summary>
    [ObservableProperty] private bool _hasToneApproximations;

    /// <summary>"2 effects approximated" — the non-blocking approximated note.</summary>
    [ObservableProperty] private string _inGameToneApproxNote = "";

    /// <summary>Multi-line tooltip detail (the converter's plain-text notes).</summary>
    [ObservableProperty] private string _inGameToneApproxDetail = "";

    /// <summary>True while the song's tones are still converting off-thread —
    /// shown as a small 'Tones loading…' badge on the pre-play card. While it
    /// is set, the coordinator's <c>BeginRun</c> skips driving (starting the
    /// run this instant would fall back to the manual rig), so the skip is
    /// visible instead of silent (plan 2.5).</summary>
    [ObservableProperty] private bool _inGameTonesLoading;

    // ---- Per-song tone preference (2.4) ---------------------------------------
    // A small three-state control on the pre-play tones card: 0 = Global
    // setting (default, no entry persisted), 1 = In-game tones (force driving
    // even with the global toggle off), 2 = My tone (+ user-preset picker).
    // Persisted in AppConfig.SongTonePreferences keyed by PlaySongItem.SongIdentity
    // (the practice-profile song key); RsTonePlaybackCoordinator.BeginRun
    // consults the same entry.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMyToneSelected))]
    private int _songTonePreferenceIndex;

    /// <summary>Shows the user-preset picker next to the three-state control.</summary>
    public bool IsMyToneSelected => SongTonePreferenceIndex == 2;

    /// <summary>The player's saved tone profiles (user presets only).</summary>
    public ObservableCollection<MyTonePresetOption> MyTonePresets { get; } = [];

    [ObservableProperty] private MyTonePresetOption? _selectedMyTonePreset;

    /// <summary>True while the per-song preference is being LOADED for a song —
    /// loads must not re-persist (same pattern as PlayModeSettingsViewModel._loadingGameplaySettings).</summary>
    private bool _loadingTonePreference;

    partial void OnSongTonePreferenceIndexChanged(int value) => PersistSongTonePreference();

    partial void OnSelectedMyTonePresetChanged(MyTonePresetOption? value) => PersistSongTonePreference();

    /// <summary>Loads the flow song's persisted tone preference into the pre-play
    /// controls (and refreshes the user-preset list). Called from PreparePrePlay.</summary>
    private void LoadSongTonePreference()
    {
        _loadingTonePreference = true;
        try
        {
            RefreshMyTonePresets();

            SongTonePreference? pref = null;
            var key = _flowSong?.SongIdentity;
            if (!string.IsNullOrEmpty(key))
                _configService.LoadAppConfig().SongTonePreferences.TryGetValue(key!, out pref);

            SongTonePreferenceIndex = pref?.Mode switch
            {
                SongTonePreference.InGameMode => 1,
                SongTonePreference.MyToneMode => 2,
                _ => 0
            };
            SelectedMyTonePreset =
                MyTonePresets.FirstOrDefault(p => string.Equals(p.Id, pref?.PresetId, StringComparison.OrdinalIgnoreCase))
                ?? MyTonePresets.FirstOrDefault();
        }
        finally
        {
            _loadingTonePreference = false;
        }
    }

    private void RefreshMyTonePresets()
    {
        MyTonePresets.Clear();
        foreach (var preset in _tonePresets.UserPresets)
            MyTonePresets.Add(new MyTonePresetOption(preset.Id, preset.Name));
    }

    private void PersistSongTonePreference()
    {
        if (_loadingTonePreference) return;
        var key = _flowSong?.SongIdentity;
        if (string.IsNullOrEmpty(key)) return;

        var config = _configService.LoadAppConfig();
        if (SongTonePreferenceIndex == 0)
        {
            // 'Global setting' is the default — keep the config free of no-ops.
            if (!config.SongTonePreferences.Remove(key!)) return;
        }
        else
        {
            config.SongTonePreferences[key!] = new SongTonePreference
            {
                Mode = SongTonePreferenceIndex == 1 ? SongTonePreference.InGameMode : SongTonePreference.MyToneMode,
                PresetId = SongTonePreferenceIndex == 2 ? SelectedMyTonePreset?.Id ?? "" : ""
            };
        }

        _configService.SaveAppConfig(config);
    }

    // Latest-wins guard for the async tone refresh: a newer refresh (arrangement
    // switch) supersedes a slower in-flight one so a stale badge never lands.
    private int _toneRefreshToken;

    /// <summary>Single recompute of the in-game-tones badge for the loaded song +
    /// selected arrangement. Called from <see cref="PreparePrePlay"/> and whenever
    /// the arrangement changes (tones are per-arrangement). The heavy conversion
    /// runs OFF the UI thread (BUG 2 / 0.15.1) — the badge updates when it finishes.
    /// Never throws.</summary>
    private void RefreshInGameToneState() => _ = RefreshInGameTonesAsync();

    /// <inheritdoc cref="RefreshInGameToneState" />
    /// <returns>Completes when the refreshed summary has landed (or was superseded) —
    /// what the song-tone edit awaits after it rewrote the package.</returns>
    private Task RefreshInGameTonesAsync()
    {
        if (_rsToneCoordinator is null)
        {
            ApplyToneSummary(RsToneConversionSummary.None);
            return Task.CompletedTask;
        }

        // Null for an own chart — which is NOT the same as "no tones". The
        // coordinator falls back to the open project's own authored bodies, so a
        // chart with tones dialled in the Tone Designer and tone changes on its
        // timeline plays with them rather than completely dry: the one song
        // whose tones the player definitely meant.
        var psarc = _flowSong?.Inner?.Song.PackagePath;
        var identity = _flowSong?.SongIdentity;       // per-song tone preference key
        var arrangement = _projectState.ActiveArrangement;
        var token = ++_toneRefreshToken;
        // A real (off-thread) conversion is about to run — show the loading badge
        // until ITS summary lands. Only a song with no tone bodies from EITHER
        // source short-circuits inline.
        var hasSomethingToConvert = !string.IsNullOrWhiteSpace(psarc) ||
                                    (_projectState.ActiveProject?.ToneDefinitions.Count ?? 0) > 0;
        InGameTonesLoading = hasSomethingToConvert && arrangement is not null;
        return RefreshInGameToneStateAsync(psarc, arrangement, identity, token);
    }

    private async Task RefreshInGameToneStateAsync(string? psarc, Arrangement? arrangement, string? identity, int token)
    {
        RsToneConversionSummary summary;
        try
        {
            summary = await _rsToneCoordinator!.PrepareForSongAsync(psarc, arrangement, identity);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Audio, "In-game tone prepare failed; treating as no tones.", ex);
            summary = RsToneConversionSummary.None;
        }

        // The await hops back to the UI thread; drop the result if a newer refresh
        // has since been requested (e.g. the player switched arrangement) — the
        // newer refresh owns the loading badge then.
        if (token != _toneRefreshToken) return;
        InGameTonesLoading = false;
        ApplyToneSummary(summary);
    }

    /// <summary>Internal rather than private so a test can drive the tones card
    /// with a REAL <see cref="RsToneConversionSummary" /> instead of setting its
    /// half-dozen output properties by hand.</summary>
    internal void ApplyToneSummary(RsToneConversionSummary summary)
    {
        HasInGameTones = summary.HasTones;
        InGameTonesDspAvailable = summary.DspAvailable;

        var loc = _localization;
        InGameToneSummary = summary.HasTones
            ? loc.Plural("PlayMode_InGameTones_SummaryOne", "PlayMode_InGameTones_Summary", summary.ToneCount)
            : "";
        HasToneApproximations = summary.HasApproximations;
        InGameToneApproxNote = summary.HasApproximations
            ? loc.Plural("PlayMode_InGameTones_ApproximatedOne", "PlayMode_InGameTones_Approximated",
                summary.ApproximatedEffectCount + summary.SkippedEffectCount)
            : "";
        InGameToneApproxDetail = string.Join("\n", summary.Notes);

        // The Get Ready screen shows the tones as chips and the approximation
        // notes as a reviewable list, so both arrive as collections rather than
        // as the pre-joined strings the old card used.
        ToneChips.Clear();
        foreach (var name in summary.ToneNames)
        {
            ToneChips.Add(new SongToneChip(name, summary.EditedToneNames.Contains(name, StringComparer.OrdinalIgnoreCase),
                loc.Format("PlayMode_SongToneEdit_ChipName", name)));
        }

        CanEditSongTones = summary.CanSaveToneEdits && summary.DspAvailable && SongToneEdit is not null;
        SongToneEditNote = CanEditSongTones ? loc["PlayMode_GetReady_Tones_EditHint"] : "";
        ToneApproximationNotes.Clear();
        foreach (var note in summary.Notes) ToneApproximationNotes.Add(note);
        if (!summary.HasApproximations) AreToneApproximationsExpanded = false;
    }

    // ---- Editing a song tone (SongToneEditViewModel) -------------------------

    /// <summary>The chips are edit buttons: the tones came from a library song
    /// and the build has a native rig.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSongToneCommand))]
    private bool _canEditSongTones;

    /// <summary>The line under the chips saying how to edit them. Empty when they
    /// cannot be edited (an own chart, no native rig).</summary>
    [ObservableProperty] private string _songToneEditNote = "";

    /// <summary>A chip was clicked: put that tone on the rig and open it, with the
    /// edit bar over it.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSongTones))]
    private void EditSongTone(SongToneChip? chip)
    {
        if (chip is null || SongToneEdit is not { } edit || ToneRig.Pedalboard is null) return;
        if (!edit.Begin(chip.Name, chip.IsEdited)) return;
        ToneRig.OpenPedalboard();
    }
}

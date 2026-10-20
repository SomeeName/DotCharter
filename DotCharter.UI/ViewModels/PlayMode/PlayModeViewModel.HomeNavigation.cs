// PlayModeViewModel: Home's recent-song entry into the existing song-load and Get ready flow.
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.PlayMode;

public partial class PlayModeViewModel
{
    /// <summary>Opens a remembered song and path without starting a run; false when it did not open.</summary>
    public async Task<bool> OpenSongAsync(string songKey, string arrangementName)
    {
        await _homeSongGate.WaitAsync();
        try
        {
            EndRunSideEffects();
            FlowError = "";
            await SongSelect.EnsureLibrary();
            if (string.IsNullOrWhiteSpace(songKey))
            {
                CurrentScreen = PlayModeScreen.SongSelect;
                return false;
            }
            var item = SongSelect.AllSongs.FirstOrDefault(s =>
                string.Equals(s.SongIdentity, songKey, StringComparison.OrdinalIgnoreCase));
            if (item == null && SongSelect.LooksLikeProjectPath(songKey))
                item = PlaySongItem.FromProject(songKey, _files.FileExists(songKey), _localization);

            if (item is not { CanPlay: true })
            {
                CurrentScreen = PlayModeScreen.SongSelect;
                FlowError = _localization["PlayMode_LoadFailed"];
                return false;
            }

            SelectedSong = item;
            if (!await LoadFlowSong(item))
            {
                CurrentScreen = PlayModeScreen.SongSelect;
                return false;
            }
            SelectedFlowArrangement = FlowArrangements.FirstOrDefault(o => string.Equals(
                LivePlayViewModel.ProfileArrangementKey(o.Arrangement), arrangementName, StringComparison.OrdinalIgnoreCase))
                ?? FlowArrangements.FirstOrDefault(o => MatchesRailPick(o, arrangementName))
                ?? SelectedFlowArrangement;
            CurrentScreen = PlayModeScreen.TuningCheck;
            return true;
        }
        catch (Exception ex)
        {
            CurrentScreen = PlayModeScreen.SongSelect;
            FlowError = _localization["PlayMode_LoadFailed"];
            _logger.Error(AppLogCategory.Application, "Could not open the remembered song.", ex);
            return false;
        }
        finally
        {
            _homeSongGate.Release();
        }
    }
}

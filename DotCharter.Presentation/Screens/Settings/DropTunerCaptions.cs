using System.Collections.ObjectModel;
using DotCharter.Localization;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>The drop tuner quality rows of Settings ▸ Audio, in <see cref="DotCharter.Tone.Audio.DropTunerEngineMap" /> order,
/// read again in the new language on a switch, together with the screen's other formatted lines.</summary>
internal static class DropTunerCaptions
{
    private static readonly string[] Keys = ["DropTuner_QualityTight", "DropTuner_QualityBalanced", "DropTuner_QualitySmooth"];

    /// <summary>The rows in the current language; they follow every later language switch while the screen lives.</summary>
    public static ObservableCollection<string> Follow(ILocalizationService localization, AudioSettingsViewModel screen)
    {
        WeakLanguageSubscription.Subscribe(localization, screen, vm => Relabel(vm, localization));
        return [.. Keys.Select(k => localization[k])];
    }

    /// <summary>Replaces each row's caption in place and keeps the pick a list control may drop on a replace.</summary>
    internal static void Relabel(AudioSettingsViewModel screen, ILocalizationService localization)
    {
        var pick = screen.SelectedDropTunerEngineIndex;
        for (var i = 0; i < Keys.Length; i++)
        {
            var caption = localization[Keys[i]];
            if (screen.DropTunerEngineOptions[i] != caption) screen.DropTunerEngineOptions[i] = caption;
        }

        screen.SelectedDropTunerEngineIndex = pick;
        screen.RaiseTextsChanged();
    }
}

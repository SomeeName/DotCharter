using DotCharter.Localization;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Generate;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The AI creator's audio-check provenance in the charter's words: who judged the kept tone (Rocksmith itself, or
///     DotCharter's clone only and why the game did not), and one line per round. A clone-only result never reads as
///     the game's.
/// </summary>
public static class ToneCreatorCheckText
{
    /// <summary>"Judged by Rocksmith itself: 58 %", or the clone-only line with the reason.</summary>
    public static string Judge(ILocalizationService loc, RsToneLoopResult result)
    {
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(result);
        var score = Percent(loc, result.Best?.Score);
        return result.JudgedByGame
            ? loc.Format("Dialog_ToneCreator_JudgedByGame", score)
            : loc.Format("Dialog_ToneCreator_JudgedByClone", score, GameReason(loc, result.CloneOnlyReason));
    }

    /// <summary>A round's best tone (a game score before any clone score) and both judges' numbers, or that every
    /// proposal was refused and the first reason.</summary>
    public static string Round(ILocalizationService loc, RsToneLoopRound round)
    {
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(round);
        var best = round.Candidates.Where(c => c.Score is not null).MinBy(c => (c.Game is null ? 1 : 0, c.Score!.Distance));
        if (best?.Refined is not { } tone)
            return loc.Format("Dialog_ToneCreator_CheckRoundRefused", round.Index + 1, round.Candidates.FirstOrDefault(c => c.Error is not null)?.Error ?? "");
        return best.Game is { } game
            ? loc.Format("Dialog_ToneCreator_CheckRoundGame", round.Index + 1, AiToneText.Summary(tone), Percent(loc, best.CloneAfter), Percent(loc, game))
            : loc.Format("Dialog_ToneCreator_CheckRoundClone", round.Index + 1, AiToneText.Summary(tone), Percent(loc, best.CloneAfter));
    }

    /// <summary>Why the game did not judge; the stable code for the rarer faults.</summary>
    public static string GameReason(ILocalizationService loc, string? refusal) => refusal switch
    {
        GameRenderRefusals.NotReady => loc["Dialog_ToneCreator_GameNotReady"],
        GameRenderRefusals.LivePreview => loc["Dialog_ToneCreator_GameLivePreview"],
        GameRenderRefusals.GameOwnDevice => loc["Dialog_ToneCreator_GameOwnDevice"],
        _ => loc.Format("Dialog_ToneCreator_GameFailed", refusal ?? GameRenderRefusals.NoRenderer)
    };

    private static string Percent(ILocalizationService loc, ToneLoopScore? score) =>
        score?.Percent is { } p ? loc.Format("Dialog_ToneCreator_Percent", p) : loc["Dialog_ToneCreator_Unrated"];
}

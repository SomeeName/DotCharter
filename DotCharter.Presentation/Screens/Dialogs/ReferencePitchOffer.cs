using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Services.Audio.Tuning;
using DotCharter.Services.Editing.Arrangements;
using DotCharter.Services.Session;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     After audio is attached: measures the song's reference pitch and, when it disagrees
///     confidently with the active arrangement's <c>CentOffset</c>, puts the two values on the status
///     rail with one verb that corrects every arrangement whose residual disagrees, as ONE undo step.
///     Each arrangement keeps its own whole semitones. Nothing is changed without the click.
/// </summary>
public sealed class ReferencePitchOffer(IReferencePitchService service, ChartEditDialogServices edit)
{
    /// <summary>Offers after every <c>.dcsong</c> the session opens as a new project.</summary>
    public void Watch(IProjectSessionService session) => session.SongPackageOpened += OnSongPackageOpened;

    private async void OnSongPackageOpened()
    {
        try
        {
            await OfferAsync();
        }
        catch (Exception ex)
        {
            edit.Logger.Warning(AppLogCategory.Audio, "Reference pitch check after opening a song package failed", ex);
        }
    }

    public async Task<bool> OfferAsync(CancellationToken cancellationToken = default)
    {
        if (edit.Editor.ActiveArrangement is not { } active || edit.State.ActiveProject is not { } project) return false;
        var check = await service.MeasureAsync(active.ArrangementType == ArrangementType.Bass, active.CentOffset, cancellationToken);
        if (!ReferenceEquals(edit.State.ActiveProject, project)) return false;
        edit.Logger.Info(AppLogCategory.Audio,
            $"Reference pitch after attach: measured {check.MeasuredCents:+0.0;-0.0}, confidence {check.Confidence:0.00}, " +
            $"chart {check.AuthoredCentOffset:0.#}, offered={check.ShouldOffer}.");
        if (!check.ShouldOffer) return false;

        var loc = edit.Localization;
        edit.State.SetMessage(loc.Format("Status_ReferencePitchOffer", ReferencePitchPanelViewModel.Describe(check, loc)),
            StatusSeverity.Info,
            new StatusMessageAction(loc.Format("Action_ReferencePitchCorrect", Signed(check.SuggestedCentOffset)),
                () => Apply(project, check.MeasuredCents)));
        return true;
    }

    /// <summary>Moves every arrangement's residual onto <paramref name="measuredCents" />; one undo node.</summary>
    public int Apply(ChartProject project, double measuredCents)
    {
        if (!ReferenceEquals(edit.State.ActiveProject, project)) return 0;
        var actions = new List<IChartAction>();
        foreach (var arrangement in project.Arrangements)
        {
            var suggested = ReferencePitchCheck.Suggest(arrangement.CentOffset, measuredCents);
            if (suggested == arrangement.CentOffset) continue;
            var before = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
            var after = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
            after.CentOffset = suggested;
            actions.Add(new MutateArrangementSettingsAction(arrangement, before, after, edit.Editor.RepublishActiveArrangement));
        }

        if (actions.Count == 0) return 0;
        edit.History.Do(new CompositeAction(actions));
        edit.Logger.Info(AppLogCategory.Events, $"Reference pitch corrected on {actions.Count} arrangement(s) to {measuredCents:+0.0;-0.0} cents.");
        return actions.Count;
    }

    private static string Signed(double cents) => cents.ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.CurrentCulture);
}

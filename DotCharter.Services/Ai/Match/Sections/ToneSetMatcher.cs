using DotCharter.Localization;
using System.Diagnostics;
using System.Globalization;

namespace DotCharter.Services.Ai.Match.Sections;

/// <summary>
///     A whole song's sections matched in one go: one rig per part, which is what
///     makes tone-from-audio usable on a song that is not in the library.
///     <para>
///         <b>Sequential, and that is a hard constraint rather than a simplification.</b>
///         The renderer is ONE device-less engine with one chain and one seat, and its
///         own lock serialises concurrent renders — so running four sections in
///         parallel buys nothing and costs the ability to report which one is running.
///     </para>
///     <para>
///         <b>A set never fails as a whole.</b> A section that cannot be isolated,
///         cannot be matched, or is too short comes back as
///         <see cref="ToneSectionMatch" /> with a null match and a reason, and the run
///         carries on to the next part. Four sections and one missing bass stem must
///         leave the user with three rigs, not with an error dialog. CANCELLATION is
///         the one exception that propagates: a user's Cancel is not a failure of a
///         section and must not be recorded as one.
///     </para>
///     <para>
///         The prior source is optional and asked only for a section that does not
///         already carry a prior — a user's or a proposer's guess outranks the
///         model's. A prior that fails is a NOTE, never a lost section: knowledge is
///         the matcher's starting point, and it can always start from retrieval
///         instead.
///     </para>
/// </summary>
public sealed class ToneSetMatcher : IToneSetMatcher
{
    private readonly IStemIsolator _isolator;
    private readonly IToneMatcher _matcher;
    private readonly ILocalizationService _localization;
    private readonly IToneMatchPriorSource? _priors;

    /// <summary>Builds the set matcher.</summary>
    /// <param name="isolator">Cache-first stem isolation, per section's own stem.</param>
    /// <param name="matcher">The per-section matcher.</param>
    /// <param name="localization">Resolves the status and progress text the user reads.</param>
    /// <param name="priors">Optional. Null, or an unavailable source, means every
    /// section is a pure audio match.</param>
    public ToneSetMatcher(IStemIsolator isolator, IToneMatcher matcher, ILocalizationService localization, IToneMatchPriorSource? priors = null)
    {
        _isolator = isolator ?? throw new ArgumentNullException(nameof(isolator));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _localization = localization;
        _priors = priors;
    }

    /// <inheritdoc />
    public async Task<ToneSetMatchResult> MatchAsync(
        ToneSetMatchRequest request, IProgress<string>? status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sections);

        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var results = new List<ToneSectionMatch>(request.Sections.Count);
        var total = request.Sections.Count;

        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var section = request.Sections[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"Section {i + 1} of {total}");
            var step = _localization.Format("Progress_SectionOf", i + 1, total);
            var sectionClock = Stopwatch.StartNew();

            if (!section.IsUseful)
            {
                var why = ToneStems.All.Contains(section.Stem, StringComparer.OrdinalIgnoreCase)
                    ? $"the span is {section.Duration.TotalSeconds:0.0} s, shorter than the " +
                      $"{ToneSection.MinDuration.TotalSeconds:0} s a tone can be measured over"
                    : $"'{section.Stem}' is not a stem this app separates";
                results.Add(new ToneSectionMatch(section, null, why));
                notes.Add($"{prefix} ({section.Label}): skipped — {why}");
                status?.Report(_localization.Format("Progress_StepSkipped", step, why));
                continue;
            }

            try
            {
                status?.Report(_localization.Format("Progress_StepIsolating", step,
                    ToneStemNames.Display(section.Stem, _localization)));
                var target = await _isolator
                    .IsolateAsync(request.SongAudioPath, section.Stem, section.From, section.To, status, ct)
                    .ConfigureAwait(false);

                var prior = section.Prior;
                if (prior is null && _priors is { IsAvailable: true })
                {
                    status?.Report(_localization.Format("Progress_StepStartingPoint", step));
                    try
                    {
                        prior = await _priors
                            .GetPriorAsync(section, request.SongTitle, request.Artist, status, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // The contract says a prior source returns null rather than
                        // throwing. Defending anyway costs three lines and is what
                        // stops a provider outage from losing a whole song's match.
                        notes.Add($"{prefix} ({section.Label}): prior unavailable — {ex.GetType().Name}");
                    }
                }

                status?.Report(_localization.Format("Progress_StepMatching", step, section.Label));
                var match = await _matcher.MatchAsync(
                    new ToneMatchRequest(
                        target,
                        request.ReferenceDi,
                        prior,
                        request.CandidatesPerSection,
                        request.BudgetPerSection)
                    { StemHint = section.Stem },
                    status,
                    ct).ConfigureAwait(false);

                results.Add(new ToneSectionMatch(section, match, null));

                var best = match.Candidates.Count > 0 ? match.Candidates[0].Distance : double.NaN;
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{prefix} ({section.Label}, {section.Stem}): {match.Candidates.Count} candidates, " +
                    $"best {best:0.000} against a floor of {match.TargetSelfDistanceFloor:0.000}, " +
                    $"{sectionClock.Elapsed.TotalSeconds:0.0} s"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new ToneSectionMatch(section, null, ex.Message));
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{prefix} ({section.Label}): failed after {sectionClock.Elapsed.TotalSeconds:0.0} s — " +
                    $"{ex.Message}"));
                status?.Report(_localization.Format("Progress_StepFailed", step, ex.Message));
            }
        }

        var matched = results.Count(static r => r.Match is not null);
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"{matched}/{total} sections matched in {sw.Elapsed.TotalSeconds:0.0} s"));

        return new ToneSetMatchResult(results, sw.Elapsed, notes);
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Ai.Match;

namespace DotCharter.UI.ViewModels.PlayMode.ToneSections;

/// <summary>
///     "Match by section" — one rig per part of the song, saved as a tone SET.
///
///     <para>
///         <b>The owner's shape of it:</b> "when generating the tone we would
///         also have to pick stems/instrument/part like 0:10 to 0:30 clean then
///         when we want to get the distortion 0:30 to 0:50 distortion etc". So
///         the surface is a LIST: each row names a stretch of song, a stem and a
///         working label; "Match all" finds a rig for every row; and "Apply as
///         tone set" turns the chosen rigs into tone presets plus a tone change
///         at each section's start, so the song plays with tone changes.
///     </para>
///     <para>
///         <b>It works on a song that is not in the library.</b> Chart tone
///         changes are used when there ARE any (through the panel's existing
///         match source, i.e. <c>ToneChangeSpans.Resolve</c>), but a section can
///         equally be typed in or proposed from the audio, and the flow never
///         asks for an arrangement until the very last step.
///     </para>
///     <para>
///         <b>Nothing here is registered or resolved from here.</b> Every service
///         arrives through <see cref="Configure" />, which
///         <c>ToneDesignViewModel.ConfigureSections</c> forwards from the
///         composition root's <c>GetService</c>-backed funcs. With neither a
///         proposer nor a set matcher registered <see cref="IsAvailable" /> is
///         false and the block is OFF SCREEN, not disabled — a capability the app
///         cannot honour must never look live.
///     </para>
///     <para>
///         Nothing runs on the audio thread, nothing blocks the UI thread, and
///         there is no <c>async void</c>. One cancellation token covers proposing,
///         matching and narrating alike, so the one Cancel button is honest at
///         every stage.
///     </para>
/// </summary>
public sealed partial class ToneSectionsViewModel : ObservableObject
{
    /// <summary>How long a hand-added section is, before the player edits it.
    /// Comfortably over <see cref="ToneSection.MinDuration" /> so a new row is
    /// valid the moment it appears — a list whose Add button produces an invalid
    /// row reads as broken.</summary>
    internal static readonly TimeSpan NewSectionLength = TimeSpan.FromSeconds(20);

    private readonly IToneSectionHost _host;

    private readonly IAppLogger? _logger;
    private readonly ILocalizationService _loc;
    private readonly Action<Action> _post;

    private Func<IToneSectionProposer?> _proposer = static () => null;
    private Func<IToneSetMatcher?> _setMatcher = static () => null;
    private Func<IToneNarrator?> _narrator = static () => null;
    private Func<double> _playheadMs = static () => 0.0;
    private Func<IReadOnlyList<ToneSetEntry>, int?>? _placeToneChanges;

    private CancellationTokenSource? _cts;
    private int _nextId;

    /// <summary>Builds the section list over a host.</summary>
    /// <param name="host">The tone-design panel's seams — see
    /// <see cref="IToneSectionHost" />.</param>
    /// <param name="localization">The section list's words.</param>
    /// <param name="post">Marshals a worker-thread progress line onto the UI
    /// thread. Tests omit it and it runs inline.</param>
    /// <param name="logger">Receives the exception behind a "matching failed"
    /// banner, so "see the log" is true. Null (tests) logs nothing.</param>
    public ToneSectionsViewModel(
        IToneSectionHost host,
        ILocalizationService localization,
        Action<Action>? post = null,
        IAppLogger? logger = null)
    {
        _host = host;
        _logger = logger;
        _loc = localization;
        _post = post ?? (action => action());

        Stems = ToneStems.All
            .Select(id => new ToneStemOption(id, _loc[StemKey(id)]))
            .ToList();
        _defaultStem = Stems[0];
    }

    /// <summary>
    ///     The host hands the section slice in, exactly as
    ///     <c>ToneDesignViewModel.ConfigureMatch</c> hands in the match slice:
    ///     <c>Func</c>s resolved per call rather than captured instances, because
    ///     a service can be registered — or absent — independently of this view
    ///     model's lifetime.
    /// </summary>
    /// <param name="proposer">Suggests the song's parts. Null ⇒ only the chart's
    /// own tone changes can be proposed.</param>
    /// <param name="setMatcher">Matches every section in one run. Null ⇒ "Match
    /// all" is off screen.</param>
    /// <param name="narrator">Explains a chosen rig in words. Null ⇒ no Explain
    /// button.</param>
    /// <param name="playheadMs">The transport's position, for the two "set from
    /// playhead" buttons.</param>
    /// <param name="placeToneChanges">Places one tone change per entry at its
    /// section's start, through the host's undoable edit pipeline, and answers
    /// how many it placed — or NULL when the project has no arrangement to place
    /// them in, which is what makes "presets only, and say so" a different
    /// outcome from "placed none".</param>
    public void Configure(
        Func<IToneSectionProposer?> proposer,
        Func<IToneSetMatcher?> setMatcher,
        Func<IToneNarrator?>? narrator = null,
        Func<double>? playheadMs = null,
        Func<IReadOnlyList<ToneSetEntry>, int?>? placeToneChanges = null)
    {
        _proposer = proposer;
        _setMatcher = setMatcher;
        if (narrator != null) _narrator = narrator;
        if (playheadMs != null) _playheadMs = playheadMs;
        if (placeToneChanges != null) _placeToneChanges = placeToneChanges;

        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanPropose));
        OnPropertyChanged(nameof(CanMatchAll));
        ProposeCommand.NotifyCanExecuteChanged();
        MatchAllCommand.NotifyCanExecuteChanged();
    }

    // ---- state ----------------------------------------------------------------

    /// <summary>False in a build with neither a proposer nor a set matcher
    /// registered, which takes the whole block off screen.</summary>
    public bool IsAvailable => _proposer() != null || _setMatcher() != null;

    /// <summary>True when a proposer is registered OR the song has a chart whose
    /// tone changes can be offered — the chart half needs no service at all.</summary>
    public bool CanPropose => _proposer() != null || HasChartSpans();

    /// <summary>True when the set matcher is registered.</summary>
    public bool CanMatchAll => _setMatcher() != null;

    /// <summary>True when a narrator is registered and says it is available.</summary>
    public bool CanNarrate => _narrator() is { IsAvailable: true };

    /// <summary>The stem picker's options, built once and shared by every
    /// row.</summary>
    public IReadOnlyList<ToneStemOption> Stems { get; }

    /// <summary>Which stem a NEW row starts on, and the one "Propose sections"
    /// scans. Guitar by default.</summary>
    [ObservableProperty] private ToneStemOption _defaultStem;

    /// <summary>The sections, in the order they were added. The request is built
    /// in SONG order; the list is not reordered under the player's cursor.</summary>
    public ObservableCollection<ToneSectionRowViewModel> Sections { get; } = [];

    /// <summary>True once there is at least one section.</summary>
    public bool HasSections => Sections.Count > 0;

    /// <summary>
    ///     True while "Match all" cannot run because the list is empty — the
    ///     section half of <c>ToneDesignViewModel.NeedsPrompt</c>, and there for
    ///     the same reason: Match all is this block's own verb, so it says why it
    ///     cannot run rather than disappearing. Not "CanRunMatchAll inverted": a
    ///     run in flight disables it too, and "add a section first" is wrong while
    ///     the sections you added are being matched.
    /// </summary>
    public bool NeedsASection => !IsBusy && !HasSections;

    /// <summary>Suggestions waiting to be accepted or rejected.</summary>
    public ObservableCollection<ToneSectionProposalViewModel> Proposals { get; } = [];

    /// <summary>True while there are proposals on offer, which is what collapses
    /// the whole proposal block.</summary>
    public bool HasProposals => Proposals.Count > 0;

    /// <summary>True while proposing, matching, explaining or applying.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProposeCommand))]
    [NotifyCanExecuteChangedFor(nameof(MatchAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSectionCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsASection))]
    private bool _isBusy;

    /// <summary>The flow's progress line. Deliberately not the panel's
    /// <c>Status</c> or its <c>MatchStatus</c>: all three can be on screen at
    /// once and they mean different things.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    /// <summary>True when <see cref="Status" /> has something to show.</summary>
    public bool HasStatus => Status.Length > 0;

    /// <summary>The user-facing failure, already localized. The service's own
    /// words go to the log, never here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    /// <summary>True when <see cref="ErrorText" /> has something to show.</summary>
    public bool HasError => ErrorText.Length > 0;

    // ---- the section list ------------------------------------------------------

    /// <summary>
    ///     Adds one section at the playhead. It starts VALID — see
    ///     <see cref="NewSectionLength" /> — so the first thing a player does with
    ///     it is edit a working row rather than fix a broken one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void AddSection()
    {
        var from = TimeSpan.FromMilliseconds(Math.Max(0, _playheadMs()));
        Add(NextLabel(), DefaultStem.Id, from, from + NewSectionLength);
    }

    private bool NotBusy => !IsBusy;

    private ToneSectionRowViewModel Add(string label, string stem, TimeSpan from, TimeSpan to)
    {
        var row = new ToneSectionRowViewModel(
            $"section-{++_nextId}",
            label,
            stem,
            from,
            to,
            Stems,
            _playheadMs,
            RemoveSection,
            ExplainAsync,
            PublishSelected,
            () => CanNarrate,
            _loc);

        Sections.Add(row);
        OnPropertyChanged(nameof(HasSections));
        OnPropertyChanged(nameof(NeedsASection));
        ApplyCommand.NotifyCanExecuteChanged();
        MatchAllCommand.NotifyCanExecuteChanged();
        return row;
    }

    private void RemoveSection(ToneSectionRowViewModel row)
    {
        Sections.Remove(row);
        OnPropertyChanged(nameof(HasSections));
        OnPropertyChanged(nameof(NeedsASection));
        ApplyCommand.NotifyCanExecuteChanged();
        MatchAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A chosen candidate goes to the PANEL's result block, which is
    /// what hands it to Hear it / Save as tone / Discard unchanged.</summary>
    private void PublishSelected(ToneSectionRowViewModel row)
    {
        if (row.SelectedCandidate is not { } candidate) return;
        _host.PublishResult(candidate.AsDesignResult, candidate.ChainSummary);
    }

    private string NextLabel() => _loc.Format("ToneSections_DefaultLabel", Sections.Count + 1);

    // ---- propose ---------------------------------------------------------------

    /// <summary>
    ///     Offers the song's parts.
    ///
    ///     <para>
    ///         The CHART's tone changes are resolved first — the charter placed
    ///         them and they are the authoritative answer — and handed to
    ///         <see cref="IToneSectionProposer" /> as its <c>chartSpans</c>, which
    ///         is what lets the proposer report a boundary the audio AGREES with
    ///         as <see cref="ToneSectionOrigins.Both" />. With no proposer
    ///         registered the chart spans are the whole offer, so a song in the
    ///         library still proposes without the audio slice; with no chart they
    ///         are simply empty, which is the song-not-in-the-library case this
    ///         feature exists for.
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunPropose))]
    private async Task ProposeAsync()
    {
        var source = _host.ResolveMatchSource();
        var proposer = _proposer();
        var chart = ChartSections(source);

        if (proposer is null)
        {
            if (chart.Count == 0)
            {
                Fail("ToneSections_Fault_NoProposals");
                return;
            }

            ShowProposals(chart
                .Select(s => new ToneSectionProposal(s, 1.0, ToneSectionOrigins.Chart))
                .ToList());
            return;
        }

        if (source is null || string.IsNullOrWhiteSpace(source.AudioPath))
        {
            Fail("ToneSections_Fault_NoAudio");
            return;
        }

        ErrorText = string.Empty;
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            Status = _loc["ToneSections_Proposing"];
            var progress = new InlineProgress(line => _post(() => Status = line));
            var proposals = await proposer
                .ProposeAsync(source.AudioPath!, DefaultStem.Id, chart, progress, cts.Token)
                .ConfigureAwait(true);

            ShowProposals(proposals);
        }
        catch (OperationCanceledException)
        {
            Status = _loc["ToneSections_Cancelled"];
            ErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            Fail("ToneSections_Fault_Failed", ex);
        }
        finally
        {
            EndRun(cts);
        }
    }

    private bool CanRunPropose => !IsBusy && CanPropose;

    private void ShowProposals(IReadOnlyList<ToneSectionProposal> proposals)
    {
        Proposals.Clear();
        foreach (var proposal in proposals.OrderBy(p => p.Section.From))
            Proposals.Add(new ToneSectionProposalViewModel(proposal, Accept, Reject, _loc));

        OnPropertyChanged(nameof(HasProposals));

        if (Proposals.Count == 0)
        {
            Fail("ToneSections_Fault_NoProposals");
            return;
        }

        Status = _loc.Format("ToneSections_Proposed", Proposals.Count);
    }

    private void Accept(ToneSectionProposalViewModel proposal)
    {
        var section = proposal.Proposal.Section;
        var label = string.IsNullOrWhiteSpace(section.Label) ? NextLabel() : section.Label;
        Add(label, section.Stem, section.From, section.To);
        Reject(proposal);
    }

    private void Reject(ToneSectionProposalViewModel proposal)
    {
        Proposals.Remove(proposal);
        OnPropertyChanged(nameof(HasProposals));
    }

    /// <summary>The chart's tone-change spans as sections. Spans the chart layer
    /// already calls too short are skipped exactly as
    /// <c>ToneDesignViewModel.PickSpan</c> skips them.</summary>
    private IReadOnlyList<ToneSection> ChartSections(ToneMatchSource? source)
    {
        if (source is null) return Array.Empty<ToneSection>();

        var index = 0;
        return source.Spans
            .Where(s => s.IsUseful)
            .Select(s => new ToneSection(
                $"chart-{s.Index}",
                string.IsNullOrWhiteSpace(s.ToneName)
                    ? _loc.Format("ToneSections_DefaultLabel", ++index)
                    : s.ToneName,
                DefaultStem.Id,
                TimeSpan.FromMilliseconds(s.StartMs),
                TimeSpan.FromMilliseconds(s.EndMs)))
            .ToList();
    }

    private bool HasChartSpans()
    {
        var source = _host.ResolveMatchSource();
        return source is not null && source.Spans.Any(s => s.IsUseful);
    }

    // ---- match all -------------------------------------------------------------

    /// <summary>
    ///     One run over every valid section. The set matcher isolates each
    ///     section's stem, asks the prior source for a starting point and searches
    ///     — sequentially, because the renderer is one device-less engine — and
    ///     answers per section.
    ///     <para>
    ///         <b>A set never fails as a whole because one part could not be
    ///         isolated.</b> Each row carries its own outcome, and a failed row
    ///         says so beside the rows that succeeded.
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunMatchAll))]
    private async Task MatchAllAsync()
    {
        var matcher = _setMatcher();
        if (matcher is null)
        {
            Fail("ToneSections_Fault_NotAvailable");
            return;
        }

        var source = _host.ResolveMatchSource();
        if (source is null || string.IsNullOrWhiteSpace(source.AudioPath))
        {
            Fail("ToneSections_Fault_NoAudio");
            return;
        }

        var rows = Sections.Where(r => r.IsValid).OrderBy(r => r.From).ToList();
        if (rows.Count == 0)
        {
            Fail("ToneSections_Fault_NoSections");
            return;
        }

        ErrorText = string.Empty;
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var di = await _host.EnsureReferenceDiAsync(cts.Token).ConfigureAwait(true);
            if (di is null)
            {
                Fail("ToneSections_Fault_NoReferenceDi");
                return;
            }

            foreach (var row in rows)
            {
                row.StatusText = _loc["ToneSections_StatusPending"];
                row.IsFailed = false;
            }

            Status = _loc["ToneSections_Matching"];
            var progress = new InlineProgress(line => _post(() => Status = line));
            var (title, artist) = _host.SongContext();

            var result = await matcher
                .MatchAsync(
                    new ToneSetMatchRequest(
                        source.AudioPath!,
                        di,
                        rows.Select(r => r.ToSection()).ToList(),
                        SongTitle: string.IsNullOrWhiteSpace(title) ? null : title,
                        Artist: string.IsNullOrWhiteSpace(artist) ? null : artist),
                    progress,
                    cts.Token)
                .ConfigureAwait(true);

            PublishSet(result);
        }
        catch (OperationCanceledException)
        {
            Status = _loc["ToneSections_Cancelled"];
            ErrorText = string.Empty;
            foreach (var row in rows.Where(r => !r.HasCandidates)) row.StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            Fail("ToneSections_Fault_Failed", ex);
        }
        finally
        {
            EndRun(cts);
        }
    }

    private bool CanRunMatchAll => !IsBusy && CanMatchAll && Sections.Any(r => r.IsValid);

    private void PublishSet(ToneSetMatchResult result)
    {
        var matched = 0;
        foreach (var section in result.Sections)
        {
            var row = Sections.FirstOrDefault(r => r.Id == section.Section.Id);
            // A matcher that answered for a section this list does not have is
            // not an error — the player may have removed a row mid-run — but it
            // must not be silently attributed to another row either.
            if (row is null) continue;

            if (section.Match is { } match && match.Candidates.Count > 0)
            {
                row.PublishCandidates(match, _host);
                row.StatusText = _loc.Format("ToneSections_StatusMatched", row.Candidates.Count);
                row.IsFailed = false;
                matched++;
            }
            else
            {
                row.PublishCandidates(null, _host);
                // The contract's own Error string is the SERVICE's words; it goes
                // to the log, and the row gets a sentence a player can act on.
                row.StatusText = _loc["ToneSections_StatusFailed"];
                row.IsFailed = true;
            }
        }

        ApplyCommand.NotifyCanExecuteChanged();
        Status = _loc.Format("ToneSections_Matched", matched, result.Sections.Count);
        if (matched == 0) Fail("ToneSections_Fault_NoCandidates");
    }

    // ---- explain ---------------------------------------------------------------

    private async Task ExplainAsync(ToneSectionRowViewModel row)
    {
        var narrator = _narrator();
        if (narrator is not { IsAvailable: true } || row.SelectedCandidate is not { } candidate)
        {
            Fail("ToneSections_Fault_NoNarrator");
            return;
        }

        ErrorText = string.Empty;
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        row.IsExplaining = true;
        try
        {
            Status = _loc["ToneSections_Explaining"];
            var (title, artist) = _host.SongContext();
            var narration = await narrator
                .NarrateAsync(candidate.Tone.Rig, row.ToSection(), title, artist, cts.Token)
                .ConfigureAwait(true);

            // A narrator that answers null is one with no provider configured —
            // not a failure, and not an empty box either.
            row.Explanation = narration is null
                ? _loc["ToneSections_NoNarration"]
                : narration.Rationale;
            Status = string.Empty;
        }
        catch (OperationCanceledException)
        {
            Status = _loc["ToneSections_Cancelled"];
        }
        catch (Exception ex)
        {
            Fail("ToneSections_Fault_Failed", ex);
        }
        finally
        {
            row.IsExplaining = false;
            EndRun(cts);
        }
    }

    // ---- apply as tone set -----------------------------------------------------

    /// <summary>
    ///     Each section's chosen candidate becomes a tone preset named after the
    ///     section — through the drawer's OWN save path, so it lands in My Tones
    ///     visibly — and a tone change at the section's start in the current
    ///     arrangement, through the host's undoable edit pipeline.
    ///     <para>
    ///         <b>A song with no arrangement gets the presets and is TOLD so.</b>
    ///         Refusing the whole action would throw away a set the player just
    ///         spent minutes matching, and placing nothing silently would read as
    ///         the button not working. That is why the placement delegate answers
    ///         <c>null</c> for "no arrangement" and a COUNT for "placed this
    ///         many".
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        var chosen = Sections
            .Where(r => r.IsValid && r.SelectedCandidate != null)
            .OrderBy(r => r.From)
            .ToList();

        if (chosen.Count == 0)
        {
            Fail("ToneSections_Fault_NothingChosen");
            return;
        }

        ErrorText = string.Empty;
        var entries = new List<ToneSetEntry>(chosen.Count);
        foreach (var row in chosen)
        {
            var candidate = row.SelectedCandidate!;
            var saved = _host.SaveTone(
                row.Label,
                candidate.Tone.Category,
                candidate.Tone.Description,
                candidate.Tone.Rig);
            entries.Add(new ToneSetEntry(row.ToSection(), saved));
        }

        var placed = _placeToneChanges?.Invoke(entries);
        Status = placed is null
            ? _loc.Format("ToneSections_AppliedPresetsOnly", entries.Count)
            : _loc.Format("ToneSections_Applied", entries.Count, placed.Value);
    }

    private bool CanApply => !IsBusy && Sections.Any(r => r.IsValid && r.SelectedCandidate != null);

    // ---- plumbing --------------------------------------------------------------

    /// <summary>Cancels whichever stage is in flight — proposing, matching and
    /// narrating share one token, so one button is honest at every stage.</summary>
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        var cts = _cts;
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the run's finally already disposed it */ }
    }

    private void EndRun(CancellationTokenSource cts)
    {
        IsBusy = false;
        if (ReferenceEquals(_cts, cts)) _cts = null;
        cts.Dispose();
    }

    private void Fail(string key, Exception? exception = null)
    {
        // Same rule as ToneDesignViewModel.FailMatch: the reason goes to the log
        // AND onto the panel; a swallowed exception behind "see the log" is a
        // message that lies.
        var reason = exception is null ? string.Empty : $" {exception.GetType().Name}: {exception.Message}";
        ErrorText = _loc[key] + reason;
        Status = string.Empty;
        if (exception is not null)
            _logger?.Error(AppLogCategory.Errors, "Match by section failed.", exception);
    }

    private static string StemKey(string stem) => stem switch
    {
        ToneStems.Guitar => "ToneSections_Stem_Guitar",
        ToneStems.Bass => "ToneSections_Stem_Bass",
        ToneStems.Piano => "ToneSections_Stem_Piano",
        ToneStems.Other => "ToneSections_Stem_Other",
        // The vocabulary is additive by contract: a stem this build has no word
        // for prints its own id rather than an empty picker row.
        _ => stem
    };

    /// <summary>An <see cref="IProgress{T}" /> that calls its handler
    /// SYNCHRONOUSLY on the reporting thread. <see cref="Progress{T}" /> cannot be
    /// used — it defers the line to a later dispatcher pass, so a matcher's
    /// "section 2 of 4" can land after the cancel branch wrote "Cancelled". Same
    /// rule, and the same reason, as the design and match flows.</summary>
    private sealed class InlineProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }

    /// <summary>Test seam: adds a section without the playhead or a proposer, so a
    /// case can set up the list it is actually about.</summary>
    /// <param name="label">The row's label.</param>
    /// <param name="stem">One of <see cref="ToneStems" />.</param>
    /// <param name="from">Start.</param>
    /// <param name="to">End.</param>
    internal ToneSectionRowViewModel AddForTests(string label, string stem, TimeSpan from, TimeSpan to) =>
        Add(label, stem, from, to);
}

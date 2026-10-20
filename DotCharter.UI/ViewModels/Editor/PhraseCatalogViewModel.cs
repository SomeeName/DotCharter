using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Export;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The named phrase catalog pane: add the current selection under a name,
///     browse, preview, paste at the playhead, and paste by typed name — the
///     Editor-on-Fire fret-catalog verbs (<c>EOF_SRC/menu/song.c:122-136</c>,
///     <c>menu/edit.c:35-36</c>), over DotCharter's existing copy/paste plumbing.
///     <para>
///         This is its OWN surface on purpose. The inspector view models are
///         under a change freeze (<c>docs/agents/ui/misc-surfaces.md</c>), and a
///         browsable library is not a property sheet for the current selection
///         anyway.
///     </para>
/// </summary>
// ViewModelBase (not plain ObservableObject) so the app ViewLocator resolves
// PhraseCatalogView when this VM is a dock pane Context.
public partial class PhraseCatalogViewModel : ViewModelBase
{
    private readonly IPhraseCatalogService _catalog;
    private readonly IGlobalEditingCoordinator _editing;
    private readonly ChartEditorViewModel _chartEditor;
    private readonly ProjectState _state;
    private readonly ILocalizationService _localization;
    private readonly TransportViewModel _transport;

    [ObservableProperty] private PhraseCatalogEntry? _selectedEntry;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _newPhraseName = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsProblem;

    /// <summary>Armed by the first Delete click — the two-click confirm latch
    /// (the <c>Propagate</c> idiom, same shape as
    /// <c>CheckpointsViewModel.Delete</c>'s row latch). The button relabels
    /// while it is set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteCaption))]
    private bool _isConfirmingDelete;

    /// <inheritdoc cref="IsConfirmingDelete" />
    public string DeleteCaption => IsConfirmingDelete
        ? _localization["PhraseCatalog_DeleteConfirm"]
        : _localization["PhraseCatalog_Delete"];

    public PhraseCatalogViewModel(
        IPhraseCatalogService catalog,
        IGlobalEditingCoordinator editing,
        ChartEditorViewModel chartEditor,
        TransportViewModel transport,
        ProjectState state,
        ILocalizationService localization,
        Action<Action>? searchPost = null)
    {
        _localization = localization;
        _catalog = catalog;
        _editing = editing;
        _chartEditor = chartEditor;
        _transport = transport;
        _state = state;
        // Synchronous by default (tests construct this VM directly); the
        // composition root passes the UI dispatcher's background post.
        _searchPass = new Tone.UI.ViewModels.Shell.CoalescedFilterPass(RefreshEntries, searchPost ?? (action => action()));

        _catalog.CatalogChanged += RefreshEntries;

        // NOT here: reading the library deserializes EVERY stored riff (one full
        // .rscp project parse per file, and the folder grows with use). This view
        // model is a constructor argument of MainWindowViewModel, so the ctor runs
        // on the pre-window path — ahead of the first frame — for a pane that is
        // not on screen at boot and may never be opened at all. Same treatment as
        // the Menu's plan-card library scan: Background priority, i.e. once the
        // window is up. See EnsureLoaded.
        Dispatcher.UIThread.Post(EnsureLoaded, DispatcherPriority.Background);
    }

    /// <summary>Fills <see cref="Entries" /> from the library. Posted once from
    /// the constructor at <see cref="DispatcherPriority.Background" /> (i.e. after
    /// the first frame) and called again when the pane attaches, so a pane opened
    /// before that turn drains still shows its riffs. The service caches
    /// (<c>_entries ??=</c>), so the repeat is a list rebuild, not a folder
    /// read.</summary>
    public void EnsureLoaded() => RefreshEntries();

    /// <summary>Raised when a keybind asks for the find/paste-by-name box; the
    /// view focuses it. Focus is a view concern, so the VM only asks.</summary>
    public event Action? FocusSearchRequested;

    public ObservableCollection<PhraseCatalogEntry> Entries { get; } = [];

    public string StorageDirectory => _catalog.StorageDirectory;

    public bool HasEntries => Entries.Count > 0;

    public bool HasSelection => SelectedEntry != null;

    /// <summary>Localized one-line summary of the selected entry's content.</summary>
    public string SelectedSummary
    {
        get
        {
            var entry = SelectedEntry;
            if (entry == null) return "";
            // Five counted fragments in one sentence, so five PAIRS rather than
            // one string: every one of them reaches 1 in the ordinary case (a
            // phrase with a single chord, a single anchor), and German has no
            // "1 Akkorde". EntrySummary is the JOINER now, not the sentence.
            var loc = _localization;
            return loc.Format("PhraseCatalog_EntrySummary",
                loc.Plural("PhraseCatalog_Entry_NotesOne", "PhraseCatalog_Entry_NotesFmt", entry.NoteCount),
                loc.Plural("PhraseCatalog_Entry_ChordsOne", "PhraseCatalog_Entry_ChordsFmt", entry.ChordCount),
                loc.Plural("PhraseCatalog_Entry_ShapesOne", "PhraseCatalog_Entry_ShapesFmt", entry.HandShapeCount),
                loc.Plural("PhraseCatalog_Entry_AnchorsOne", "PhraseCatalog_Entry_AnchorsFmt", entry.AnchorCount),
                loc.PluralWith("PhraseCatalog_Entry_LengthOne", "PhraseCatalog_Entry_LengthFmt",
                    entry.LengthInBeats == 1.0 ? 1 : 2,
                    entry.LengthInBeats.ToString("0.##", CultureInfo.CurrentCulture)));
        }
    }

    /// <summary>Localized description of where the selected entry came from —
    /// the context the compatibility check reasons about.</summary>
    public string SelectedSourceSummary
    {
        get
        {
            var entry = SelectedEntry;
            if (entry == null) return "";
            var tuning = entry.Source.TuningNames.Count > 0
                ? string.Join(" ", entry.Source.TuningNames)
                : entry.Source.TuningType;
            return _localization.Format("PhraseCatalog_EntrySource",
                entry.Source.ArrangementName, tuning, entry.Source.Capo, entry.Source.StringCount);
        }
    }

    /// <summary>The live verdict for the selected entry against the arrangement
    /// the paste would land in — shown BEFORE the user presses paste.</summary>
    public string CompatibilityMessage
    {
        get
        {
            var entry = SelectedEntry;
            if (entry == null) return "";
            var check = PhrasePasteCompatibility.Evaluate(entry, _state.ActiveArrangement);
            return check.Severity == PhrasePasteSeverity.Ok
                ? _localization["PhraseCatalog_FitsDestination"]
                : Describe(check);
        }
    }

    public bool CompatibilityIsProblem
    {
        get
        {
            var entry = SelectedEntry;
            return entry != null &&
                   PhrasePasteCompatibility.Evaluate(entry, _state.ActiveArrangement).Severity
                   != PhrasePasteSeverity.Ok;
        }
    }

    /// <summary>Stores the current highway selection. Uses the typed name when
    /// there is one, else an auto-numbered fallback so the keybind alone is a
    /// complete gesture (EOF's <c>SHIFT+W</c> never asks either).</summary>
    [RelayCommand]
    private void CaptureSelection()
    {
        if (PlayOnlyExportGuard.IsPlayOnly(_state.ActiveProject))
        {
            SetStatus(_localization[PlayOnlyExportGuard.MessageKey], true);
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewPhraseName) ? NextAutoName() : NewPhraseName.Trim();
        var entry = _editing.CapturePhrase(_chartEditor.FocusArea, name);
        if (entry == null)
        {
            SetStatus(_localization["PhraseCatalog_CaptureEmpty"], true);
            return;
        }

        NewPhraseName = "";
        Reveal(entry);
        SetStatus(_localization.Format("PhraseCatalog_Captured", entry.Name), false);
    }

    [RelayCommand]
    private void PasteSelected() => Paste(SelectedEntry);

    /// <summary>EOF's paste-by-typed-name (<c>SHIFT+~</c>): the find box doubles
    /// as the name entry. An exact name wins; otherwise a filter that has
    /// narrowed to exactly one entry is unambiguous enough to paste.</summary>
    [RelayCommand]
    private void PasteByName()
    {
        // Riff NAMES are not unique — the store keeps IDS unique and deliberately
        // lets two riffs share a display name (PhraseCatalogService.UniqueIdLocked
        // and `TwoPhrasesWithTheSameNameGetDistinctFiles`). FindByName then
        // returns whichever the newest-first order happens to put in front, so
        // this gesture silently pasted an arbitrary one of them into the chart,
        // reported the name it was asked for, and gave the user no way to tell
        // which they had got — let alone to choose. A paste is an EDIT; guessing
        // is the one thing it may not do.
        var typed = SearchText.Trim();
        if (CountNamed(typed) > 1)
        {
            SetStatus(_localization.Format("PhraseCatalog_NameAmbiguous", typed), true);
            return;
        }

        var entry = _catalog.FindByName(typed);
        if (entry == null && Entries.Count == 1) entry = Entries[0];
        if (entry == null)
        {
            SetStatus(_localization.Format("PhraseCatalog_NoSuchPhrase", SearchText), true);
            return;
        }

        SelectedEntry = Entries.FirstOrDefault(e => e.Id == entry.Id) ?? entry;
        Paste(entry);
    }

    [RelayCommand]
    private void RenameSelected()
    {
        var entry = SelectedEntry;
        if (entry == null || string.IsNullOrWhiteSpace(NewPhraseName)) return;
        var name = NewPhraseName.Trim();
        if (!_catalog.Rename(entry, name))
        {
            SetStatus(_localization["PhraseCatalog_RenameFailed"], true);
            return;
        }

        NewPhraseName = "";
        Reveal(entry);
        SetStatus(_localization.Format("PhraseCatalog_Renamed", name), false);
    }

    /// <summary>
    ///     Puts a just-captured or just-renamed riff on screen and selects it.
    ///     <para>
    ///         The find box is a FILTER and it stays where the user left it, so
    ///         both verbs can produce an entry the filter does not match — and
    ///         both then reported success over a pane showing nothing, because
    ///         the refill rebuilt <see cref="Entries" /> from the live filter and
    ///         the id lookup that followed came back null. "Renamed 'Bridge
    ///         riff'" over an empty list reads exactly like a rename that ate the
    ///         riff, and a stored riff is the only copy of itself.
    ///     </para>
    ///     <para>
    ///         Clearing the filter is the one reading of the gesture that cannot
    ///         be wrong: the user has just NAMED the thing they want to see. A
    ///         filter the entry still matches is left alone — throwing it away
    ///         unconditionally would be a different bug.
    ///     </para>
    /// </summary>
    private void Reveal(PhraseCatalogEntry entry)
    {
        if (Entries.All(e => e.Id != entry.Id) && SearchText.Length > 0)
        {
            SearchText = "";
            // The setter only REQUESTS a pass (typing is a burst); this is a
            // discrete gesture, and the selection below has to see the refilled
            // list, not the one the filter left behind.
            _searchPass.RunImmediately();
        }

        SelectedEntry = Entries.FirstOrDefault(e => e.Id == entry.Id);
    }

    /// <summary>
    ///     Two-click confirm (the latch idiom, <c>docs/agents/ui/dialogs.md</c>).
    ///     A stored riff is the ONLY copy — it lives in the per-user library, not
    ///     in the project, so no undo, no save-as-you-were and no file to reopen
    ///     brings it back. So it never goes on a single click of a button sitting in
    ///     the same strip as Paste and Rename, and it reports when it worked.
    ///     Picking another riff, a catalogue refresh or a search re-filter
    ///     disarms; the second click reports what it removed.
    /// </summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        var entry = SelectedEntry;
        if (entry == null) return;

        if (!IsConfirmingDelete)
        {
            IsConfirmingDelete = true;
            SetStatus(_localization.Format("PhraseCatalog_DeleteArmed", entry.Name), true);
            return;
        }

        IsConfirmingDelete = false;
        if (_catalog.Remove(entry))
            SetStatus(_localization.Format("PhraseCatalog_Deleted", entry.Name), false);
        else
            // Silent before: a failed remove left the row on screen with no word
            // about why, which reads exactly like a delete that did nothing.
            SetStatus(_localization.Format("PhraseCatalog_DeleteFailed", entry.Name), true);
    }

    // `ReloadCommand` is DELETED (0.58). The pane draws no refresh affordance and
    // needs none: `CatalogChanged` fires on every add/rename/remove and the ctor
    // hooks it to `RefreshEntries`, `EnsureLoaded()` re-reads on pane attach, and
    // the one scenario the service's own doc names for `InvalidateCache` — a
    // restored backup — calls it through `ImportedConfigStores`.

    public void RequestFocusSearch() => FocusSearchRequested?.Invoke();

    private void Paste(PhraseCatalogEntry? entry)
    {
        if (entry == null) return;

        var outcome = _editing.PastePhrase(entry, _state.CurrentTimeMs, _transport.GridDivisor);
        switch (outcome.Result)
        {
            case PhrasePasteResult.NoDestination:
                SetStatus(_localization["PhraseCatalog_NoDestination"], true);
                break;
            case PhrasePasteResult.Blocked:
                SetStatus(_localization.Format("PhraseCatalog_PasteBlocked",
                    entry.Name, Describe(outcome.Check)), true);
                break;
            case PhrasePasteResult.PastedWithWarning:
                SetStatus(_localization.Format("PhraseCatalog_PastedWithWarning",
                    entry.Name, Describe(outcome.Check)), true);
                break;
            default:
                SetStatus(_localization.Format("PhraseCatalog_Pasted", entry.Name), false);
                break;
        }
    }

    /// <summary>Turns the check's localization keys + arguments into one
    /// readable sentence. The check itself stays pure and language-free.</summary>
    private string Describe(PhrasePasteCheck? check)
    {
        if (check == null || check.Issues.Count == 0) return "";
        return string.Join(" ", check.Issues.Select(issue =>
            _localization.Format(issue.LocalizationKey, issue.Args.ToArray())));
    }

    private void SetStatus(string message, bool isProblem)
    {
        StatusMessage = message;
        StatusIsProblem = isProblem;
    }

    /// <summary>How many riffs in the LIBRARY answer to this exact name — not
    /// how many the find box currently shows, which is a filter over it. The
    /// same comparison <c>IPhraseCatalogService.FindByName</c> uses.</summary>
    private int CountNamed(string trimmed)
    {
        if (trimmed.Length == 0) return 0;
        var count = 0;
        foreach (var entry in _catalog.Entries)
        {
            if (string.Equals(entry.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                count++;
        }

        return count;
    }

    private string NextAutoName()
    {
        for (var i = 1; i < 10000; i++)
        {
            var candidate = _localization.Format("PhraseCatalog_AutoName", i);
            if (_catalog.FindByName(candidate) == null) return candidate;
        }

        return _localization.Format("PhraseCatalog_AutoName", DateTime.UtcNow.Ticks);
    }

    /// <summary>Coalesces the search box's per-keystroke refill of
    /// <see cref="Entries" /> into one pass (the <c>CoalescedFilterPass</c>
    /// rule). Discrete triggers (catalogue changes, pane attach) keep calling
    /// <see cref="RefreshEntries" /> directly. The ctor's <c>searchPost</c> is
    /// its scheduler: the product default is the UI dispatcher's background
    /// post; tests pass a synchronous one.</summary>
    private readonly Tone.UI.ViewModels.Shell.CoalescedFilterPass _searchPass;

    private void RefreshEntries()
    {
        // A rebuilt list is a new set of rows; an armed Delete must not survive
        // it, or the click that lands next deletes something the user never
        // armed. Explicit rather than relying on the selection changing — the
        // same entry often stays selected across a refresh.
        IsConfirmingDelete = false;

        var previous = SelectedEntry?.Id;
        var filter = SearchText.Trim();

        Entries.Clear();
        foreach (var entry in _catalog.Entries)
        {
            if (filter.Length == 0 || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Entries.Add(entry);
        }

        SelectedEntry = Entries.FirstOrDefault(e => e.Id == previous) ?? Entries.FirstOrDefault();
        OnPropertyChanged(nameof(HasEntries));
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        // Typing is a burst: one coalesced pass, after input drains.
        _searchPass.Request();
    }

    partial void OnSelectedEntryChanged(PhraseCatalogEntry? value)
    {
        _ = value;
        // Moving the selection disarms — the armed button belongs to the riff
        // that was picked when it was armed, not to whatever is picked now.
        IsConfirmingDelete = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(SelectedSourceSummary));
        OnPropertyChanged(nameof(CompatibilityMessage));
        OnPropertyChanged(nameof(CompatibilityIsProblem));
    }
}

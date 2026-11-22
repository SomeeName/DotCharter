using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Note pages bound to the staged-edit engine (design D): Placement, Selection, Technique
///     (note and chord) and Bends stage their controls into <see cref="InspectorStagedEdits" /> and the pinned footer
///     (<see cref="Footer" />) writes them with one Apply. Each page reads "staged value, else the sound's own";
///     a stage re-reads only the page on screen and the footer (<see cref="RefreshShown" />, also on a section
///     change); an Apply, Discard, selection, history or mask change re-reads every page (<see cref="Refresh" />).
///     Timeline and keyboard edits stay immediate: they land in the history, which re-reads the drafts' baselines
///     (the engine's <c>Rebase</c>), withdraws a question the footer was asking, and then re-reads these pages.
/// </summary>
public sealed class InspectorStagedPages : IDisposable
{
    private readonly ChartEditorViewModel _editor;
    private readonly IHistoryService _history;
    private readonly ProjectState _projectState;
    private readonly SoundSectionsViewModel _sections;
    private readonly SoundQueueViewModel _queue;
    private readonly ChordTemplateEditorViewModel _chordPanel;
    private bool _refreshing;

    /// <summary>Binds the pages to <paramref name="engine" />. <paramref name="chordPanel" /> owns the chord "Apply to"
    /// rung and family: a change there moves what a chord's Apply reaches, so the footer re-reads its plan.</summary>
    public InspectorStagedPages(InspectorStagedEdits engine, ChartEditorViewModel editor, IHistoryService history,
        ProjectState projectState, ILocalizationService localization, SoundSectionsViewModel sections, SoundQueueViewModel queue,
        Func<int> gridDivisor, ChordTemplateEditorViewModel chordPanel)
    {
        GridDivisor = gridDivisor;
        Engine = engine;
        _editor = editor;
        _history = history;
        _projectState = projectState;
        _sections = sections;
        _queue = queue;
        _chordPanel = chordPanel;
        Localization = localization;
        Footer = new StagedFooterViewModel(this, sections, queue);
        Placement = new StagedPlacementViewModel(this);
        NoteTechnique = new StagedNoteTechniqueViewModel(this);
        ChordTechnique = new StagedChordTechniqueViewModel(this);
        Selection = new StagedSelectionViewModel(this);
        Bends = new StagedBendsViewModel(this);
        _history.HistoryChanged += OnHistoryChanged;
        _editor.Selection.SelectionChanged += OnSelectionChanged;
        _sections.PropertyChanged += OnSectionsChanged;
        _projectState.PropertyChanged += OnProjectStateChanged;
        _chordPanel.PropertyChanged += OnChordPanelChanged;
    }

    public InspectorStagedEdits Engine { get; }

    public StagedFooterViewModel Footer { get; }

    public StagedPlacementViewModel Placement { get; }

    public StagedNoteTechniqueViewModel NoteTechnique { get; }

    public StagedChordTechniqueViewModel ChordTechnique { get; }

    public StagedSelectionViewModel Selection { get; }

    public StagedBendsViewModel Bends { get; }

    internal ILocalizationService Localization { get; }

    /// <summary>The timeline's grid (steps per beat): one sustain step of the Placement spinner.</summary>
    internal Func<int> GridDivisor { get; }

    internal ChartEditorViewModel Editor => _editor;

    internal ProjectState ProjectState => _projectState;

    internal Arrangement? Arrangement => _editor.ActiveArrangement;

    internal int StringCount => Arrangement?.Tuning.Length ?? 6;

    internal int Capo => Math.Max(0, Arrangement?.Capo ?? 0);

    internal IReadOnlyList<string> TuningNames => TuningDisplayNames.For(Arrangement) ?? [];

    /// <summary>Every page and the footer re-read what they show: the sound itself changed (a selection or history
    /// change, an Apply or a Discard), so a hidden page's bound view must not keep the old sound's values.</summary>
    public void Refresh() => Reread(all: true);

    /// <summary>A stage or a section change: only the page that staged, the page on screen and the footer re-read (a
    /// stage changes one draft, never the sound); a hidden page re-reads when a section change shows it.</summary>
    internal void RefreshShown(StagedPageViewModel? staged = null) => Reread(all: false, staged);

    /// <summary>A page staged an edit. With the Apply step on it waits as a draft (only that page, the page on screen and
    /// the footer re-read); off (the default) it is written at once through the engine's own Apply (one undo step)
    /// and every page re-reads the sound it changed.</summary>
    internal void Staged(StagedPageViewModel page)
    {
        if (Engine.ApplyStep || KindOf(page) is not { } kind)
        {
            RefreshShown(page);
            return;
        }

        Engine.ApplyNow(kind);
        Refresh();
    }

    private static StagedPage? KindOf(StagedPageViewModel page) => page switch
    {
        StagedPlacementViewModel => StagedPage.Placement,
        StagedSelectionViewModel => StagedPage.Selection,
        StagedNoteTechniqueViewModel or StagedChordTechniqueViewModel => StagedPage.Technique,
        StagedBendsViewModel => StagedPage.Bends,
        _ => null
    };

    private void Reread(bool all, StagedPageViewModel? staged = null)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (all)
            {
                foreach (var page in new StagedPageViewModel[] { Placement, NoteTechnique, ChordTechnique, Selection, Bends }) page.Reread();
            }
            else
            {
                staged?.Reread();
                if (Shown() is { } page && !ReferenceEquals(page, staged)) page.Reread();
            }
            Footer.Reread();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>The staged page on screen, or null over a section that writes at once.</summary>
    private StagedPageViewModel? Shown() => StagedFooterViewModel.PageOf(_sections.Section, Engine.Source) switch
    {
        StagedPage.Placement => Placement,
        StagedPage.Selection => Selection,
        StagedPage.Technique when Engine.Source is GuitarNote => NoteTechnique,
        StagedPage.Technique => ChordTechnique,
        StagedPage.Bends => Bends,
        _ => null
    };

    /// <summary>Goes to <paramref name="sound" />: its arrangement and level first when they are not on screen, then
    /// selects it and seeks to it as the queue's own step does.</summary>
    internal void Show(ITimedElement sound)
    {
        if (InspectorStagedEdits.Locate(_projectState.ActiveProject, sound) is not var (arrangement, level)) return;
        if (!ReferenceEquals(_editor.ActiveArrangement, arrangement)) _editor.ActiveArrangement = arrangement;
        if (!ReferenceEquals(_projectState.ActiveLevel, level)) _projectState.ActiveLevel = level;
        _queue.SelectAndSeek(sound);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        Footer.Dismiss();
        NoteTechnique.FollowSelection();
        Refresh();
    }

    private void OnHistoryChanged()
    {
        Footer.Dismiss();
        Refresh();
    }

    private void OnSectionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoundSectionsViewModel.Section)) RefreshShown();
    }

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.StringEditMask) or nameof(ProjectState.InspectorApplyStep)) Refresh();
    }

    /// <summary>The chord "Apply to" rung or family moved: the scope a chord's Apply reaches did too.</summary>
    private void OnChordPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChordTemplateEditorViewModel.ApplyRung) or nameof(ChordTemplateEditorViewModel.ApplyFamily))
            RefreshShown();
    }

    public void Dispose()
    {
        _chordPanel.PropertyChanged -= OnChordPanelChanged;
        _history.HistoryChanged -= OnHistoryChanged;
        _editor.Selection.SelectionChanged -= OnSelectionChanged;
        _sections.PropertyChanged -= OnSectionsChanged;
        _projectState.PropertyChanged -= OnProjectStateChanged;
    }
}

/// <summary>A staged Note page: its getters read "staged, else the sound's own"; <see cref="Reread" /> re-reads them all.</summary>
public abstract class StagedPageViewModel(InspectorStagedPages owner) : ObservableObject
{
    protected InspectorStagedPages Owner { get; } = owner;

    protected InspectorStagedEdits Engine => Owner.Engine;

    protected ILocalizationService Loc => Owner.Localization;

    /// <summary>Re-reads every bound value (one notification for all of them).</summary>
    internal virtual void Reread() => OnPropertyChanged(string.Empty);

    /// <summary>This page staged something: it waits for Apply, or is written at once (<see cref="InspectorStagedPages.Staged" />).</summary>
    protected void Staged() => Owner.Staged(this);
}

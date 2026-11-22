using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
///     The Issues tab in inspector design D: four cards in a <see cref="ColumnFlowPanel" />. The code-behind keeps
///     the panel's pager and <see cref="IssuesSectionsViewModel" /> in step, both ways: a page pick opens its
///     section, a section opened from outside shows its card, a finding clicked in the list shows the Fixes card,
///     and the Review card on screen (a wide pane) keeps the Editor on Fire round trip computed.
/// </summary>
public partial class IssuesInspectorView : UserControl
{
    private InspectorViewModel? _vm;
    private bool _visible;

    public IssuesInspectorView()
    {
        InitializeComponent();
        IssuesCards.Pager.PagePicked += OnPagePicked;
        IssuesCards.ShownCardsChanged += OnShownCardsChanged;
        IssuesList.FindingOpened += OnFindingOpened;
        _ = new EffectiveVisibilityWatcher(this, visible =>
        {
            _visible = visible;
            OnShownCardsChanged();
        });
    }

    /// <summary>
    ///     The card a section opens. Findings is the list card at every width: where the list keeps the left column the
    ///     pager falls back to its first page (Fixes), and in a Tiny pane the list is the page. Never a choice by the
    ///     panel's tier, which is still the default before the first measure and would pin the Fixes page.
    /// </summary>
    internal Control CardFor(IssuesSection section) => section switch
    {
        IssuesSection.Project => IssuesProjectCard,
        IssuesSection.EditorOnFire => IssuesReviewCard,
        _ => IssuesFindingsCard
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Follow(DataContext as InspectorViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Follow(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null) Follow(DataContext as InspectorViewModel);
    }

    // The sections model outlives a redocked view: subscribe only while attached.
    private void Follow(InspectorViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        _vm?.IssuesSections.PropertyChanged -= OnSectionsChanged;
        _vm = vm;
        if (vm is null) return;
        vm.IssuesSections.PropertyChanged += OnSectionsChanged;
        IssuesCards.ShowPage(CardFor(vm.IssuesSections.Section));
        OnShownCardsChanged();
    }

    private void OnSectionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IssuesSectionsViewModel.Section) && _vm is { } vm) IssuesCards.ShowPage(CardFor(vm.IssuesSections.Section));
    }

    private void OnPagePicked(Control card)
    {
        var section = ReferenceEquals(card, IssuesProjectCard) ? IssuesSection.Project
            : ReferenceEquals(card, IssuesReviewCard) ? IssuesSection.EditorOnFire
            : IssuesSection.Findings;
        _vm?.IssuesSections.ChooseCommand.Execute(section);
        IssuesCards.ShowPage(card);
    }

    private void OnFindingOpened()
    {
        _vm?.IssuesSections.ChooseCommand.Execute(IssuesSection.Findings);
        IssuesCards.ShowPage(IssuesFixesCard);
    }

    // The panel says when a card comes on screen or leaves it; never a handler on every layout pass of the window.
    private void OnShownCardsChanged() =>
        _vm?.IssuesSections.ShowEditorOnFire(_visible && IssuesCards.ShownCards.Contains(IssuesReviewCard));
}

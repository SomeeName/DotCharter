using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.UI.Gallery.Pages;

namespace DotCharter.UI.Gallery;

/// <summary>
///     The gallery shell: sections → pages → variants, and the one control
///     currently hosted. No services, no DI — every page builds its own fakes.
/// </summary>
public sealed partial class GalleryViewModel : ObservableObject
{
    public GalleryViewModel() : this(GalleryCatalog.All) { }

    public GalleryViewModel(IReadOnlyList<GalleryPage> pages)
    {
        Sections = pages
            .GroupBy(p => p.Section, StringComparer.Ordinal)
            .Select(g => new GallerySection(g.Key, g.Select(p => new GalleryPageItem(this, p)).ToList()))
            .ToList();

        var first = Sections.SelectMany(s => s.Pages).FirstOrDefault();
        if (first != null) Select(first);
    }

    public IReadOnlyList<GallerySection> Sections { get; }

    [ObservableProperty] private GalleryPageItem? _selectedPage;

    [ObservableProperty] private GalleryVariantItem? _selectedVariant;

    /// <summary>The variant strip for the selected page. Empty until one is selected.</summary>
    [ObservableProperty] private IReadOnlyList<GalleryVariantItem> _variants = [];

    /// <summary>The live control. Rebuilt on every page/variant change.</summary>
    [ObservableProperty] private Control? _currentControl;

    public string WindowTitle =>
        SelectedPage is null
            ? "DotCharter UI Gallery"
            : $"DotCharter UI Gallery — {SelectedPage.Page.Name} · {SelectedVariant?.Variant.Name ?? "—"}";

    internal void Select(GalleryPageItem page)
    {
        if (ReferenceEquals(SelectedPage, page) && CurrentControl != null) return;

        foreach (var item in Sections.SelectMany(s => s.Pages)) item.SetSelected(ReferenceEquals(item, page));
        SelectedPage = page;
        Variants = page.Page.Variants.Select(v => new GalleryVariantItem(this, v)).ToList();
        SelectVariant(Variants[0]);
    }

    internal void SelectVariant(GalleryVariantItem variant)
    {
        foreach (var item in Variants) item.SetSelected(ReferenceEquals(item, variant));
        SelectedVariant = variant;
        // A variant is a FACTORY, never a cached instance: the same control
        // cannot hang off two parents, and a page that mutates state would
        // otherwise show the previous visit's leftovers.
        CurrentControl = variant.Variant.Create();
        OnPropertyChanged(nameof(WindowTitle));
    }
}

public sealed record GallerySection(string Name, IReadOnlyList<GalleryPageItem> Pages);

/// <summary>A page in the left navigation list. <c>IsSelected</c> drives the chip's checked look.</summary>
public sealed partial class GalleryPageItem : ObservableObject
{
    private readonly GalleryViewModel _owner;

    public GalleryPageItem(GalleryViewModel owner, GalleryPage page)
    {
        _owner = owner;
        Page = page;
    }

    public GalleryPage Page { get; }

    public string Name => Page.Name;

    [ObservableProperty] private bool _isSelected;

    /// <summary>True while the OWNER is writing the flag, so the write does not
    /// bounce back into <see cref="GalleryViewModel.Select" />.</summary>
    private bool _echo;

    partial void OnIsSelectedChanged(bool value)
    {
        if (_echo) return;
        if (value) _owner.Select(this);
        // Clicking the CHECKED chip again must not leave the shell with a page
        // shown and nothing in the nav saying which — put the check back.
        else if (ReferenceEquals(_owner.SelectedPage, this)) SetSelected(true);
    }

    internal void SetSelected(bool value)
    {
        _echo = true;
        try { IsSelected = value; }
        finally { _echo = false; }
    }
}

/// <summary>One entry on the variant strip.</summary>
public sealed partial class GalleryVariantItem : ObservableObject
{
    private readonly GalleryViewModel _owner;

    public GalleryVariantItem(GalleryViewModel owner, GalleryVariant variant)
    {
        _owner = owner;
        Variant = variant;
    }

    public GalleryVariant Variant { get; }

    public string Name => Variant.Name;

    [ObservableProperty] private bool _isSelected;

    /// <inheritdoc cref="GalleryPageItem" />
    private bool _echo;

    partial void OnIsSelectedChanged(bool value)
    {
        if (_echo) return;
        if (value) _owner.SelectVariant(this);
        else if (ReferenceEquals(_owner.SelectedVariant, this)) SetSelected(true);
    }

    internal void SetSelected(bool value)
    {
        _echo = true;
        try { IsSelected = value; }
        finally { _echo = false; }
    }
}

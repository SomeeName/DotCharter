namespace DotCharter.UI.Editing.Linking;

/// <summary>A click on a LINKS chip: the group, the control and point it came from, and the
/// arrangement and position under the pointer (the panel checks that placement).</summary>
public sealed record LinkPanelRequest(string GroupId, object? Source, Avalonia.Point Point, Arrangement? Arrangement,
    FractionalPosition At);

/// <summary>
///     Which link group the editor is pointing at: the hovered one (the LINKS row, the song
///     map), the selected one (the link panel is open on it) and a short flash after an edit
///     was copied into its placements. Every timeline and the song map read this one object,
///     so hovering a chip outlines the group everywhere.
/// </summary>
public sealed class LinkHighlight
{
    /// <summary>How long the placements an edit was copied to flash.</summary>
    public const double FlashDurationMs = 700;

    private readonly Func<long> _now;
    private long _flashStartedMs = long.MinValue;

    public LinkHighlight(Func<long>? now = null) => _now = now ?? (() => Environment.TickCount64);

    public string? HoveredGroupId { get; private set; }

    /// <summary>Where the pointer is over the hovered chip, in <see cref="HoverSource" />'s pixels.</summary>
    public Avalonia.Point HoverPoint { get; private set; }

    /// <summary>The control the hover came from (the hover card follows only its own timeline).</summary>
    public object? HoverSource { get; private set; }
    public string? SelectedGroupId { get; private set; }

    /// <summary>The group whose placements flash, while <see cref="FlashAlpha" /> is above zero.</summary>
    public string? FlashGroupId { get; private set; }

    /// <summary>The placements to flash (the edit's own placement is not among them).</summary>
    public IReadOnlyList<LinkPlacement> FlashTargets { get; private set; } = [];

    /// <summary>Raised whenever anything here changes; renderers ask for a frame.</summary>
    public event Action? Changed;

    /// <summary>A chip was clicked: open the link panel.</summary>
    public event Action<LinkPanelRequest>? PanelRequested;

    /// <summary>The group drawn emphasised: the hovered one, else the selected one.</summary>
    public string? EmphasisGroupId => HoveredGroupId ?? SelectedGroupId;

    public void SetHovered(string? groupId, object? source = null, Avalonia.Point point = default)
    {
        var moved = groupId != null && (point != HoverPoint || !ReferenceEquals(source, HoverSource));
        if (HoveredGroupId == groupId && !moved) return;
        HoveredGroupId = groupId;
        HoverSource = groupId == null ? null : source;
        HoverPoint = point;
        Changed?.Invoke();
    }

    /// <summary>Selects the request's group and asks the shell to open the link panel on it.</summary>
    public void RequestPanel(LinkPanelRequest request)
    {
        SetSelected(request.GroupId);
        PanelRequested?.Invoke(request);
    }

    public void SetSelected(string? groupId)
    {
        if (SelectedGroupId == groupId) return;
        SelectedGroupId = groupId;
        Changed?.Invoke();
    }

    public void Flash(string groupId, IReadOnlyList<LinkPlacement> targets)
    {
        FlashGroupId = groupId;
        FlashTargets = targets;
        _flashStartedMs = _now();
        Changed?.Invoke();
    }

    /// <summary>1 when a flash starts, fading linearly to 0 over <see cref="FlashDurationMs" />.</summary>
    public float FlashAlpha
    {
        get
        {
            if (FlashGroupId == null) return 0f;
            var elapsed = _now() - _flashStartedMs;
            return elapsed is < 0 or >= (long)FlashDurationMs ? 0f : (float)(1 - elapsed / FlashDurationMs);
        }
    }

    /// <summary>Forgets everything (a project switch).</summary>
    public void Clear()
    {
        HoveredGroupId = SelectedGroupId = FlashGroupId = null;
        FlashTargets = [];
        Changed?.Invoke();
    }
}

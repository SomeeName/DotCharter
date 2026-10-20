using Avalonia.Controls;
using Avalonia.Input;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.PlayMode.Screens;

/// <summary>
///     Play Mode's Song Library (Song Select). The code here is the list's plumbing,
/// moved with the markup out of <see cref="PlayModeView" />: covers streamed
///     for the realized rows only, re-ensured when the screen comes back, facet counts
///     refreshed as a funnel opens, double-click to play, and the search box's keys.
///     <see cref="PlayModeView" /> still OWNS the key routing — its tunnel handler on
///     the whole Play Mode surface calls <see cref="HandleSearchKeys" /> — so Ctrl+F
///     reaches the search box from anywhere on the screen, as it always did.
/// </summary>
public partial class SongSelectView : UserControl
{
    public SongSelectView()
    {
        InitializeComponent();

        // A search keystroke's cost, logged like a tab switch.
        _ = new SearchSettleTiming("Song Library", SongSelectSearchBox, SongListBox);

        // Covers load lazily per realized row (the list virtualizes), same
        // pattern as the song browser: no bulk decode on scan.
        SongListBox.ContainerPrepared += (_, e) =>
        {
            if (DataContext is not PlayModeViewModel { SongSelect.Library: { } library }) return;
            if (e.Container.DataContext is PlaySongItem { Inner: { } inner })
            {
                SongCoverCache.Shared.Unpark(inner);
                _ = inner.LoadCoverImageAsync(library);
            }
        };

        // …and the mirror half the browser has always wired: a recycled row's
        // cover is PARKED on the shared byte-capped LRU (see SongCoverCache) —
        // scrolling back is free, memory stays bounded, and a dwell-pending
        // load for a row that merely flew past aborts before the disk. The
        // selected song stays live — the detail rail shows it at card size.
        SongListBox.ContainerClearing += (_, e) =>
        {
            if (DataContext is not PlayModeViewModel vm) return;
            if (e.Container.DataContext is PlaySongItem { Inner: { } inner } &&
                !ReferenceEquals(vm.SelectedSong?.Inner, inner))
            {
                SongCoverCache.Shared.Park(inner);
            }
        };

        // The two song lists share PsarcSongListItem instances, so the OTHER
        // surface (the editor's song browser) can release a cover this list's
        // already-realized rows still bind — and a realized row gets no
        // ContainerPrepared when Song Select comes back. Re-ensure on the
        // effective-visibility flip (the house pattern: MenuBackdropControl).
        _ = new EffectiveVisibilityWatcher(SongListBox, visible =>
        {
            if (visible) RewarmRealizedCovers();
        });

        // Double-click a row = play it (same as its Spielen button).
        SongListBox.DoubleTapped += (_, _) =>
        {
            if (DataContext is PlayModeViewModel { SelectedSong: { } song } vm)
                vm.PlaySongCommand.Execute(song);
        };
    }

    /// <summary>Idempotent cover re-ensure for every realized row plus the
    /// selected song (whose cover the detail rail shows even when its row is
    /// scrolled out). Cheap when nothing was released — the per-item
    /// Interlocked one-shot guard makes each call a few comparisons.</summary>
    private void RewarmRealizedCovers()
    {
        if (DataContext is not PlayModeViewModel { SongSelect.Library: { } library } vm) return;
        if (vm.SelectedSong is { Inner: { } selected })
        {
            SongCoverCache.Shared.Unpark(selected);
            _ = selected.LoadCoverImageAsync(library);
        }

        foreach (var container in SongListBox.GetRealizedContainers())
        {
            if (container.DataContext is PlaySongItem { Inner: { } inner })
            {
                SongCoverCache.Shared.Unpark(inner);
                _ = inner.LoadCoverImageAsync(library);
            }
        }
    }

    /// <summary>
    ///     Song Select's keyboard flow, called by <see cref="PlayModeView" />'s TUNNEL
    ///     KeyDown handler while this screen shows (the caller gates on it).
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Ctrl+F</b> focuses the search box from anywhere on the
    ///             screen. It is claimed there rather than as a global keybind
    ///             because the chart editor already owns Ctrl+F
    ///             (<c>KeybindConfig.Toggle3DFingering</c>) — a shell-wide chord
    ///             would have taken it away from the editor, and the editor's is
    ///             routed by the bubble fallback only while the editor is visible,
    ///             so the two never meet.
    ///         </item>
    ///         <item>
    ///             <b>Esc</b> empties the box and STAYS, but only while it has
    ///             text; an empty box falls through to the shell's one Escape
    ///             chain and navigates back. The window tunnel yields the key
    ///             through <c>GlobalChordGate.AllowsEscape</c> — see
    ///             <see cref="SearchBoxKeys" /> for why the view cannot simply
    ///             out-race it.
    ///         </item>
    ///         <item><b>Down</b> hands the keyboard to the song list.</item>
    ///     </list>
    /// </summary>
    internal void HandleSearchKeys(KeyEventArgs e)
    {
        if (SearchBoxKeys.IsFocusSearchChord(e.Key, e.KeyModifiers))
        {
            SongSelectSearchBox.Focus();
            SongSelectSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // The remaining two only mean anything while the box itself has the
        // keyboard — Down is the list's own key everywhere else on the screen.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox box ||
            !ReferenceEquals(box, SongSelectSearchBox))
        {
            return;
        }

        switch (SearchBoxKeys.Resolve(e.Key, e.KeyModifiers, !string.IsNullOrEmpty(box.Text)))
        {
            case SearchBoxKeyAction.ClearText:
                // Through the box, so the two-way binding writes SearchText and
                // the VM's own coalesced pass runs exactly as it does for typing.
                box.Text = "";
                e.Handled = true;
                break;
            case SearchBoxKeyAction.FocusList:
                if (!FocusSongList()) return;
                e.Handled = true;
                break;
        }
    }

    /// <summary>A facet popover is OPENING — refresh the per-option match
    /// counts now, not per filter pass: the numbers are only visible inside an
    /// open popover, and counting nine facets on every settled keystroke would
    /// tax typing for figures nobody is looking at. All facets are refreshed
    /// (the stacked popovers show up to four), one cheap pass per facet over
    /// the collapsed list on a discrete click. Same contract as the editor
    /// browser's handler of the same name.</summary>
    private void OnFacetFlyoutOpening(object? sender, EventArgs e)
    {
        if (DataContext is not PlayModeViewModel vm) return;
        foreach (var facet in vm.SongSelect.SongFacets) vm.SongSelect.RefreshSongFacetCounts(facet);
    }

    /// <summary>Moves the keyboard from the search box into the song list,
    /// selecting the first row when nothing is selected yet. False when the list
    /// is empty — the caret then stays where it is rather than being parked on a
    /// list with no rows.</summary>
    private bool FocusSongList()
    {
        var target = SearchBoxKeys.SelectionForFocusHandover(SongListBox.SelectedIndex, SongListBox.ItemCount);
        if (target < 0) return false;

        SongListBox.SelectedIndex = target;
        if (SongListBox.ContainerFromIndex(target) is { } container) container.Focus();
        else SongListBox.Focus();
        return true;
    }
}

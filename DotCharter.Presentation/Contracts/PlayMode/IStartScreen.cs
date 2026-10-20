using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
///     The redesigned Play Mode &gt; Start screen — the front DOOR, the one
///     frame before the menu.
///     <para>
///         Derived from what the current <c>StartScreenRoot</c> exposes: the
///         wordmark lockup, the profile switcher, the guest-session entry, the
///         left-handed toggle, the three status pills (library / latency /
///         input) and START. What it deliberately does NOT model is the profile
///         MANAGEMENT verbs the current screen hangs off the same panel —
///         rename, picture, import, export — because those are a dialog's job,
///         and six icon buttons on the door is why it read as a settings page.
///     </para>
/// </summary>
public interface IStartScreen : INotifyPropertyChanged
{
    /// <summary>The build stamp, already formatted ("v0.62.1").</summary>
    string AppVersion { get; }

    /// <summary>The profiles this machine knows, in switcher order.</summary>
    IReadOnlyList<IProfileTile> Profiles { get; }

    /// <summary>True when <see cref="Profiles" /> is non-empty.</summary>
    bool HasProfiles { get; }

    /// <summary>The name over START. Empty in the guest case.</summary>
    string ActiveProfileName { get; }

    /// <summary>Whether the active profile mirrors the highway.</summary>
    bool IsLefty { get; set; }

    /// <summary>Library / latency / input, as the three pills under the lockup.</summary>
    IReadOnlyList<IFactTile> StatusFacts { get; }

    /// <summary>Enters the menu as the active profile.</summary>
    ICommand StartCommand { get; }

    /// <summary>Whether <see cref="StartCommand" /> would do anything.</summary>
    bool CanStart { get; }

    /// <summary>Opens the "add a new profile" field on the panel.</summary>
    ICommand AddProfileCommand { get; }

    /// <summary>True while the guest panel is open instead of the profile list.</summary>
    bool IsGuestEntryOpen { get; }

    /// <summary>Opens and closes the guest panel.</summary>
    ICommand ToggleGuestEntryCommand { get; }

    /// <summary>The guest's name as typed.</summary>
    string GuestName { get; set; }

    /// <summary>Calibrates and plays as a throwaway guest.</summary>
    ICommand StartGuestSessionCommand { get; }

    /// <summary>True once <see cref="GuestName" /> is worth starting with.</summary>
    bool CanStartGuestSession { get; }
}

/// <summary>One profile in the switcher.</summary>
public interface IProfileTile : INotifyPropertyChanged
{
    string Name { get; }

    /// <summary>The avatar's letter — the fake has no images and nor does the door.</summary>
    string Initial { get; }

    /// <summary>"124 songs · 11 day streak", already formatted.</summary>
    string DetailLabel { get; }

    bool IsActive { get; }

    bool IsLefty { get; }

    /// <summary>Makes this the active profile. Always republishes.</summary>
    ICommand SelectCommand { get; }
}

using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Presentation.Shared;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The AI chat pane's dock Context. The dock needs a <see cref="ViewModelBase" /> the
///     <c>ViewLocator</c> maps to a view (<c>AiChatDockView</c>), and the pane itself is a
///     target-architecture screen bound to <see cref="IAiChatScreen" />, so this holds the ONE
///     <see cref="AiChatViewModel" /> and nothing else: a float or redock builds a new view over
///     the same conversation.
///     <para>
///         It also re-reads Settings ▸ AI when the Settings tab closes, so a provider chosen
///         there shows in the pane without a restart.
///     </para>
/// </summary>
public sealed class AiChatDockViewModel : ViewModelBase
{
    public AiChatDockViewModel(AiChatViewModel screen, SettingsSession settings, ILocalizationService localization)
    {
        Chat = screen;
        PaneTitle = localization["Menu_PanelAiChat"];
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsSession.IsOpen) && !settings.IsOpen) screen.RefreshProvider();
        };
    }

    /// <summary>The pane's screen view model; the shell answers its Settings door.</summary>
    public AiChatViewModel Chat { get; }

    /// <summary>The dock tab's and the float window's title.</summary>
    public string PaneTitle { get; }

    /// <summary>What the pane's view binds.</summary>
    public IAiChatScreen Screen => Chat;
}

using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Editor;

/// <summary>
/// The AI chat pane: a conversation about the open chart, docked on the right
///     of the editor. The charter picks a model from the configured provider, says what the next
///     message is about (the selection, the section at the playhead or the whole arrangement, and
///     which arrangement), and reads the replies with their cost. The pane itself changes nothing in
///     the chart: proposals go to the review card, which is not part of this contract.
///     <para>
///         The earlier chats of THIS chart are <see cref="History" />, a contract of its own so the
/// per-chart store can implement it without the rest of the pane.
///     </para>
/// </summary>
public interface IAiChatScreen : INotifyPropertyChanged
{
    /// <summary>The open conversation's title: its first message, or "New chat" before one is sent.</summary>
    string Title { get; }

    // ── the model ────────────────────────────────────────────────────────

    /// <summary>False when Settings ▸ AI names no provider; the model picker is then disabled and the reason shown.</summary>
    bool HasProvider { get; }

    /// <summary>Why there is no provider and where to set one up. Empty while <see cref="HasProvider" />.</summary>
    string NoProviderText { get; }

    /// <summary>The configured provider in words ("Claude Code, your Claude sign-in"), for the picker's tooltip.</summary>
    string ProviderText { get; }

    /// <summary>Opens Settings ▸ AI.</summary>
    ICommand OpenAiSettingsCommand { get; }

    /// <summary>The models the configured provider offers; empty without a provider.</summary>
    IReadOnlyList<IAiChatOption> Models { get; }

    IAiChatOption? SelectedModel { get; set; }

    /// <summary>The reasoning level Settings ▸ AI sends with the picked model ("Reasoning: High"),
    /// shown beside it; "Default" where this provider or model cannot take one. Empty without a provider.</summary>
    string EffortText { get; }

    // ── the task ─────────────────────────────────────────────────────────

    /// <summary>What the AI may change: chord fingering with the hand positions kept (the
    /// measured default), hand positions and fingering together (the opt-in), sections and
    /// phrases, or an answer in words. Notes never change; the choice is explicit, never guessed
    /// from the text. A fingering pick is remembered per arrangement type.</summary>
    IReadOnlyList<IAiChatOption> Jobs { get; }

    IAiChatOption? SelectedJob { get; set; }

    /// <summary>Why the picked fingering task is the default on this arrangement, or what the
    /// opt-in trades, with the measured numbers. Empty for the other tasks.</summary>
    string JobNote { get; }

    bool HasJobNote { get; }

    // ── the scope ────────────────────────────────────────────────────────

    /// <summary>Selection, this section, whole arrangement — what the next message is about.</summary>
    IReadOnlyList<IAiChatOption> Scopes { get; }

    IAiChatOption? SelectedScope { get; set; }

    /// <summary>The open chart's arrangements, by name.</summary>
    IReadOnlyList<IAiChatOption> Arrangements { get; }

    IAiChatOption? SelectedArrangement { get; set; }

    /// <summary>What the chosen scope covers right now ("Bars 17–32 · 48 notes · 6 chords"), or why it is empty.</summary>
    string ScopeSummary { get; }

    // ── the conversation ─────────────────────────────────────────────────

    /// <summary>The turns, oldest first.</summary>
    IReadOnlyList<IAiChatTurn> Turns { get; }

    bool HasTurns { get; }

    /// <summary>Shown while the conversation is empty: what the picked task does and what to say,
    /// or that a chart must be open first.</summary>
    string EmptyText { get; }

    /// <summary>The message box's hint for the picked task: an example of what to type
    /// ("e.g. Why is the hand in 5th position in bar 19?").</summary>
    string InputHint { get; }

    /// <summary>True while a reply is being worked on; Send gives way to Cancel.</summary>
    bool IsBusy { get; }

    /// <summary>"Working…", with what the model is doing when the provider says.</summary>
    string BusyText { get; }

    /// <summary>Stops the reply being worked on; the message stays in the conversation.</summary>
    ICommand CancelCommand { get; }

    bool HasError { get; }

    /// <summary>Why the last reply failed. Empty while <see cref="HasError" /> is false.</summary>
    string ErrorText { get; }

    /// <summary>The message being typed. Enter sends it, Shift+Enter starts a new line.</summary>
    string InputText { get; set; }

    /// <summary>Sends <see cref="InputText" />; cannot run while it is blank, while busy or without a provider.</summary>
    ICommand SendCommand { get; }

    /// <summary>Starts an empty conversation; the current one stays in <see cref="History" />.</summary>
    ICommand NewChatCommand { get; }

    /// <summary>The open chart's earlier chats.</summary>
    IAiChatHistory History { get; }
}

/// <summary>One choice in a picker: a model, a scope or an arrangement.</summary>
public interface IAiChatOption : INotifyPropertyChanged
{
    string Label { get; }

    /// <summary>A second line where one helps ("claude-sonnet-5-5", "Bars 17–32"); may be empty.</summary>
    string Detail { get; }
}

/// <summary>One message in the conversation.</summary>
public interface IAiChatTurn : INotifyPropertyChanged
{
    /// <summary>True for the charter's message, false for the model's reply.</summary>
    bool IsUser { get; }

    /// <summary>"You", or the model that replied ("Claude Sonnet 5.5").</summary>
    string RoleText { get; }

    string Text { get; }

    /// <summary>When it was sent or received, in the user's time format ("14:02").</summary>
    string TimeText { get; }

    /// <summary>"1,240 in · 380 out · $0.004" when the provider reported it; empty otherwise.</summary>
    string CostText { get; }

    bool HasCost { get; }

    /// <summary>True on the LAST reply when it came back with no answer (the run failed, or another
    /// run was in flight); false on every other turn.</summary>
    bool CanRetry { get; }

    /// <summary>Sends the message this reply answered again — same words, task, scope and model — in
    /// the reply's place. Applies nothing; a proposal still goes to the review card. Null unless
    /// <see cref="CanRetry" />; cannot run while a reply is being worked on.</summary>
    ICommand? RetryCommand { get; }

    /// <summary>True on a reply that lists findings the pass (or an Accept) left in its range and a job
    /// can fix: "Ask the AI to fix these".</summary>
    bool CanFix { get; }

    /// <summary>Runs a follow-up pass with those findings in the prompt; its result is held for the
    /// review card like any other. Null unless <see cref="CanFix" />.</summary>
    ICommand? FixCommand { get; }
}

/// <summary>
///     The open chart's earlier chats, as a searchable dropdown under the
///     title. Only this chart's chats are listed; opening a conversation that
///     belongs to another chart is refused with <see cref="NoticeText" />.
/// </summary>
public interface IAiChatHistory : INotifyPropertyChanged
{
    /// <summary>True while the dropdown under the title is open.</summary>
    bool IsOpen { get; set; }

    /// <summary>"Chats about Song title".</summary>
    string Caption { get; }

    /// <summary>What the list is narrowed to: a chat shows when its title or model contains it,
    /// case-insensitively. Empty lists every chat.</summary>
    string SearchText { get; set; }

    /// <summary>The chats <see cref="SearchText" /> leaves, newest first.</summary>
    IReadOnlyList<IAiChatHistoryEntry> Entries { get; }

    bool HasEntries { get; }

    /// <summary>Why the list is empty: no earlier chats, or none matching <see cref="SearchText" />.</summary>
    string EmptyText { get; }

    /// <summary>What the last pick did not do and why (a chat of another chart was refused). Empty when there is nothing to say.</summary>
    string NoticeText { get; }

    bool HasNotice { get; }
}

/// <summary>One earlier chat in the picker.</summary>
public interface IAiChatHistoryEntry : INotifyPropertyChanged
{
    string Title { get; }

    /// <summary>When it was last continued ("Yesterday, 21:40").</summary>
    string DateText { get; }

    /// <summary>The model it was held with.</summary>
    string ModelText { get; }

    /// <summary>True for the chat open in the pane.</summary>
    bool IsCurrent { get; }

    /// <summary>Opens this chat in the pane and closes the picker.</summary>
    ICommand OpenCommand { get; }

    bool IsRenaming { get; }

    /// <summary>The title being typed while <see cref="IsRenaming" />.</summary>
    string TitleDraft { get; set; }

    ICommand BeginRenameCommand { get; }

    ICommand CommitRenameCommand { get; }

    ICommand CancelRenameCommand { get; }

    /// <summary>Asks first: the row turns into <see cref="DeletePromptText" />
    /// with <see cref="ConfirmDeleteCommand" /> and <see cref="CancelDeleteCommand" />.</summary>
    ICommand DeleteCommand { get; }

    /// <summary>True while the row asks whether to delete.</summary>
    bool IsConfirmingDelete { get; }

    /// <summary>"Delete “title”? This cannot be undone."</summary>
    string DeletePromptText { get; }

    /// <summary>Deletes the chat's file; the row leaves the list.</summary>
    ICommand ConfirmDeleteCommand { get; }

    ICommand CancelDeleteCommand { get; }
}

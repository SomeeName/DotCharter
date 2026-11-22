using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chat;
using DotCharter.Tone.Logging;
using IAiChatHistory = DotCharter.Services.Ai.Chat.IAiChatHistory;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>A model, scope or arrangement choice; <see cref="Key" /> is what the view model acts on.</summary>
public sealed class AiChatOptionItem(string label, string detail, object key) : ObservableObject, IAiChatOption
{
    public string Label { get; private set; } = label;

    public string Detail { get; } = detail;

    public object Key { get; } = key;

    /// <summary>The resx key the label is read from; null for a literal label (a model, an arrangement).</summary>
    internal string? LabelKey { get; init; }

    public override string ToString() => Label;

    /// <summary>Reads the label again in the current language; the item, and so a selection of it, stays.</summary>
    internal void Relabel(ILocalizationService localization)
    {
        if (LabelKey is null) return;
        Label = localization[LabelKey];
        OnPropertyChanged(nameof(Label));
    }
}

/// <summary>A row of the arrangement picker; <see cref="Key" /> is the arrangement, null for "All arrangements".</summary>
public sealed class AiChatArrangementChoice(string label, object? key, Action<AiChatArrangementChoice> toggled)
    : ObservableObject, IAiChatArrangementChoice
{
    private bool _isChecked;

    public string Label { get; } = label;

    public string Detail => "";

    public object? Key { get; } = key;

    public bool IsAll => Key is null;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value)) return;
            toggled(this);
        }
    }

    /// <summary>Sets the box without telling the picker: the picker is the one changing it.</summary>
    internal void Set(bool value) => SetProperty(ref _isChecked, value, nameof(IsChecked));

    public override string ToString() => Label;
}

/// <summary>One message as the pane shows it: a stored turn, or a notice the pane adds itself.</summary>
public sealed class AiChatTurnItem(bool isUser, string role, string text, string time, string cost = "", ICommand? retry = null)
    : ObservableObject, IAiChatTurn
{
    public bool CanRetry => RetryCommand is not null;

    public bool CanFix => FixCommand is not null;

    public ICommand? FixCommand { get; init; }

    /// <summary>Set on a reply that came back with no answer: sends the same request again.</summary>
    public ICommand? RetryCommand { get; } = retry;

    public bool IsUser { get; } = isUser;

    public string RoleText { get; } = role;

    public string Text { get; } = text;

    public string TimeText { get; } = time;

    /// <summary>"2,310 in · 214 out" when the provider reported the tokens; empty otherwise.</summary>
    public string CostText { get; } = cost;

    public bool HasCost => CostText.Length > 0;

    public override string ToString() => $"{RoleText}: {Text}";
}

/// <summary>
/// What the pane's Send drives: the open chart's chat history and the chart pass
/// that turns an instruction into HELD proposals for the review card. One bundle so
///     the view model stays inside its constructor budget.
/// </summary>
public sealed class AiChatServices(IAiChatHistory history, IAiChatChartActions actions, IAppLogger logger, TimeProvider? time = null)
{
    public IAppLogger Logger { get; } = logger;

    public IAiChatHistory History { get; } = history;

    public IAiChatChartActions Actions { get; } = actions;

    public TimeProvider Time { get; } = time ?? TimeProvider.System;
}

/// <summary>The AI chat as the Issues tab's door: Fix and Explain with AI are sent as chat messages.</summary>
public interface IAiChatFindingsDoor
{
    bool CanAsk { get; }

    string? PickedModelId { get; }

    event Action? ShowRequested;

    /// <summary>Sends the request as the charter's message; false, with the reason in the pane, when it cannot.</summary>
    Task<bool> AskAsync(AiChatInstructionRequest request);
}

/// <summary>A model the chat can name, with the words the picker shows.</summary>
public sealed record AiChatModel(string Id, string Label);

/// <summary>
///     The models the configured provider offers in the chat's picker: the Claude and Codex
///     presets Settings ▸ AI offers as fill-in buttons, plus the model typed there when it is none
///     of them (a local server's model is only ever the typed one). Pure, so a new provider is a
///     row here.
/// </summary>
public static class AiChatModelCatalog
{
    private static readonly AiChatModel[] Claude =
    [
        new(ClaudeCliToneModel.DefaultModelId, "Claude Haiku 5.5"),
        new(ClaudeCliToneModel.AlternateModelId, "Claude Sonnet 5.5"),
        new(ClaudeCliToneModel.OpusModelId, "Claude Opus 5.5")
    ];

    private static readonly AiChatModel[] Codex =
    [
        new(CodexCliToneModel.LunaModelId, "GPT-6 Luna"),
        new(CodexCliToneModel.SolModelId, "GPT-6 Sol")
    ];

    /// <summary>True when Settings ▸ AI names a provider the chat can use. The bundled model
    /// is reserved and not built, so it counts as none.</summary>
    public static bool IsUsable(AiProviderSettings settings) =>
        settings.IsEnabled && settings.Kind != AiProviderKind.BuiltIn;

    public static IReadOnlyList<AiChatModel> For(AiProviderSettings settings)
    {
        if (!IsUsable(settings)) return [];
        IReadOnlyList<AiChatModel> presets = settings.Kind switch
        {
            AiProviderKind.Claude or AiProviderKind.ClaudeCli => Claude,
            AiProviderKind.CodexCli => Codex,
            _ => []
        };
        var typed = settings.ModelId.Trim();
        return typed.Length == 0 || presets.Any(m => m.Id == typed) ? presets : [.. presets, new(typed, typed)];
    }

    /// <summary>The model the picker starts on: the one Settings ▸ AI names, else the provider's default.</summary>
    public static string? DefaultId(AiProviderSettings settings)
    {
        var typed = settings.ModelId.Trim();
        return typed.Length > 0 ? typed : AiDefaultModels.For(settings.Kind);
    }

    /// <summary>The words for a stored model id; an id no preset knows is shown as it is.</summary>
    public static string LabelOf(string modelId) =>
        Claude.Concat(Codex).FirstOrDefault(m => m.Id == modelId)?.Label ?? modelId;
}

/// <summary>The pane's words for the history's answers and its times.</summary>
internal static class AiChatText
{
    /// <summary>The visible, translated reason for a refusal of <see cref="IAiChatHistory" />.</summary>
    public static string Status(ILocalizationService localization, AiChatStatus status) => localization[status switch
    {
        AiChatStatus.NoChart => "AiChat_Status_NoChart",
        AiChatStatus.ChartNotSaved => "AiChat_Status_ChartNotSaved",
        AiChatStatus.ChartMismatch => "AiChat_Status_ChartMismatch",
        AiChatStatus.NotFound => "AiChat_Status_NotFound",
        AiChatStatus.Corrupt => "AiChat_Status_Corrupt",
        AiChatStatus.UnsupportedFormat => "AiChat_Status_UnsupportedFormat",
        AiChatStatus.Unreadable => "AiChat_Status_Unreadable",
        AiChatStatus.TooLarge => "AiChat_Status_TooLarge",
        AiChatStatus.LimitReached => "AiChat_Status_LimitReached",
        AiChatStatus.InvalidId => "AiChat_Status_InvalidId",
        _ => "AiChat_Status_WriteFailed"
    }];

    /// <summary>The key of the configured provider's name, for the model picker's tooltip.</summary>
    public static string ProviderKey(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Claude => "Ai_Provider_Claude",
        AiProviderKind.OpenAiCompatible => "Ai_Provider_OpenAiCompatible",
        AiProviderKind.CodexCli => "Ai_Provider_CodexCli",
        _ => "Ai_Provider_ClaudeCli"
    };

    /// <summary>"2,310 in · 214 out", or empty when nothing was reported.</summary>
    public static string Tokens(ILocalizationService localization, AiChatUsage? usage) =>
        usage is { InputTokens: { } input, OutputTokens: { } output }
            ? localization.Format("AiChat_Tokens", input.ToString("N0", CultureInfo.CurrentCulture),
                output.ToString("N0", CultureInfo.CurrentCulture))
            : "";

    /// <summary>"1 note · 3 chords": each count in its own singular or plural form.</summary>
    public static string Counts(ILocalizationService localization, int notes, int chords) =>
        localization.Format("AiChat_Count_Pair",
            localization.Plural("AiChat_Count_NotesOne", "AiChat_Count_Notes", notes),
            localization.Plural("AiChat_Count_ChordsOne", "AiChat_Count_Chords", chords));

    /// <summary>"14:02".</summary>
    public static string Time(DateTimeOffset at) =>
        at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>"Today, 14:02", "Yesterday, 21:40" or "7 Oct, 19:12" in the user's culture.</summary>
    public static string Date(ILocalizationService localization, DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = Time(at);
        if (local.Date == today) return localization.Format("AiChat_Today", time);
        if (local.Date == today.AddDays(-1)) return localization.Format("AiChat_Yesterday", time);
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern;
        return $"{local.ToString(pattern, CultureInfo.CurrentCulture)}, {time}";
    }
}

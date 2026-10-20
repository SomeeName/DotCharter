using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Ai;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     What Settings ▸ AI shows for one provider: which rows and notes apply,
///     the words the CLI path and model rows use, the model it starts with, and
///     what the picker says when this machine cannot offer it. One entry per
///     provider, so a new provider is one row here rather than a condition in
///     every property of the view model.
/// </summary>
public sealed record AiProviderShape
{
    private static readonly AiProviderShape Off = new();

    public bool ShowKey { get; private init; }

    public bool ShowBaseUrl { get; private init; }

    public bool ShowCliPath { get; private init; }

    public bool ShowWebResearch { get; private init; }

    public bool ShowClaudeCliNote { get; private init; }

    public bool ShowCodexCliNote { get; private init; }

    public bool ShowClaudePresets { get; private init; }

    public bool ShowCodexPresets { get; private init; }

    /// <summary>An empty model box is valid: the provider picks its own default.</summary>
    public bool ModelIsOptional { get; private init; }

    /// <summary>What an empty model box is filled with when the provider is picked, or null.</summary>
    public string? DefaultModel { get; private init; }

    public string CliPathLabelKey { get; private init; } = "Ai_CliPathLabel";

    public string CliPathDescKey { get; private init; } = "Ai_CliPathDesc";

    public string ModelDescKey { get; private init; } = "Ai_ModelDesc";

    /// <summary>What the picker says when this machine cannot offer the provider.</summary>
    public string MissingKey { get; private init; } = "Ai_Provider_BuiltInHint";

    /// <summary>The picker's rows, in order. None is the off switch and is always offered.</summary>
    public static IReadOnlyList<AiProviderOption> Options(ILocalizationService localization, Func<AiProviderKind, bool> supports) =>
    [
        new(AiProviderKind.None, localization["Ai_Provider_None"], true),
        new(AiProviderKind.Claude, localization["Ai_Provider_Claude"], supports(AiProviderKind.Claude)),
        new(AiProviderKind.OpenAiCompatible, localization["Ai_Provider_OpenAiCompatible"], supports(AiProviderKind.OpenAiCompatible)),
        new(AiProviderKind.ClaudeCli, localization["Ai_Provider_ClaudeCli"], supports(AiProviderKind.ClaudeCli)),
        new(AiProviderKind.CodexCli, localization["Ai_Provider_CodexCli"], supports(AiProviderKind.CodexCli)),
        new(AiProviderKind.BuiltIn, localization["Ai_Provider_BuiltIn"], supports(AiProviderKind.BuiltIn))
    ];

    public static AiProviderShape For(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Claude => Off with { ShowKey = true, DefaultModel = AiDefaultModels.For(AiProviderKind.Claude) },
        AiProviderKind.OpenAiCompatible => Off with { ShowKey = true, ShowBaseUrl = true },
        AiProviderKind.ClaudeCli => Off with
        {
            ShowCliPath = true,
            ShowWebResearch = true,
            ShowClaudeCliNote = true,
            ShowClaudePresets = true,
            DefaultModel = AiDefaultModels.For(AiProviderKind.ClaudeCli),
            MissingKey = "Ai_Provider_ClaudeCliMissing"
        },
        AiProviderKind.CodexCli => Off with
        {
            ShowCliPath = true,
            ShowWebResearch = true,
            ShowCodexCliNote = true,
            ShowCodexPresets = true,
            ModelIsOptional = true,
            DefaultModel = AiDefaultModels.For(AiProviderKind.CodexCli),
            CliPathLabelKey = "Ai_CodexPathLabel",
            CliPathDescKey = "Ai_CodexPathDesc",
            ModelDescKey = "Ai_CodexModelDesc",
            MissingKey = "Ai_Provider_CodexCliMissing"
        },
        _ => Off
    };
}

/// <summary>One row of the provider picker, with whether this build and machine can offer it.</summary>
public sealed record AiProviderOption(AiProviderKind Kind, string Label, bool IsAvailable) : IAiProviderOption;

namespace DotCharter.Tone.Ai;

/// <summary>
/// The reasoning choice in words, for every Reasoning row (DotCharter's Settings ▸ AI,
///     RigLab's AI tab) and the AI chat's label alike. It lives here, beside
///     <see cref="AiReasoning" />, so the app and RigLab read ONE set of resx keys and ONE rule
/// for when the picker is live.
/// </summary>
public static class AiReasoningText
{
    public static IReadOnlyList<AiReasoningEffort> Levels { get; } =
        [AiReasoningEffort.Default, AiReasoningEffort.Low, AiReasoningEffort.Medium, AiReasoningEffort.High];

    public static string Key(AiReasoningEffort effort) => effort switch
    {
        AiReasoningEffort.Low => "Ai_Effort_Low",
        AiReasoningEffort.Medium => "Ai_Effort_Medium",
        AiReasoningEffort.High => "Ai_Effort_High",
        _ => "Ai_Effort_Default"
    };

    /// <summary>The resx key of the reason a provider/model cannot take the choice, or null when it can.</summary>
    public static string? UnsupportedKey(AiReasoningUnsupported why) => why switch
    {
        AiReasoningUnsupported.ClaudeModelTooOld => "Ai_Effort_Unsupported_ClaudeModel",
        AiReasoningUnsupported.NoProvider => "Ai_Effort_Unsupported_NoProvider",
        _ => null
    };

    /// <summary>Whether <paramref name="kind" /> with the model id ON SCREEN can take a level. An
    /// empty box is resolved to the provider's own default first, as the provider resolves it.</summary>
    public static AiReasoningUnsupported Support(AiProviderKind kind, string? modelIdOnScreen)
    {
        var id = (modelIdOnScreen ?? "").Trim();
        return AiReasoning.Support(kind, id.Length > 0 ? id : DefaultModelOf(kind));
    }

    /// <summary>The row's description key: why the level cannot be taken, or what it does.</summary>
    public static string DescriptionKey(AiProviderKind kind, string? modelIdOnScreen)
        => UnsupportedKey(Support(kind, modelIdOnScreen))
           ?? (kind == AiProviderKind.OpenAiCompatible ? "Ai_EffortDesc_OpenAi" : "Ai_EffortDesc");

    /// <summary>The model an EMPTY box resolves to, as the provider resolves it.</summary>
    private static string DefaultModelOf(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Claude => ClaudeToneModel.DefaultModelId,
        AiProviderKind.ClaudeCli => ClaudeCliToneModel.DefaultModelId,
        AiProviderKind.CodexCli => CodexCliToneModel.DefaultModelId,
        _ => ""
    };
}

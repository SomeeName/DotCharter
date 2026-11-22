using DotCharter.Services.Ai.Chat;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The scope ONE request runs on instead of the pane's pick (a palette id's); never saved.</summary>
internal sealed class AiChatScopeOverride
{
    /// <summary>The override while a request's scopes are collected; null = the pane's pick.</summary>
    public AiChatScopeKind? Kind { get; private set; }

    /// <summary>Runs <paramref name="collect" /> with <paramref name="kind" /> as the scope, then clears it.</summary>
    public List<AiChatScope> Collect(AiChatScopeKind? kind, Func<List<AiChatScope>> collect)
    {
        Kind = kind;
        try
        {
            return collect();
        }
        finally
        {
            Kind = null;
        }
    }
}

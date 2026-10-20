using DotCharter.Localization;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.State;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using ToneChange = DotCharter.Core.Domain.Events.ToneChange;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>
/// The chat pane's Tones task scope line: what the scope covers as TONES — the tone names it
///     would generate for, or why none — and the cheap "is it another tone now" check the playhead case
///     runs on every tick.
/// </summary>
internal sealed class AiChatToneScope(ProjectState state, ILocalizationService localization)
{
    private ToneChange? _toneInForce;

    /// <summary>The scope line for <paramref name="kind" /> on <paramref name="arrangement" />.</summary>
    public string Summary(AiChatScopeKind kind, Arrangement? arrangement, IEnumerable<ToneChange> selected)
    {
        if (arrangement is null) return localization["AiChat_Summary_NoArrangement"];
        var scope = AiChatScope.Tones(kind, arrangement, selected, state.CurrentTimeMs, state.Beats);
        if (scope?.ToneChanges is not { } changes)
        {
            return localization[kind switch
            {
                AiChatScopeKind.Selection => "AiChat_Summary_NoToneSelected",
                AiChatScopeKind.Section => "AiChat_Summary_NoToneAtPlayhead",
                _ => "AiChat_Summary_NoTones"
            }];
        }

        var names = changes
            .Select(c => string.IsNullOrWhiteSpace(c.ToneName) ? localization["AiChat_ToneUnnamed"] : c.ToneName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return localization.Format(kind == AiChatScopeKind.Section ? "AiChat_Summary_Section" : "AiChat_Summary_Tones",
            string.Join(", ", names));
    }

    /// <summary>True when the tone change in force at the playhead is not the one the scope line last named.
    /// A loop over the placed changes, no allocation: it runs on every playhead tick.</summary>
    public bool InForceChanged(Arrangement? arrangement)
    {
        ToneChange? found = null;
        var foundMs = double.NegativeInfinity;
        if (arrangement is not null)
        {
            var changes = arrangement.ToneChanges;
            for (var i = 0; i < changes.Count; i++)
            {
                var ms = changes[i].GetTimeMs(state.Beats);
                if (ms > state.CurrentTimeMs || ms < foundMs) continue;
                found = changes[i];
                foundMs = ms;
            }
        }

        if (ReferenceEquals(found, _toneInForce)) return false;
        _toneInForce = found;
        return true;
    }
}

namespace DotCharter.Core.Domain.Project;

/// <summary>
/// Per-phrase metadata that mirrors rscharter's `Phrase{maxDifficulty, solo}`.
/// Stored as a sidecar map keyed by phrase name so existing string-based phrase
/// lists in DotCharter remain unchanged.
/// </summary>
public class PhraseInfo
{
    public int MaxDifficulty { get; set; }
    public bool Solo { get; set; }

    public PhraseInfo() { }

    public PhraseInfo(int maxDifficulty, bool solo)
    {
        MaxDifficulty = maxDifficulty;
        Solo = solo;
    }
}

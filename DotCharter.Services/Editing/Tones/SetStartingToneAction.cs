using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tones;

/// <summary>
///     Picks the tone an arrangement STARTS in (the Song tab's starting-tone chooser): writes
///     <see cref="Arrangement.StartingTone" /> alone, one undo node; undo puts the old key back. The
///     tone slots (Base, A–D) are the Tone Designer's and stay as they are.
/// </summary>
public sealed class SetStartingToneAction(Arrangement arrangement, string tone) : IChartAction, ILinkPolicyAction
{
    private readonly string _before = arrangement.StartingTone;
    private readonly string _after = tone.Trim();

    /// <summary>Arrangement metadata, not content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public bool HasEffect => !string.Equals(_before, _after, StringComparison.Ordinal);

    public void Execute() => arrangement.StartingTone = _after;

    public void Undo() => arrangement.StartingTone = _before;
}

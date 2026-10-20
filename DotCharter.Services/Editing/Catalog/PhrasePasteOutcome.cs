namespace DotCharter.Services.Editing.Catalog;

/// <summary>What a phrase-catalog paste attempt actually did.</summary>
public enum PhrasePasteResult
{
    /// <summary>Nothing to paste into — no focused or active arrangement.</summary>
    NoDestination,

    /// <summary>Pasted, and the phrase fits its destination exactly.</summary>
    Pasted,

    /// <summary>Pasted, but the phrase will not sound the way it was captured.
    /// The reasons are on <see cref="PhrasePasteOutcome.Check" />.</summary>
    PastedWithWarning,

    /// <summary>Refused: the paste would have written chart data the export gate
    /// rejects. Nothing was changed and no history entry was created.</summary>
    Blocked
}

/// <summary>
///     The result of one paste, with the compatibility findings that explain it.
///     Returned rather than thrown/logged-only so the surface that asked can say
///     why — a refused or degraded paste that reports nothing is the failure this
///     whole path exists to avoid.
/// </summary>
public sealed class PhrasePasteOutcome
{
    public static readonly PhrasePasteOutcome NoDestination = new(PhrasePasteResult.NoDestination, null);

    public PhrasePasteOutcome(PhrasePasteResult result, PhrasePasteCheck? check)
    {
        Result = result;
        Check = check;
    }

    public PhrasePasteResult Result { get; }

    public PhrasePasteCheck? Check { get; }

    public bool Pasted => Result is PhrasePasteResult.Pasted or PhrasePasteResult.PastedWithWarning;
}

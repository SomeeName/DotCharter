namespace DotCharter.Tone.Presentation.Shared;

/// <summary>
///     Collapses a BURST of requests into ONE pass: at most one request is in
///     flight, and it runs when the post drains — below input, so a burst of
///     keystrokes is processed first. No timer and no guessed delay.
/// </summary>
public class CoalescedPass
{
    private readonly Action _pass;
    private readonly Action<Action> _post;
    private readonly Action _run;

    public CoalescedPass(Action pass, Action<Action> post)
    {
        _pass = pass;
        _post = post;
        _run = Run;
    }

    public int RequestCount { get; private set; }

    public int PassCount { get; private set; }

    public bool IsPending { get; private set; }

    /// <summary>Asks for a pass; a second request while one is pending is free.</summary>
    public void Request()
    {
        RequestCount++;
        if (IsPending) return;
        IsPending = true;
        _post(_run);
    }

    /// <summary>Runs a pass NOW and drops any pending one — for a single discrete gesture rather than a burst.</summary>
    public void RunImmediately()
    {
        RequestCount++;
        IsPending = false;
        PassCount++;
        _pass();
    }

    public void Cancel() => IsPending = false;

    private void Run()
    {
        if (!IsPending) return;
        IsPending = false;
        PassCount++;
        _pass();
    }
}

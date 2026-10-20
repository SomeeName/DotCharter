namespace DotCharter.UI.ViewModels.Onboarding;

/// <summary>The pages of the first-run wizard, in the order the user meets them.</summary>
public enum OnboardingStep
{
    /// <summary>Welcome + role choice (Charter / Player), language, update opt-in.</summary>
    Welcome,
    /// <summary>The PSARC / song library folder.</summary>
    Library,
    /// <summary>Output device, sample rate, buffer — with a test click.</summary>
    Audio,
    /// <summary>Guitar input device / channel with a live meter.</summary>
    Input,
    /// <summary>A starter tone from the preset library.</summary>
    Tone,
    /// <summary>Latency / A-V calibration.</summary>
    Latency,
    /// <summary>Create or pick a player profile.</summary>
    Profile,
    /// <summary>Summary of what was set.</summary>
    Done
}

/// <summary>
///     The pure step state machine behind <see cref="OnboardingWizardViewModel"/>:
///     which page is current, what Next / Back / Skip do at each edge, and which
///     pages were skipped rather than committed. No services, no clock, no
///     Avalonia — the view model owns the side effects and asks this for the
///     navigation answer, which is what keeps every transition assertable in the
///     default gate.
/// </summary>
public sealed class OnboardingStepMachine
{
    /// <summary>Every step, in wizard order. The enum's declaration order IS the
    /// order — this array exists so a reorder is one edit and a test can pin it.</summary>
    public static readonly IReadOnlyList<OnboardingStep> Order =
    [
        OnboardingStep.Welcome,
        OnboardingStep.Library,
        OnboardingStep.Audio,
        OnboardingStep.Input,
        OnboardingStep.Tone,
        OnboardingStep.Latency,
        OnboardingStep.Profile,
        OnboardingStep.Done
    ];

    private readonly HashSet<OnboardingStep> _skipped = [];

    /// <param name="includeLibrary">False in a build that cannot read game packages:
    /// the Library page asks for a folder of them, so it is ABSENT from <see cref="Steps"/>.</param>
    public OnboardingStepMachine(bool includeLibrary = true) =>
        Steps = includeLibrary ? Order : [.. Order.Where(s => s != OnboardingStep.Library)];

    /// <summary>The steps THIS run walks: <see cref="Order"/>, minus the ones the build leaves out.</summary>
    public IReadOnlyList<OnboardingStep> Steps { get; }

    public OnboardingStep Current { get; private set; } = OnboardingStep.Welcome;

    /// <summary>Zero-based position of <see cref="Current"/> in <see cref="Order"/>.</summary>
    public int Index => IndexOf(Current);

    public int Count => Steps.Count;

    public bool IsFirst => Index == 0;

    public bool IsLast => Index == Count - 1;

    /// <summary>The steps the user chose to skip during THIS run (Next and Back
    /// clear a step from the set; a completed run resets it).</summary>
    public IReadOnlySet<OnboardingStep> Skipped => _skipped;

    /// <summary>Back to the first page with nothing skipped — what "Run setup
    /// again" needs, so a second run never inherits the first one's skips.</summary>
    public void Reset()
    {
        Current = OnboardingStep.Welcome;
        _skipped.Clear();
    }

    /// <summary>Advances after the current step was committed. False on the last
    /// page (Finish is the view model's, not a navigation).</summary>
    public bool Next()
    {
        if (IsLast) return false;
        _skipped.Remove(Current);
        Current = Steps[Index + 1];
        return true;
    }

    /// <summary>Advances WITHOUT committing, recording the skip. The Done page
    /// cannot be skipped — there is nothing after it.</summary>
    public bool Skip()
    {
        if (IsLast) return false;
        _skipped.Add(Current);
        Current = Steps[Index + 1];
        return true;
    }

    /// <summary>Returns to the previous page. False on the first page.</summary>
    public bool Back()
    {
        if (IsFirst) return false;
        Current = Steps[Index - 1];
        // Coming back onto a page un-skips it: the user is looking at it again
        // and will leave it through Next or Skip like any other page.
        _skipped.Remove(Current);
        return true;
    }

    private int IndexOf(OnboardingStep step)
    {
        for (var i = 0; i < Steps.Count; i++)
            if (Steps[i] == step) return i;
        return 0;
    }
}

namespace DotCharter.Services.Ai.Chart;

/// <summary>
/// Which hand-position/fingering job the AI chat starts on, and the measured reason
///     it prints. The comparison (four held-out arrangements; algorithm vs Sonnet 5.5,
///     Haiku 5.5, Opus 5.5 and GPT 6.1 Sol, one run each at the provider's default reasoning):
///     <list type="table">
///         <item>guitar FHP: algorithm 95.7 %, best model 96.6 % — a wash;</item>
///         <item>guitar fingering: algorithm 67.1 %, best model 96.1 % — where a model helps;</item>
///         <item>bass FHP: algorithm 79.5 %, best model 77.7 % — every model worse.</item>
///     </list>
///     So the default is <see cref="AiChartJob.FingeringOnly" /> on guitar AND bass: the anchors
///     stay the algorithm's, and the model's FHP is an explicit opt-in. The charter's own pick
///     wins and is remembered per arrangement type (<see cref="AppConfig.AiFingeringJobGuitar" />,
///     <see cref="AppConfig.AiFingeringJobBass" />). The numbers live in the reason strings
///     (<c>AiChat_JobNote_*</c>); re-measure with the reasoning setting before changing a default.
/// </summary>
public static class AiFingeringDefaults
{
    /// <summary>The job both arrangement types start on until the charter picks one.</summary>
    public const AiChartJob Measured = AiChartJob.FingeringOnly;

    /// <summary>True for the three jobs that touch anchors and/or fingers — the ones this
    /// default and the remembered pick apply to.</summary>
    public static bool IsFingeringJob(AiChartJob job)
        => job is AiChartJob.FhpAndFingering or AiChartJob.FhpOnly or AiChartJob.FingeringOnly;

    /// <summary>A bass arrangement, by its type — the measurement split guitar and bass on it.</summary>
    public static bool IsBass(Arrangement? arrangement) => arrangement?.ArrangementType == ArrangementType.Bass;

    /// <summary>The job to start on: the remembered pick for this arrangement type, else the
    /// measured default.</summary>
    public static AiChartJob For(AppConfig config, bool isBass)
        => Remembered(config, isBass) is { } job && IsFingeringJob(job) ? job : Measured;

    /// <summary>Keeps an explicit pick for this arrangement type. Returns false (and writes
    /// nothing) for a job this default does not cover, or one already remembered.</summary>
    public static bool Remember(AppConfig config, bool isBass, AiChartJob job)
    {
        if (!IsFingeringJob(job) || Remembered(config, isBass) == job) return false;
        if (isBass) config.AiFingeringJobBass = job;
        else config.AiFingeringJobGuitar = job;
        return true;
    }

    /// <summary>The resx key of the line under the chat's pickers that says why: the measured
    /// reason for the default, or what the opt-in trades. Null for a job it does not cover.</summary>
    public static string? NoteKey(AiChartJob job, bool isBass) => job switch
    {
        AiChartJob.FingeringOnly => isBass ? "AiChat_JobNote_Bass" : "AiChat_JobNote_Guitar",
        AiChartJob.FhpAndFingering or AiChartJob.FhpOnly => isBass ? "AiChat_JobNote_BassFhp" : "AiChat_JobNote_GuitarFhp",
        _ => null
    };

    private static AiChartJob? Remembered(AppConfig config, bool isBass)
        => isBass ? config.AiFingeringJobBass : config.AiFingeringJobGuitar;
}

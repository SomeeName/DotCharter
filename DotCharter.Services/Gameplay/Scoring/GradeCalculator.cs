namespace DotCharter.Services.Gameplay.Scoring;

/// <summary>Letter grade for a finished play-through, best-first.</summary>
public enum LetterGrade
{
    F = 0,
    D = 1,
    C = 2,
    B = 3,
    A = 4,
    S = 5
}

/// <summary>
///     Accuracy → letter grade. Accuracy alone decides the grade — coverage
///     and speed already feed the separate mastery metric, and a grade the
///     player can't reason about ("why B at 96%?") is worse than a simple
///     ladder.
/// </summary>
public static class GradeCalculator
{
    public static LetterGrade FromAccuracy(double accuracy)
    {
        return accuracy switch
        {
            >= 0.99 => LetterGrade.S,
            >= 0.95 => LetterGrade.A,
            >= 0.90 => LetterGrade.B,
            >= 0.80 => LetterGrade.C,
            >= 0.60 => LetterGrade.D,
            _ => LetterGrade.F
        };
    }

    public static string Label(LetterGrade grade) => grade.ToString();
}

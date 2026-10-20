namespace DotCharter.Services.State;

public interface IChartAction
{
    bool HasEffect => true;

    /// <summary>The managed memory this action keeps alive while it sits in the history, for the
    /// history's byte budget. Zero for an ordinary edit, whose node cap is budget enough; a
    /// whole-project snapshot reports its two clones (<c>ChartProjectFootprint</c>).</summary>
    long EstimatedBytes => 0;

    void Execute();
    void Undo();
}

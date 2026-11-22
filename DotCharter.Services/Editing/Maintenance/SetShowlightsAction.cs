using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>Stores a show-light set into the project (the export dialog's "Generate show lights"); undo puts the previous set back.</summary>
public sealed class SetShowlightsAction(ChartProject project, IReadOnlyList<Showlight> lights) : IChartAction, ILinkPolicyAction
{
    private Showlight[] _before = [];

    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public bool HasEffect => lights.Count > 0;

    public void Execute()
    {
        _before = [.. project.Showlights];
        project.Showlights = [.. lights];
    }

    public void Undo() => project.Showlights = [.. _before];
}

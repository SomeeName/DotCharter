using DotCharter.Services.State;

namespace DotCharter.Services.Parsing.Xml;

/// <summary>Appends one prepared XML import as one undo step, retaining the imported objects for redo.</summary>
public sealed class ImportRsXmlAction : IChartAction, ILinkPolicyAction
{
    private readonly ChartProject _host;
    private readonly RsXmlImportPlan _plan;
    private readonly Action _changed;
    private readonly bool _adoptBeats;
    private RsXmlImportResult? _result;

    public ImportRsXmlAction(ChartProject host, RsXmlImportPlan plan, Action changed)
    {
        _host = host;
        _plan = plan;
        _changed = changed;
        EstimatedBytes = EstimateImportedGraph(plan);
        _adoptBeats = plan.Kind == RsXmlImportKind.Song && plan.Arrangement != null && host.Beats.Count == 0;
    }

    // The plan retains every prepared object, even beats a populated host does not adopt.
    private static long EstimateImportedGraph(RsXmlImportPlan plan)
    {
        var imported = new ChartProject();
        if (plan.Arrangement != null) imported.Arrangements.Add(plan.Arrangement);
        if (plan.VocalTrack != null) imported.VocalTracks.Add(plan.VocalTrack);
        foreach (var beat in plan.Beats) imported.Beats.Add(beat);
        foreach (var light in plan.Showlights) imported.Showlights.Add(light);
        return ChartProjectFootprint.EstimateBytes(imported);
    }

    public long EstimatedBytes { get; }
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;
    public bool HasEffect => _plan.Kind switch
    {
        RsXmlImportKind.Song => _plan.Arrangement != null,
        RsXmlImportKind.Vocals => _plan.VocalTrack != null,
        RsXmlImportKind.Showlights => _plan.Showlights.Count > 0,
        _ => false
    };

    public RsXmlImportResult Result => _result ?? new RsXmlImportResult { Kind = _plan.Kind };

    public void Execute()
    {
        if (_result == null)
        {
            _result = new RsXmlImporter().Apply(_host, _plan);
        }
        else
        {
            if (_adoptBeats)
            {
                foreach (var beat in _plan.Beats) _host.Beats.Add(beat);
            }

            switch (_plan.Kind)
            {
                case RsXmlImportKind.Song:
                    _host.Arrangements.Add(_plan.Arrangement!);
                    break;
                case RsXmlImportKind.Vocals:
                    _host.VocalTracks.Add(_plan.VocalTrack!);
                    break;
                case RsXmlImportKind.Showlights:
                    foreach (var light in _plan.Showlights) _host.Showlights.Add(light);
                    break;
            }
        }

        _changed();
    }

    public void Undo()
    {
        switch (_plan.Kind)
        {
            case RsXmlImportKind.Song:
                _host.Arrangements.Remove(_plan.Arrangement!);
                // Adoption starts from an empty map; beat scaling can replace every imported instance.
                if (_adoptBeats) _host.Beats.Clear();

                break;
            case RsXmlImportKind.Vocals:
                _host.VocalTracks.Remove(_plan.VocalTrack!);
                break;
            case RsXmlImportKind.Showlights:
                foreach (var light in _plan.Showlights) _host.Showlights.Remove(light);
                break;
        }

        _changed();
    }
}

using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

public class SetVocalPhrasesAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly ObservableCollection<LyricPhrase> _target;
    private readonly List<LyricPhrase> _newPhrases;
    private readonly List<LyricPhrase> _oldPhrases;

    public SetVocalPhrasesAction(ObservableCollection<LyricPhrase> target, IEnumerable<LyricPhrase> newPhrases)
    {
        _target = target;
        _newPhrases = newPhrases.ToList();
        _oldPhrases = target.ToList();
    }

    public bool HasEffect => _newPhrases.Count > 0 || _oldPhrases.Count > 0;

    public void Execute()
    {
        _target.Clear();
        foreach (var p in _newPhrases) _target.Add(p);
    }

    public void Undo()
    {
        _target.Clear();
        foreach (var p in _oldPhrases) _target.Add(p);
    }
}

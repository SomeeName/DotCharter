using System.ComponentModel;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
///     The pieces more than one redesigned Play Mode flow screen needs.
///     <para>
///         They live in one file because the alternative is nine copies of
///         "a label and the verb that picks it". Anything only ONE screen shows
///         stays in that screen's own contract.
///     </para>
/// </summary>
public interface IChoiceOption
{
    /// <summary>What the row or combo shows. Already formatted.</summary>
    string Label { get; }
}

/// <summary>A caption over one already-formatted figure — the vocabulary every
/// stat block on these screens uses.</summary>
public interface IFactTile : INotifyPropertyChanged
{
    /// <summary>The mono caps caption ("LIBRARY").</summary>
    string Caption { get; }

    /// <summary>The figure, already formatted ("2,160").</summary>
    string Value { get; }

    /// <summary>True when the figure is a GOOD state — the only thing that may tint it.</summary>
    bool IsReady { get; }

    /// <summary>True when the figure is a problem the player should look at.</summary>
    bool IsWarning { get; }
}

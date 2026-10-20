using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Gameplay.Setlists;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>One setlist in Song Select's picker: the stored list, and its name as
/// the picker shows it. A rename raises <see cref="Name" />, so the picker's row
/// follows without the list being replaced.</summary>
public sealed class PlaySetlistOption(PlaySetlist setlist) : ObservableObject, ISetlistOption
{
    /// <summary>The stored setlist this row stands for.</summary>
    public PlaySetlist Setlist { get; } = setlist;

    public string Name => Setlist.Name;

    public void Rename(string name)
    {
        if (Setlist.Name == name) return;
        Setlist.Name = name;
        OnPropertyChanged(nameof(Name));
    }

    /// <summary>What a screen reader announces for the picker's row.</summary>
    public override string ToString() => Name;
}

/// <summary>One song of the picked setlist as Song Select lists it.</summary>
public sealed class PlaySetlistEntryRow : ObservableObject, ISetlistEntryRow
{
    private PlaySetlistEntryRow(int index, string title, string detail)
    {
        Index = index;
        Number = (index + 1).ToString(CultureInfo.CurrentCulture);
        Title = title;
        Detail = detail;
    }

    /// <summary>Its place in the stored list, from 0.</summary>
    public int Index { get; }

    public string Number { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public static PlaySetlistEntryRow From(int index, PlaySetlistEntry entry)
    {
        var parts = new List<string>(3);
        if (entry.Artist.Length > 0) parts.Add(entry.Artist);
        if (entry.Arrangement.Length > 0) parts.Add(entry.Arrangement);
        if (entry.EffectiveSpeedPercent < PlaySetlistEntry.MaxSpeedPercent)
            parts.Add(PercentText.Format(entry.EffectiveSpeedPercent / 100.0, 0));
        return new PlaySetlistEntryRow(index, entry.Title, string.Join(" · ", parts));
    }

    /// <summary>What a screen reader announces for the row.</summary>
    public override string ToString() => Detail.Length == 0 ? Title : $"{Title}, {Detail}";
}

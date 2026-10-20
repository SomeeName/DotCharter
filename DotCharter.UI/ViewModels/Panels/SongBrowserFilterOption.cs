namespace DotCharter.UI.ViewModels.Panels;

/// <summary>One entry of a browser filter dropdown: what it prints and the raw
/// value the filter predicate matches. Built from the SCANNED library, never
/// from a fixed list — a tuning the library does not contain must not be
/// offerable.</summary>
public sealed record SongBrowserFilterOption(string Label, string Value);

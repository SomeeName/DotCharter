namespace DotCharter.Services.Editing.Tracks;

/// <summary>Which editing surface of a track has keyboard focus: the note
/// highway or the timeline's event rows. Clipboard and delete verbs branch on it.</summary>
public enum EditorFocusArea
{
    NoteHighway,
    TimelineEvents
}

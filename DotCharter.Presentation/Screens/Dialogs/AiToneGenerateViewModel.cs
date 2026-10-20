using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
/// Tools ▸ Generate tones with AI…: asks for the charter's own words and answers them.
///     One instance per open; the text lives in <see cref="DialogMemory" /> for the open project.
/// </summary>
public sealed class AiToneGenerateViewModel : DialogViewModel<string?>, IAiToneGenerateScreen
{
    private readonly DialogMemory _memory;
    private readonly ProjectState _state;
    private string _notes;

    public AiToneGenerateViewModel(DialogMemory memory, ProjectState state)
    {
        _memory = memory;
        _state = state;
        _notes = state.ActiveProject is { } project && ReferenceEquals(project, memory.AiToneNotesProject)
            ? memory.AiToneNotes
            : "";
        GenerateCommand = new RelayCommand(Generate);
        CancelCommand = new RelayCommand(() => Close(null));
    }

    public string Notes
    {
        get => _notes;
        set
        {
            if (!SetProperty(ref _notes, value ?? "")) return;
            OnPropertyChanged(nameof(LengthText));
        }
    }

    public int MaxLength => AiToneContextText.MaxLength;

    public string LengthText => string.Create(CultureInfo.InvariantCulture, $"{_notes.Length} / {MaxLength}");

    public ICommand GenerateCommand { get; }

    public ICommand CancelCommand { get; }

    private void Generate()
    {
        var text = AiToneContextText.Normalize(_notes) ?? "";
        _memory.AiToneNotes = text;
        _memory.AiToneNotesProject = _state.ActiveProject;
        Close(text);
    }
}

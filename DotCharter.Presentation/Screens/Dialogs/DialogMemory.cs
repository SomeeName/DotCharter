using DotCharter.Core.Domain.Project;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>What a hosted dialog keeps between opens, since its view model lives only while it is open.</summary>
public sealed class DialogMemory
{
    public PasteSpecialMode PasteMode { get; set; } = PasteSpecialMode.Preserve;

    public bool PasteAtMouse { get; set; }

    /// <summary>Chart from audio's WAV, valid only for <see cref="AudioPathProject" />: a path never outlives its song.</summary>
    public string AudioPath { get; set; } = "";

    public ChartProject? AudioPathProject { get; set; }

    /// <summary>Generate tones with AI's text, for <see cref="AiToneNotesProject" /> only: a reopen keeps it, another project starts empty.</summary>
    public string AiToneNotes { get; set; } = "";

    public ChartProject? AiToneNotesProject
    {
        get => _aiToneNotesProject is not null && _aiToneNotesProject.TryGetTarget(out var p) ? p : null;
        set => _aiToneNotesProject = value is null ? null : new WeakReference<ChartProject>(value);
    }

    private WeakReference<ChartProject>? _aiToneNotesProject;   // weak: a closed project is not kept alive

    public string LintFolderPath { get; set; } = "";

    /// <summary>Batch lint's auto-fix choice. Its overwrite opt-in is deliberately NOT kept: that one is chosen per open.</summary>
    public bool LintApplyAutoFixes { get; set; }

    /// <summary>The export dialog's fields, for <see cref="ExportDraftProject" /> only: a reopen keeps them, another project resets them.</summary>
    public ExportDraft? ExportDraft { get; set; }

    public ChartProject? ExportDraftProject
    {
        get => _exportDraftProject is not null && _exportDraftProject.TryGetTarget(out var p) ? p : null;
        set => _exportDraftProject = value is null ? null : new WeakReference<ChartProject>(value);
    }

    private WeakReference<ChartProject>? _exportDraftProject;

    /// <summary>What the Tone designer imported from another package, for <see cref="ImportedTonesProject" /> only.</summary>
    public List<ToneDefinition> ImportedTones { get; } = [];

    public ChartProject? ImportedTonesProject
    {
        get => _importedTonesProject is not null && _importedTonesProject.TryGetTarget(out var p) ? p : null;
        set => _importedTonesProject = value is null ? null : new WeakReference<ChartProject>(value);
    }

    private WeakReference<ChartProject>? _importedTonesProject;   // weak: a closed project is not kept alive

    public IReadOnlyDictionary<string, string> ImportedToneBodiesFor(ChartProject project) =>
        ReferenceEquals(project, ImportedTonesProject)
            ? ImportedTones.ToDictionary(t => t.Key, t => t.Json, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>What the export dialog had on screen when it closed; the arrangement ticks are keyed by name.</summary>
public sealed class ExportDraft
{
    public required string DlcKey { get; init; }
    public required string AppId { get; init; }
    public required string AudioPath { get; init; }
    public required string PreviewAudioPath { get; init; }
    public required string AlbumArtPath { get; init; }
    public required string OutputPath { get; init; }
    public required double AudioVolume { get; init; }
    public required double PreviewVolume { get; init; }
    public required bool IncludeVocals { get; init; }
    public bool SplitExtendedStringChords { get; init; }
    public required IReadOnlyDictionary<string, bool> Ticks { get; init; }
}

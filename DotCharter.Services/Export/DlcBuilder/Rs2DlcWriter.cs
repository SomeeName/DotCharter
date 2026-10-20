using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>Serializes a <see cref="Rs2DlcProjectDto" /> the way DLC Builder does.</summary>
public static class Rs2DlcWriter
{
    /// <summary>
    ///     Upstream is <c>FSharpJsonOptions.Create(indent = true, ignoreNull = true)</c>.
    /// </summary>
    /// <remarks>
    ///     NO <c>JsonStringEnumConverter</c> and NO naming policy: the file uses the
    ///     declared PascalCase names and writes enums as numbers. Adding either
    ///     produces a file DLC Builder silently reads back as defaults.
    /// </remarks>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(Rs2DlcProjectDto project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, Options);
    }

    /// <summary>
    ///     Writes the project file, creating the folder if needed.
    ///     <para>
    ///         Through <see cref="AtomicFile" />, for the same reason as the
    ///         <c>.eof</c>: the <c>.rs2dlc</c> is <b>somebody else's project
    ///         file</b> — <c>Export/CompanionOwnership</c> exists precisely
    ///         because a charter edits it in DLC Builder — and it is rewritten
    ///         inside the worker of every project save. The ownership stamp
    ///         stops us overwriting a file we do not own; it does nothing about
    ///         a bare <c>File.WriteAllText</c> TRUNCATING one we do, so a crash
    ///         in that window emptied it. AtomicFile also creates the directory,
    ///         so no explicit CreateDirectory is needed.
    ///     </para>
    /// </summary>
    public static void WriteTo(string path, Rs2DlcProjectDto project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        AtomicFile.WriteAllText(path, Serialize(project));
    }
}

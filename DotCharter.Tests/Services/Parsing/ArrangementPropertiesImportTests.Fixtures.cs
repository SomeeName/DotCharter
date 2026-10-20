using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
///     Picked bass and the arrangement's route are charter settings no note data
///     encodes, so an importer that guesses them is not lossy — it is WRONG. Both
///     were: the SNG mapper set <c>PickedBass</c> to "is this a bass arrangement",
///     and the RS-XML reader left <c>bassPick</c> in <c>Properties</c> without ever
///     restoring the field that the writer and the psarc manifest actually read.
/// </summary>
public sealed partial class ArrangementPropertiesImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_arrprops_import_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static string ManifestJson(string body) => $$"""
        {
          "Entries": {
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA": {
              "Attributes": {
                "ArrangementName": "Bass",
                "SongKey": "Test",
                "PersistentID": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                {{body}}
              }
            }
          }
        }
        """;

    private const string PickedProps =
        """
        "ArrangementProperties": { "routeMask": 4, "bassPick": 1 }
        """;

    private const string FingeredProps =
        """
        "ArrangementProperties": { "routeMask": 4, "bassPick": 0 }
        """;

    private const string HeaderPicked =
        """
        "RouteMask": 4, "BassPick": 1
        """;

    private const string HeaderOnly =
        """
        "RouteMask": 4
        """;
}

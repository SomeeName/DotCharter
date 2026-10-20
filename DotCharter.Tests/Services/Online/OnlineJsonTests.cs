using System.Text.Json;
using DotCharter.Tone.Online;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     <para>
///         Pins the tolerant-reader contract that four HTTP clients now share
///         instead of each carrying a private copy of it. The whole point of
///         these readers is that a malformed, missing or wrong-typed property
///         DEGRADES the readout rather than faulting the call, so every case
///         below is a "server sent something unexpected" case.
///     </para>
///     <para>
///         Also lints that the bank stays shared: the ~60-line block lived in
///         two files verbatim and <c>ExtractErrorMessage</c> in four until
///         2026-08-28.
///     </para>
/// </summary>
public class OnlineJsonTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ReadersFallBackRatherThanThrowOnAWrongShape()
    {
        var row = Parse("""
                        { "name": "Ada", "blank": "", "score": 42, "big": 9007199254740993,
                          "ratio": 0.5, "when": "2026-08-28T12:00:00", "flag": true,
                          "wrongType": [1,2] }
                        """);

        Assert.Equal("Ada", OnlineJson.GetString(row, "name"));
        Assert.Equal("?", OnlineJson.GetString(row, "missing", "?"));
        Assert.Equal("?", OnlineJson.GetString(row, "blank", "?"));
        Assert.Equal("?", OnlineJson.GetString(row, "wrongType", "?"));

        Assert.Equal(42, OnlineJson.GetInt(row, "score"));
        Assert.Equal(-1, OnlineJson.GetInt(row, "missing", -1));
        Assert.Equal(-1, OnlineJson.GetInt(row, "wrongType", -1));
        Assert.Equal(-1, OnlineJson.GetInt(row, "big", -1)); // does not fit an int

        Assert.Equal(9007199254740993L, OnlineJson.GetLong(row, "big"));
        Assert.Equal(0L, OnlineJson.GetLong(row, "missing"));

        Assert.Equal(0.5, OnlineJson.GetDouble(row, "ratio"));
        Assert.Equal(0, OnlineJson.GetDouble(row, "missing"));

        Assert.True(OnlineJson.GetBool(row, "flag"));
        Assert.False(OnlineJson.GetBool(row, "missing"));
        Assert.False(OnlineJson.GetBool(row, "wrongType"));
    }

    /// <summary>
    ///     A naive server timestamp is ASSUMED UTC and normalized — reading it as
    ///     local would shift every leaderboard row by the viewer's offset.
    /// </summary>
    [Fact]
    public void GetUtcAssumesUtcAndFallsBackToMinValue()
    {
        var row = Parse("""{ "naive": "2026-08-28T12:00:00", "offset": "2026-08-28T14:00:00+02:00", "junk": "nope" }""");

        var naive = OnlineJson.GetUtc(row, "naive");
        Assert.Equal(DateTimeKind.Utc, naive.Kind);
        Assert.Equal(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc), naive);
        Assert.Equal(naive, OnlineJson.GetUtc(row, "offset"));

        Assert.Equal(DateTime.MinValue, OnlineJson.GetUtc(row, "junk"));
        Assert.Equal(DateTime.MinValue, OnlineJson.GetUtc(row, "missing"));
    }

    /// <summary>
    ///     The default key order is the TONE3000-style one; a server with a
    ///     different set passes its own, which is the reason the keys are a
    ///     parameter at all.
    /// </summary>
    [Fact]
    public void ExtractErrorMessageHonoursKeyPreferenceOrder()
    {
        const string body = """{ "error": "generic", "error_description": "the real one", "message": "" }""";

        Assert.Equal("generic", OnlineJson.ExtractErrorMessage(body));
        Assert.Equal(
            "the real one",
            OnlineJson.ExtractErrorMessage(body, "error_description", "msg", "message", "hint", "error"));
    }

    /// <summary>
    ///     A proxy's HTML error page, an empty body and a JSON array all have to
    ///     yield empty so the caller falls back to its status-code wording — this
    ///     is the branch that would otherwise surface a parser exception to a user.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("[\"error\"]")]
    [InlineData("{ \"error\": \"  \" }")]
    [InlineData("{ \"error\": 500 }")]
    public void ExtractErrorMessageYieldsEmptyRatherThanThrowing(string? body)
        => Assert.Equal(string.Empty, OnlineJson.ExtractErrorMessage(body));

    /// <summary>
    ///     Fails if a client re-grows its own copy of the bank. A private copy
    ///     compiles clean and breaks nothing — it just becomes the place a fix
    ///     lands while the other three keep the bug.
    /// </summary>
    [Fact]
    public void NoOnlineClientKeepsAPrivateCopyOfTheReaders()
    {
        var dir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Online");
        Assert.True(Directory.Exists(dir), "Online/ moved. Repoint this lint.");

        string[] banned =
        [
            "private static string GetString(JsonElement",
            "private static bool GetBool(JsonElement",
            "private static int GetInt(JsonElement",
            "private static long GetLong(JsonElement",
            "private static double GetDouble(JsonElement",
            "private static DateTime GetUtc(JsonElement"
        ];

        // Tone3000Json's readers take `(JsonElement, T fallback, params string[]
        // names)` — they try SEVERAL property names for one field, because that
        // API renamed fields between versions. That is a different reader, not a
        // copy of this one. Adding a file here to turn a red build green without
        // checking that its readers really are different defeats the lint.
        string[] allowed = ["Tone3000Json.cs"];

        var offenders = new List<string>();
        // OnlineJson and the TONE3000 client live in DotCharter.Tone/Online.
        var tone = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Online");
        Assert.True(Directory.Exists(tone), "DotCharter.Tone/Online moved. Repoint this lint.");
        foreach (var file in new[] { dir, tone }
                     .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories)))
        {
            if (Path.GetFileName(file) == "OnlineJson.cs") continue;
            if (allowed.Contains(Path.GetFileName(file))) continue;
            var src = File.ReadAllText(file);
            foreach (var sig in banned)
            {
                if (src.Contains(sig, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {sig}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Call OnlineJson instead of copying it. Found: " + string.Join("; ", offenders));
    }
}

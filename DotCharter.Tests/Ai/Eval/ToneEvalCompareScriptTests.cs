using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The model-comparison harness (<c>tools/ai-eval/Compare-Models.ps1</c> and
///     its module <c>CompareModels.psm1</c>), exercised in the DEFAULT GATE.
///     <para>
///         The pure half of that harness — the OpenRouter catalogue parser, the
///         model-id resolver, the cost estimate, the ranking and the Markdown
///         writer — is executed HERE, through <c>pwsh</c>, over canned inputs and
///         over a REAL <c>results.json</c> produced by the offline fake. That is
///         the point: a comparison script first exercised on the day it is
///         spending money across thirteen models is a script nobody has tested.
///         Re-implementing its logic in C# to test it would only test the copy.
///     </para>
///     <para>
///         <c>pwsh</c> missing is an <c>Assert.Skip</c>, never a failure — the
///         same rule every other environment-dependent harness here follows.
///     </para>
/// </summary>
public sealed class ToneEvalCompareScriptTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-ai-compare-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ToneEvalCompareScriptTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    // --------------------------------------------------------- catalogue + ids

    [Fact]
    public void TheCatalogueParser_ConvertsPerTokenPricesToPerMillion()
    {
        var catalog = WriteCatalog();

        var json = Pwsh($$"""
            $catalog = ConvertFrom-OpenRouterCatalog (Get-Content -Raw '{{catalog}}' | ConvertFrom-Json)
            $catalog['anthropic/claude-opus-5'] | ConvertTo-Json -Depth 4
            """);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("anthropic/claude-opus-5", root.GetProperty("Id").GetString());

        // The endpoint answers in USD per TOKEN as a string; every other price in
        // this repo is per MILLION tokens, and mixing the two is a 1e6 error in the
        // one column a comparison exists to produce.
        Assert.Equal(5.0, root.GetProperty("InUsdPerMTok").GetDouble(), 6);
        Assert.Equal(25.0, root.GetProperty("OutUsdPerMTok").GetDouble(), 6);
    }

    [Fact]
    public void TheResolver_KeepsAKnownId_RenamesAnAlias_AndDropsAGoneOne()
    {
        var catalog = WriteCatalog();

        var json = Pwsh($$"""
            $catalog = ConvertFrom-OpenRouterCatalog (Get-Content -Raw '{{catalog}}' | ConvertFrom-Json)
            Resolve-ToneEvalModel -Requested @(
                'anthropic/claude-opus-5',
                'mistralai/mistral-large-3',
                'anthropic/claude-opus-4') -Catalog $catalog |
                ConvertTo-Json -Depth 4 -AsArray
            """);

        var rows = JsonDocument.Parse(json).RootElement;
        Assert.Equal(3, rows.GetArrayLength());

        Assert.Equal("ok", rows[0].GetProperty("Status").GetString());
        Assert.Equal("anthropic/claude-opus-5", rows[0].GetProperty("Id").GetString());

        // The alias table, not a guess — and the price that comes back with it is
        // Large 3's, which is how the sweep prices it.
        Assert.Equal("renamed", rows[1].GetProperty("Status").GetString());
        Assert.Equal("mistralai/mistral-large-2512", rows[1].GetProperty("Id").GetString());
        Assert.Equal(0.5, rows[1].GetProperty("InUsdPerMTok").GetDouble(), 6);

        // Gone: dropped, with the ids that DO exist for that vendor, so the owner
        // can fix the list without opening a browser.
        Assert.Equal("missing", rows[2].GetProperty("Status").GetString());
        Assert.True(rows[2].GetProperty("Id").ValueKind == JsonValueKind.Null);
        Assert.Contains("anthropic/claude-opus-5", rows[2].GetProperty("Note").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheResolver_NeverGuessesARename()
    {
        // The non-vacuity case, and a real one: 'mistralai/mistral-large-3' resolved
        // by stripping its version would land on 'mistralai/mistral-large' — the
        // 2411 model at $2.00/$6.00 instead of Large 3 at $0.50/$1.50. A 4x price
        // error, silently, in the exact column this harness exists to produce. So an
        // id that is not in the catalogue and not in the alias table is DROPPED.
        var catalog = WriteCatalog(includeLarge3: false);

        var json = Pwsh($$"""
            $catalog = ConvertFrom-OpenRouterCatalog (Get-Content -Raw '{{catalog}}' | ConvertFrom-Json)
            Resolve-ToneEvalModel -Requested @('mistralai/mistral-large-3') -Catalog $catalog |
                ConvertTo-Json -Depth 4 -AsArray
            """);

        var row = JsonDocument.Parse(json).RootElement[0];
        Assert.Equal("missing", row.GetProperty("Status").GetString());
        Assert.Contains("mistralai/mistral-large", row.GetProperty("Note").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheEstimate_PricesEveryModelBeforeAnythingIsSpent()
    {
        var catalog = WriteCatalog();

        var json = Pwsh($$"""
            $catalog = ConvertFrom-OpenRouterCatalog (Get-Content -Raw '{{catalog}}' | ConvertFrom-Json)
            $models = Resolve-ToneEvalModel -Requested @('anthropic/claude-opus-5') -Catalog $catalog
            Get-ToneEvalCostEstimate -Models $models -Calls 35 | ConvertTo-Json -Depth 4 -AsArray
            """);

        var row = JsonDocument.Parse(json).RootElement[0];

        // 35 calls at the eval's own assumption of 6,000 in / 900 out, at $5/$25.
        var expected = 35 * (6_000 / 1e6 * 5.0 + 900 / 1e6 * 25.0);
        Assert.Equal(expected, row.GetProperty("EstimateUsd").GetDouble(), 6);
        Assert.Equal(1.8375, row.GetProperty("EstimateUsd").GetDouble(), 4);
    }

    // ------------------------------------------------------- the artifact line

    [Fact]
    public void TheArtifactLine_IsExactlyWhatTheScriptParsesBack()
    {
        // Both halves of the contract in one case: the producer is
        // ToneEvalConsoleLines (C#), the parser is Get-ToneEvalArtifactDirectory
        // (PowerShell), and neither is allowed to drift from the other.
        var directory = Path.Combine(_scratch, "20260912-120000");
        var console = string.Join("\n",
            "[ai-eval] OpenAiCompatible / x-ai/grok-4.6: 35 songs, 35 requests, estimated $0.30",
            ToneEvalConsoleLines.Artifacts(directory),
            "[ai-eval] report: " + Path.Combine(directory, "report.md"));
        var consoleFile = Path.Combine(_scratch, "console.log");
        File.WriteAllText(consoleFile, console);

        var parsed = Pwsh($$"""
            Get-ToneEvalArtifactDirectory (Get-Content -Raw '{{Escape(consoleFile)}}')
            """).Trim();

        Assert.Equal(directory, parsed);
    }

    [Fact]
    public void TheLiveEval_ActuallyPrintsThatLine()
    {
        // The line is the only way the comparison harness finds the run it paid
        // for, so its absence is a silent "every model failed". Globs the whole
        // ToneDesignLiveEvalTests* set: a file split must not make this pass
        // vacuously.
        var sources = Directory.GetFiles(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests", "Ai", "Eval"),
            "ToneDesignLiveEvalTests*.cs");
        Assert.NotEmpty(sources);

        var text = string.Concat(sources.Select(File.ReadAllText));
        Assert.Contains("ToneEvalConsoleLines.Artifacts(", text, StringComparison.Ordinal);
    }

    // ----------------------------------------------------- rows, rank, markdown

    [Fact]
    public async Task TheRanking_PutsP10First_AndSinksAFailedRun()
    {
        var good = await RealResultsAsync(FakeToneDesignBehaviour.Perfect, "good/model");
        var weak = await RealResultsAsync(FakeToneDesignBehaviour.Mediocre, "weak/model");

        var json = Pwsh($$"""
            $good = Get-Content -Raw '{{Escape(good)}}' | ConvertFrom-Json
            $weak = Get-Content -Raw '{{Escape(weak)}}' | ConvertFrom-Json
            $rows = @(
                (ConvertTo-ToneEvalCompareRow -Model 'weak/model' -Run $weak -WallSeconds 12 `
                    -InUsdPerMTok 0.5 -OutUsdPerMTok 1.5),
                (ConvertTo-ToneEvalCompareRow -Model 'dead/model' -Status 'failed' `
                    -Failure '401 Unauthorized' -WallSeconds 3),
                (ConvertTo-ToneEvalCompareRow -Model 'good/model' -Run $good -WallSeconds 20 `
                    -InUsdPerMTok 5 -OutUsdPerMTok 25)
            )
            Sort-ToneEvalCompareRow $rows | Select-Object Model, Status, P10, Mean, Warnings |
                ConvertTo-Json -Depth 4 -AsArray
            """);

        var rows = JsonDocument.Parse(json).RootElement;
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("good/model", rows[0].GetProperty("Model").GetString());
        Assert.Equal("weak/model", rows[1].GetProperty("Model").GetString());

        // A run that never happened cannot outrank one that did, whatever it is
        // called — it sinks, and it keeps its reason.
        Assert.Equal("dead/model", rows[2].GetProperty("Model").GetString());
        Assert.Equal("failed", rows[2].GetProperty("Status").GetString());

        Assert.Equal(100.0, rows[0].GetProperty("P10").GetDouble(), 1);
        Assert.True(rows[1].GetProperty("P10").GetDouble() < 100.0);
    }

    [Fact]
    public async Task TheMarkdownWriter_RanksTheTable_AndGivesAFailedRunARowWithItsReason()
    {
        var good = await RealResultsAsync(FakeToneDesignBehaviour.Perfect, "good/model");
        var weak = await RealResultsAsync(FakeToneDesignBehaviour.Mediocre, "weak/model");

        var markdown = Pwsh($$"""
            $good = Get-Content -Raw '{{Escape(good)}}' | ConvertFrom-Json
            $weak = Get-Content -Raw '{{Escape(weak)}}' | ConvertFrom-Json
            $rows = @(
                (ConvertTo-ToneEvalCompareRow -Model 'weak/model' -Run $weak -WallSeconds 12 `
                    -InUsdPerMTok 0.5 -OutUsdPerMTok 1.5),
                (ConvertTo-ToneEvalCompareRow -Model 'dead/model' -Status 'failed' `
                    -Failure 'HTTP 400: response_format json_schema is not supported' -WallSeconds 3),
                (ConvertTo-ToneEvalCompareRow -Model 'good/model' -Run $good -WallSeconds 20 `
                    -InUsdPerMTok 5 -OutUsdPerMTok 25)
            )
            Write-ToneEvalCompareMarkdown -Rows $rows -Meta @{ TimestampUtc = '20260912-120000'; BaseUrl = 'https://openrouter.ai/api' }
            """);

        Assert.Contains("# AI tone design — model comparison 20260912-120000", markdown,
            StringComparison.Ordinal);
        Assert.Contains("https://openrouter.ai/api", markdown, StringComparison.Ordinal);

        // Ranked, so the best model's row comes first…
        var bestAt = markdown.IndexOf("| 1 | `good/model`", StringComparison.Ordinal);
        var weakAt = markdown.IndexOf("| 2 | `weak/model`", StringComparison.Ordinal);
        Assert.True(bestAt > 0 && weakAt > bestAt,
            "the table is not ranked best-first:\n" + markdown);

        // …and the failure is a ROW carrying its reason, not a hole and not a crash.
        Assert.Contains("dead/model", markdown, StringComparison.Ordinal);
        Assert.Contains("json_schema is not supported", markdown, StringComparison.Ordinal);

        // The floor has to travel with the table, or a reader takes 70 for a pass.
        Assert.Contains("67.3", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APartialRun_KeepsTheNumbersItPaidFor_AndSaysItWasPartial()
    {
        // A run whose testhost came back non-zero but which DID write results.json
        // still measured most of the corpus. Throwing that away would be the
        // harness discarding data the owner paid for — so it ranks, labelled.
        var results = await RealResultsAsync(FakeToneDesignBehaviour.Perfect, "flaky/model");

        var markdown = Pwsh($$"""
            $run = Get-Content -Raw '{{Escape(results)}}' | ConvertFrom-Json
            $rows = @(ConvertTo-ToneEvalCompareRow -Model 'flaky/model' -Run $run -Status 'partial' `
                -Failure '2 of 35 requests threw' -WallSeconds 30 -InUsdPerMTok 1 -OutUsdPerMTok 2)
            Write-ToneEvalCompareMarkdown -Rows $rows
            """);

        Assert.Contains("| 1 | `flaky/model` (partial) |", markdown, StringComparison.Ordinal);
        Assert.Contains("100.0", markdown, StringComparison.Ordinal);
        Assert.Contains("2 of 35 requests threw", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFailureReason_PrefersTheLineAHumanCanActOn()
    {
        var log = Path.Combine(_scratch, "failed.log");
        File.WriteAllText(log, string.Join("\n",
            "  Determining projects to restore...",
            "[ai-eval] OpenAiCompatible / z-ai/glm-5.3-flash: 35 songs",
            "  Failed DesignsTheCorpusAndWritesAReport [2 s]",
            "  Error Message:",
            "   ToneDesignException Transport: 401 Unauthorized",
            "  Stack Trace:",
            "     at DotCharter.Tone.Ai.OpenAiCompatibleToneModel.CompleteAsync()"));

        var reason = Pwsh($$"""
            Get-ToneEvalFailureReason (Get-Content -Raw '{{Escape(log)}}')
            """).Trim();

        Assert.Contains("401", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScrubber_TakesTheKeyOutOfEveryCapturedLine()
    {
        const string key = "sk-or-v1-FAKE-EXAMPLE-0000";
        var scrubbed = Pwsh($$"""
            Remove-ToneEvalSecret -Text 'Authorization: Bearer {{key}} failed' -Secret @('{{key}}')
            """).Trim();

        Assert.DoesNotContain(key, scrubbed, StringComparison.Ordinal);
        Assert.Contains("REDACTED", scrubbed, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- the script's wiring

    [Fact]
    public void TheScript_DrivesTheEvalThroughItsOwnEnvironmentVariablesAndFilter()
    {
        var script = File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "tools", "ai-eval", "Compare-Models.ps1"));

        // Every variable the script sets is named by ToneEvalEnvironment, so a
        // rename there fails HERE rather than as thirteen skipped runs.
        foreach (var variable in new[]
                 {
                     ToneEvalEnvironment.EnableVariable, ToneEvalEnvironment.ProviderVariable,
                     ToneEvalEnvironment.ModelVariable, ToneEvalEnvironment.BaseUrlVariable,
                     ToneEvalEnvironment.KeyVariable, ToneEvalEnvironment.MaxVariable,
                     ToneEvalEnvironment.ShortVariable, ToneEvalEnvironment.AnchorVariable,
                     ToneEvalEnvironment.PriceInVariable, ToneEvalEnvironment.PriceOutVariable
                 })
        {
            Assert.Contains(variable, script, StringComparison.Ordinal);
        }

        // A '|' inside one --filter-class runs ZERO tests, so the script must pass
        // the live class alone.
        Assert.Contains("'--filter-class', '*ToneDesignLiveEvalTests'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--filter-class\", \"*A|B", script, StringComparison.Ordinal);

        // …and the prefix it greps is the one the test project prints.
        var module = File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "tools", "ai-eval", "CompareModels.psm1"));
        Assert.Contains(ToneEvalConsoleLines.ArtifactPrefix.TrimEnd().Replace("[", "\\[",
            StringComparison.Ordinal), module, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A real <c>results.json</c>, written by the real runner over the
    /// committed fixtures — so the PowerShell side is parsed against the SHIPPING
    /// schema, never against a hand-written idea of it.</summary>
    private async Task<string> RealResultsAsync(FakeToneDesignBehaviour behaviour, string label)
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var run = await ToneDesignEval.RunAsync(
            new FakeToneDesignService(behaviour, corpus), corpus,
            new ToneEvalOptions
            {
                ArtifactRoot = Path.Combine(_scratch, behaviour.ToString()),
                MaxSongs = 100,
                Label = label,
                Provider = "OpenAiCompatible"
            }, Ct);

        return Path.Combine(run.ArtifactDirectory, "results.json");
    }

    private string WriteCatalog(bool includeLarge3 = true)
    {
        var large3 = includeLarge3
            ? """
              ,{"id":"mistralai/mistral-large-2512","name":"Mistral: Mistral Large 3 2512",
                "pricing":{"prompt":"0.0000005","completion":"0.0000015"},"context_length":262144}
              """
            : "";

        var payload = $$"""
            {"data":[
              {"id":"anthropic/claude-opus-5","name":"Anthropic: Claude Opus 5",
               "pricing":{"prompt":"0.000005","completion":"0.000025"},"context_length":1000000},
              {"id":"mistralai/mistral-large","name":"Mistral Large",
               "pricing":{"prompt":"0.000002","completion":"0.000006"},"context_length":128000}
              {{large3}}
            ]}
            """;

        var path = Path.Combine(_scratch, $"models-{(includeLarge3 ? "full" : "old")}.json");
        File.WriteAllText(path, payload);
        return Escape(path);
    }

    private static string Escape(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Runs <paramref name="body" /> with the module imported, returns
    /// stdout. A non-zero exit is a failure carrying stderr; a missing
    /// <c>pwsh</c> is a SKIP.</summary>
    private string Pwsh(string body)
    {
        var module = Path.Combine(TestPaths.RepoRoot(), "tools", "ai-eval", "CompareModels.psm1");
        var script = string.Join(Environment.NewLine,
            "Set-StrictMode -Version Latest",
            "$ErrorActionPreference = 'Stop'",
            // pwsh colours its output, and an ESC (0x1B) on the front of a JSON
            // document is not JSON — which is how this harness first failed.
            "$PSStyle.OutputRendering = 'PlainText'",
            // …and UTF-8 out, or an em dash leaves pwsh best-fit-mapped to '-' by
            // the console codepage. The report is full of them.
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
            "$OutputEncoding = [Console]::OutputEncoding",
            "[System.Threading.Thread]::CurrentThread.CurrentCulture" + " = " + // a pwsh line, not a C# culture write (CultureCollectionLintTests)
            "[System.Globalization.CultureInfo]::InvariantCulture",
            $"Import-Module '{Escape(module)}' -Force -DisableNameChecking",
            body);

        var file = Path.Combine(_scratch, "case-" + Guid.NewGuid().ToString("N") + ".ps1");
        // With a BOM, and read back as UTF-8: the report is full of em dashes and
        // the box's console codepage is not UTF-8, which turned "—" into "-" the
        // first time this ran.
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var info = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", file })
            info.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("pwsh (PowerShell 7+) is not on PATH, so the comparison harness " +
                        "cannot be executed here. It is a script; running it is the only " +
                        "way to test it.");
            return "";
        }

        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "pwsh did not finish within 120 s.");
        Assert.True(process.ExitCode == 0,
            $"pwsh exited {process.ExitCode}.{Environment.NewLine}{stderr}{Environment.NewLine}{stdout}");

        return stdout;
    }
}

/// <summary>
///     The two PRICE variables the comparison sweep sets per model — pure, no
///     script, no network.
/// </summary>
public sealed class ToneEvalPriceEnvironmentTests
{
    [Fact]
    public void ThePricePair_ReachesTheReport_AndHalfAPairDoesNot()
    {
        var names = new[]
        {
            ToneEvalEnvironment.EnableVariable, ToneEvalEnvironment.ProviderVariable,
            ToneEvalEnvironment.ModelVariable, ToneEvalEnvironment.BaseUrlVariable,
            ToneEvalEnvironment.KeyVariable, ToneEvalEnvironment.AnthropicKeyVariable,
            ToneEvalEnvironment.PriceInVariable, ToneEvalEnvironment.PriceOutVariable
        };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.EnableVariable, "1");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.ProviderVariable, "openai");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.ModelVariable, "deepseek/deepseek-v4-flash");
            Environment.SetEnvironmentVariable(
                ToneEvalEnvironment.BaseUrlVariable, "https://openrouter.ai/api");

            // Nothing set: the honest Opus default, which the report prints.
            Assert.True(ToneEvalEnvironment.TryResolve(out var plain, out _));
            Assert.Null(plain!.InputUsdPerMTok);
            Assert.Equal(ToneEvalOptions.OpusInputUsdPerMTok, plain.ToOptions().InputUsdPerMTok);

            // Both halves: the model's real price, invariant-culture parsed.
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.PriceInVariable, "0.0657");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.PriceOutVariable, "0.1313");
            Assert.True(ToneEvalEnvironment.TryResolve(out var priced, out _));
            var options = priced!.ToOptions();
            Assert.Equal(0.0657, options.InputUsdPerMTok, 6);
            Assert.Equal(0.1313, options.OutputUsdPerMTok, 6);

            // Half a pair would price one side at DeepSeek and the other at Opus —
            // a number nothing could interpret. Both or neither.
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.PriceOutVariable, null);
            Assert.True(ToneEvalEnvironment.TryResolve(out var half, out _));
            Assert.Equal(ToneEvalOptions.OpusInputUsdPerMTok, half!.ToOptions().InputUsdPerMTok);

            // Garbage, a negative and a zero are all "unset", never a throw: a
            // research harness must not refuse to start over a cosmetic column.
            foreach (var bad in new[] { "not-a-price", "-1", "0" })
            {
                Environment.SetEnvironmentVariable(ToneEvalEnvironment.PriceInVariable, bad);
                Environment.SetEnvironmentVariable(ToneEvalEnvironment.PriceOutVariable, bad);
                Assert.True(ToneEvalEnvironment.TryResolve(out var broken, out _));
                Assert.Null(broken!.InputUsdPerMTok);
                Assert.Equal(ToneEvalOptions.OpusOutputUsdPerMTok,
                    broken.ToOptions().OutputUsdPerMTok);
            }

            // …and the decimal point is the INVARIANT one on a German box.
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.PriceInVariable, "2.5");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.PriceOutVariable, "10");
            Assert.True(ToneEvalEnvironment.TryResolve(out var invariant, out _));
            Assert.Equal(2.5, invariant!.InputUsdPerMTok!.Value, 6);
            Assert.Equal(10.0, invariant.OutputUsdPerMTok!.Value, 6);
            Assert.Equal("2.5", 2.5.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }
}

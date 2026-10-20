using System.Reflection;
using DotCharter.Services.Audio.StemSeparation;
using JuceSharp.StemSeparation.Onnx;
using JuceSharp.StemSeparation.Runtime;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The integrity contract on stem-model downloads, after they moved onto
///     JuceSharp (2026-09-11).
///
///     <para>
///         This app used to carry its own <c>HttpClient</c>, its own URL/hash
///         table and its own <c>.partial</c>-plus-rename dance. It had no resume,
///         no mirrors, and a hash for TWO of eleven entries — and the eleven
///         included a row claiming <c>htdemucs.onnx</c> was ~80 MB when the file
///         is 302,899,058 bytes, i.e. a "size" that was wrong by a factor of
///         four and was therefore useless as the integrity bound it was written
///         to be. All of that now comes from
///         <see cref="OnnxModelCatalog" />, whose nine entries are pinned with
///         hashes read from the Hugging Face repository API, and is fetched by
///         <c>VerifiedDownloader</c>, which resumes, fails over between mirrors
///         and verifies the COMPLETE file before the atomic rename.
///     </para>
///     <para>
///         <b>The download mechanics are no longer tested here</b>, because they
///         are no longer ours: JuceSharp's <c>VerifiedDownloaderTests</c> and
///         <c>DownloadResilienceTests</c> own the truncated-body, wrong-hash,
///         resume and mirror-failover cases against a real loopback server. What
///         is left here is the SEAM — that the two catalogues agree, that nothing
///         re-types a hash, that no second download path grew back, and that the
///         UI's progress contract still holds. Nothing below touches the network.
///     </para>
/// </summary>
public sealed class StemModelDownloadIntegrityTests
{
    // ---- The catalogue seam -------------------------------------------------

    /// <summary>
    ///     Every JuceSharp entry is offered, and every offered ONNX row is that
    ///     entry VERBATIM. A field copied by hand is a field that can drift, and
    ///     drifting is exactly what the old table did.
    /// </summary>
    [Fact]
    public void Every_JuceSharp_ONNX_entry_is_offered_and_carried_verbatim()
    {
        // The rows hosted on the app's own private releases carry an entry too, but they are
        // the app's (JuceSharp's downloader cannot sign in): see the lint below.
        var derived = StemModelCatalog.BuiltIn.Where(d => d.Onnx is not null && d.ReleaseAsset is null).ToList();

        // 7 since #254 took MrCitron's two defective htdemucs exports out of JuceSharp's table.
        Assert.True(derived.Count >= 7,
            $"Only {derived.Count} catalogue rows are derived from JuceSharp; the derivation has been " +
            "unwired and the app is back to a hand-maintained ONNX table.");
        Assert.Equal(
            OnnxModelCatalog.All.Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal),
            derived.Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal));

        foreach (var d in derived)
        {
            var entry = d.Onnx!;
            Assert.Equal(entry.FileName, d.FileName);
            Assert.Equal(entry.Urls[0], d.DownloadUrl);
            Assert.Equal(entry.Sha256, d.Sha256);
            Assert.Equal(entry.SizeBytes, d.ExactBytes);
            // ApproxBytes is a UI hint, but there is no reason for a rounded
            // second copy of a number we know exactly — and the rounded copy is
            // what was wrong by 220 MB.
            Assert.Equal(entry.SizeBytes, d.ApproxBytes);
            Assert.Equal(entry.SourceCount, d.SourceCount);
            Assert.Equal(entry.HashSource, d.HashSource);
            Assert.Equal(StemModelCatalog.MapKind(entry.Kind), d.Kind);
        }
    }

    /// <summary>
    ///     A size this table once got wrong by re-typing it. The entry it was
    ///     written for (<c>htdemucs-4s-onnx</c>, which this catalogue claimed was
    ///     ~80 MB) left the catalogue in #254; the 6-stem export's exact size
    ///     guards the same failure — a regression means somebody re-typed the table.
    /// </summary>
    [Fact]
    public void The_htdemucs_ONNX_size_is_the_real_one_not_a_retyped_one()
    {
        var d = StemModelCatalog.FindById("htdemucs-6s-onnx");
        Assert.NotNull(d);
        Assert.Equal(114_559_139, d!.ExactBytes);
        Assert.False(string.IsNullOrWhiteSpace(d.Sha256));
    }

    /// <summary>
    ///     #267: a row hosted on the app's own private releases is written in the app, not
    ///     derived — and that must not become a back door for a hand-typed table. Each one is
    ///     pinned, its descriptor carries its entry's fields verbatim, the asset it downloads IS
    ///     its file, and JuceSharp does not also list it (two sources for one id would drift).
    /// </summary>
    [Fact]
    public void Every_app_hosted_row_is_pinned_and_carries_its_own_entry_verbatim()
    {
        var hosted = StemModelCatalog.BuiltIn.Where(d => d.ReleaseAsset is not null).ToList();
        Assert.NotEmpty(hosted);
        foreach (var d in hosted)
        {
            var entry = d.Onnx;
            Assert.NotNull(entry);
            Assert.Equal(entry.Id, d.Id);
            Assert.Equal(entry.FileName, d.FileName);
            Assert.Equal(entry.FileName, d.ReleaseAsset!.AssetName);
            Assert.Equal(entry.Sha256, d.Sha256);
            Assert.Equal(entry.SizeBytes, d.ExactBytes);
            Assert.Equal(entry.SourceCount, d.SourceCount);
            Assert.Equal(StemModelCatalog.MapKind(entry.Kind), d.Kind);
            Assert.Matches("^[0-9a-f]{64}$", d.Sha256!);
            Assert.True(d.ExactBytes > 0);
            Assert.StartsWith("models-", d.ReleaseAsset.Tag, StringComparison.Ordinal);
            Assert.Null(OnnxModelCatalog.FindById(d.Id));
        }
    }

    [Fact]
    public void Every_derived_entry_is_pinned_with_a_traceable_hash_and_an_exact_size()
    {
        foreach (var d in StemModelCatalog.BuiltIn.Where(x => x.Onnx is not null))
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Sha256), $"'{d.Id}' came from JuceSharp unpinned.");
            Assert.Equal(64, d.Sha256!.Length);
            Assert.True(d.Sha256.All(Uri.IsHexDigit), $"'{d.Id}' has a non-hex character in its SHA-256.");
            Assert.Equal(d.Sha256, d.Sha256.ToLowerInvariant());
            Assert.True(d.ExactBytes > 0, $"'{d.Id}' pins a hash but no exact size.");
            Assert.Contains("read 2026", d.HashSource, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The ggml half is derived too, since JuceSharp 0.9 grew a catalogue for
    ///     it. Field by field, because a hand-copied field is a field that drifts
    ///     — and this is the half that drifted furthest: two of the three rows
    ///     shipped unpinned behind a <c>HashSource</c> asserting that a Hugging
    ///     Face DATASET repo publishes no LFS oid, which is false.
    /// </summary>
    [Fact]
    public void Every_JuceSharp_ggml_entry_is_offered_and_carried_verbatim()
    {
        var derived = StemModelCatalog.BuiltIn.Where(d => d.Ggml is not null).ToList();

        Assert.Equal(DemucsGgmlModelCatalog.All.Count, derived.Count);
        Assert.Equal(
            DemucsGgmlModelCatalog.All.Select(e => e.FileName).OrderBy(x => x, StringComparer.Ordinal),
            derived.Select(d => d.FileName).OrderBy(x => x, StringComparer.Ordinal));

        foreach (var d in derived)
        {
            var entry = d.Ggml!;
            Assert.Same(DemucsGgmlModelCatalog.FindById(entry.Id), entry);
            Assert.Equal(entry.DisplayName, d.DisplayName);
            Assert.Equal(entry.FileName, d.FileName);
            Assert.Equal(entry.Urls[0], d.DownloadUrl);
            Assert.Equal(entry.Sha256, d.Sha256);
            Assert.Equal(entry.SizeBytes, d.ExactBytes);
            Assert.Equal(entry.SizeBytes, d.ApproxBytes);
            Assert.Equal(entry.SourceCount, d.SourceCount);
            Assert.Equal(entry.HashSource, d.HashSource);
            Assert.Equal(StemModelKind.DemucsGgml, d.Kind);
            Assert.Null(d.Onnx);
            // …and it survives JuceSharp's own pre-flight on the two fields that
            // decide where bytes come from and where they land.
            DemucsGgmlModelCatalog.ValidateEntry(entry);
        }
    }

    /// <summary>
    ///     The one ggml field that is NOT derived, and why. These ids are in every
    ///     existing settings file (<c>AppConfig.HtDemucsActiveModelId</c>) and in
    ///     every cached separation's manifest, so taking JuceSharp's
    ///     <c>ggml-*</c> ids verbatim would silently un-select a user's model.
    /// </summary>
    [Theory]
    [InlineData("htdemucs-4s", "ggml-model-htdemucs-4s-f16.bin")]
    [InlineData("htdemucs-6s", "ggml-model-htdemucs-6s-f16.bin")]
    [InlineData("hdemucs-mmi-v3", "ggml-model-hdemucs_mmi-v3-f16.bin")]
    public void The_persisted_ggml_ids_are_unchanged(string id, string fileName)
    {
        var d = StemModelCatalog.FindById(id);
        Assert.NotNull(d);
        Assert.Equal(fileName, d!.FileName);
        Assert.NotNull(d.Ggml);
    }

    /// <summary>
    ///     "Unverified" has to be a STATED position. An empty provenance field is
    ///     how a pin nobody can re-check, or a gap nobody knows is a gap, survives
    ///     a review.
    /// </summary>
    [Fact]
    public void Every_builtin_says_where_its_hash_came_from_or_why_it_has_none()
    {
        var ggml = StemModelCatalog.BuiltIn.Where(d => d.Onnx is null).ToList();
        Assert.Equal(3, ggml.Count);
        Assert.All(ggml, d => Assert.Equal(StemModelKind.DemucsGgml, d.Kind));
        // All three are pinned now — the hand-written table left two of them
        // unpinned for a release.
        Assert.All(ggml, d => Assert.False(string.IsNullOrWhiteSpace(d.Sha256), $"'{d.Id}' is unpinned."));

        foreach (var d in StemModelCatalog.BuiltIn)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.HashSource),
                $"'{d.Id}' records no provenance for its hash — or for the absence of one.");
            if (string.IsNullOrWhiteSpace(d.Sha256))
                Assert.Contains("UNVERIFIED", d.HashSource, StringComparison.Ordinal);
            else
                Assert.True(d.ExactBytes > 0, $"'{d.Id}' pins a hash but no exact size.");
        }
    }

    // ---- No second download path --------------------------------------------

    /// <summary>
    ///     THE LINT. The app's stem-model code may not build an HTTP client or
    ///     stream a body itself. A private second downloader is how the first one
    ///     came to exist, and a partially-migrated path is worse than either:
    ///     the verified route and the unverified route would both look shipped.
    /// </summary>
    [Fact]
    public void The_stem_model_code_owns_no_HTTP_client_of_its_own()
    {
        foreach (var file in new[]
                 {
                     "StemModelManager.cs", "StemModelCatalog.cs", "StemModelDownloadPlan.cs",
                 })
        {
            var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Audio", "StemSeparation", file);
            Assert.True(File.Exists(path), $"{file} has moved — repoint this lint.");
            var src = StripComments(File.ReadAllText(path));

            Assert.DoesNotContain("HttpClient", src, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpMessageHandler", src, StringComparison.Ordinal);
            Assert.DoesNotContain("SocketsHttpHandler", src, StringComparison.Ordinal);
            // …and no hand-rolled staging file, which is the other half of what
            // was duplicated.
            Assert.DoesNotContain(".partial", src, StringComparison.Ordinal);
        }
    }

    /// <summary>Positively: the manager reaches the verified route. A lint that
    /// only says "no HttpClient" passes just as happily over a manager that
    /// downloads nothing at all.</summary>
    [Fact]
    public void Every_download_the_manager_performs_goes_through_the_verified_route()
    {
        var src = StripComments(File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Services", "Audio", "StemSeparation", "StemModelManager.cs")));

        // A CATALOGUE row goes back out through the catalogue it came in from —
        // ONNX through OnnxModelCatalog, ggml through DemucsGgmlModelCatalog.
        // The kind-less door is for a USER IMPORT only: the checkpoint
        // importer's loose .ckpt / .yaml, plus FileRequestFor for an imported
        // descriptor that belongs to neither catalogue.
        Assert.Equal(1, CountOccurrences(src, "OnnxModelCatalog.EnsureAsync("));
        Assert.Equal(1, CountOccurrences(src, "DemucsGgmlModelCatalog.EnsureAsync("));
        Assert.Equal(2, CountOccurrences(src, "VerifiedDownload.EnsureFileAsync("));
        // …and a row on the app's private releases goes through the signed-in, pinned
        // release download (GitHubReleaseAssetDownloader), never one of its own.
        Assert.Equal(1, CountOccurrences(src, ".DownloadVerifiedAsync("));
        Assert.Contains("StemModelDownloadPlan.FileRequestFor(", src, StringComparison.Ordinal);
        Assert.Contains("StemModelDownloadPlan.LooseFileRequest(", src, StringComparison.Ordinal);

        // …and BOTH catalogue doors state the unpinned decision at the call
        // site. The kind-less door cannot: it has no notion of a pinned entry,
        // so routing a catalogue row through it silently discards the refusal
        // rather than declining it. That is what the ggml half used to do.
        Assert.Equal(2, CountOccurrences(src, "allowUnverified:"));
    }

    /// <summary>
    ///     The refusal the ggml half gained by moving doors, end to end: an
    ///     UNPINNED ggml entry cannot be fetched unless the call site says so.
    ///     <para>
    ///         The kind-less <c>VerifiedDownload.EnsureFileAsync</c> takes a
    ///         nullable hash and fetches either way — correct for a URL the user
    ///         typed, wrong for a catalogue row, because there is then no place
    ///         where anyone DECIDED to accept an unverified model. The manager's
    ///         ggml branch passes <c>allowUnverified: ggml.Sha256 is null</c>
    ///         exactly as the ONNX branch beside it does, and what that opts into
    ///         is this throw.
    ///     </para>
    ///     <para>
    ///         All three shipping ggml rows are pinned today, so the refusal is
    ///         unreachable through the real catalogue — which is precisely why it
    ///         needs a synthetic entry to be tested at all. It fires the day a
    ///         fourth model arrives before its hash does. No network: the check
    ///         is ahead of the first request.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task An_unpinned_ggml_entry_is_refused_unless_the_call_site_allows_it()
    {
        var pinned = DemucsGgmlModelCatalog.All[0];
        var unpinned = pinned with
        {
            Id = "ggml-unpinned-probe",
            Sha256 = null,
            HashSource = "No hash: synthetic entry, this test only.",
        };

        var dir = Path.Combine(Path.GetTempPath(), "dc-ggml-refusal-" + Guid.NewGuid().ToString("N"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DemucsGgmlModelCatalog.EnsureAsync(unpinned, dir, progress: null, allowUnverified: false,
                log: null, ct: TestContext.Current.CancellationToken));
        Assert.Contains(unpinned.Id, ex.Message, StringComparison.Ordinal);

        // Nothing was fetched, and nothing was even created on the way to not
        // fetching it.
        Assert.False(Directory.Exists(dir));

        // Positively: every row the app actually ships IS pinned, so the branch
        // the manager takes for them is the verified one.
        Assert.All(DemucsGgmlModelCatalog.All, e => Assert.False(string.IsNullOrWhiteSpace(e.Sha256)));
    }

    /// <summary>
    ///     The dressing is GONE, not merely unused. <c>NearestKindLabel</c> gave a
    ///     ggml weight and a user's <c>.ckpt</c> an <c>OnnxStemModelKind</c> — the
    ///     INFERENCE DRIVER that consumes the file — purely so the download call
    ///     would compile, and said so in its own comment. A field that is a lie so
    ///     the call compiles is worse than no field: the next reader believes it.
    ///     JuceSharp 0.9 has a kind-less door, so nothing needs to lie.
    /// </summary>
    [Fact]
    public void The_nearest_kind_label_dressing_no_longer_exists()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Static | BindingFlags.Instance;

        var plan = typeof(StemModelCatalog).Assembly
                       .GetType("DotCharter.Services.Audio.StemSeparation.StemModelDownloadPlan", throwOnError: false)
                   ?? throw new InvalidOperationException("StemModelDownloadPlan has moved — repoint this test.");

        foreach (var gone in new[] { "NearestKindLabel", "EntryForLooseFile", "EntryFor" })
        {
            Assert.True(plan.GetMember(gone, all).Length == 0,
                $"StemModelDownloadPlan.{gone} is back: a non-ONNX file is being dressed as an ONNX " +
                "entry again instead of going through VerifiedDownload.EnsureFileAsync.");
        }

        // …and positively, the two kind-less doors are the surface.
        Assert.NotNull(plan.GetMethod("FileRequestFor", all));
        Assert.NotNull(plan.GetMethod("LooseFileRequest", all));
    }

    // ---- The plan (pure) ----------------------------------------------------

    [Fact]
    public void A_derived_descriptor_still_carries_JuceSharps_own_entry_mirrors_included()
    {
        var d = StemModelCatalog.FindById("uvr-mdx-net-inst-hq-3");
        Assert.NotNull(d);
        Assert.NotNull(d!.Onnx);

        // The mirror list is the thing a re-typed table loses first: this entry
        // is re-hosted twice and the two hashes agree, so a failing CDN is a
        // fallback rather than a dead download.
        Assert.True(d.Onnx!.Urls.Count >= 2, "The mirror list did not travel.");
        OnnxModelCatalog.ValidateEntry(d.Onnx);
    }

    /// <summary>
    ///     The plan still resolves a ggml row correctly — but since 2026-09-12
    ///     this is NOT the door the manager takes for one: a built-in ggml row
    ///     goes out through <c>DemucsGgmlModelCatalog.EnsureAsync</c>, and
    ///     <c>FileRequestFor</c> is the user-import path. What is asserted here is
    ///     that the fields travel, which is the same claim either way.
    /// </summary>
    [Fact]
    public void A_ggml_descriptor_plans_a_verified_fetch_with_its_own_url_size_and_pin()
    {
        var d = StemModelCatalog.FindById("htdemucs-6s");
        Assert.NotNull(d);
        Assert.Null(d!.Onnx);

        var request = StemModelDownloadPlan.FileRequestFor(d, Path.Combine("X:", "models"));

        Assert.Equal(d.DownloadUrl, request.Url.OriginalString);
        Assert.Equal(Path.Combine("X:", "models", d.FileName), request.DestinationPath);
        Assert.Equal(d.Sha256, request.Sha256);
        Assert.Equal(d.ExactBytes, request.ExpectedBytes);
        Assert.Equal(54_855_129, request.ExpectedBytes);
    }

    /// <summary>
    ///     A file name is where bytes LAND, and a custom row's comes from a
    ///     user-supplied URL or a settings file. A directory component there is an
    ///     arbitrary-path writer fed over the network, so the plan refuses it —
    ///     this used to be JuceSharp's <c>ValidateEntry</c>, reached only because
    ///     the fetch went through a catalogue.
    /// </summary>
    [Fact]
    public void A_file_name_with_a_directory_component_is_refused_never_written()
    {
        var evil = new StemModelDescriptor
        {
            Id = "evil",
            DisplayName = "evil",
            Description = "evil",
            DownloadUrl = "https://example.invalid/x.bin",
            FileName = Path.Combine("..", "..", "startup.exe"),
        };

        Assert.Throws<ArgumentException>(() =>
            StemModelDownloadPlan.FileRequestFor(evil, Path.Combine("X:", "models")));
    }

    [Fact]
    public void An_unpinned_descriptor_plans_a_null_hash_never_an_empty_string()
    {
        // "" would read to the downloader as a hash to compare against, and every
        // download would then fail its verify instead of being honestly
        // unverified.
        var custom = new StemModelDescriptor
        {
            Id = "user-import",
            DisplayName = "user-import",
            Description = "user-import",
            DownloadUrl = "https://example.invalid/model.onnx",
            FileName = "model.onnx",
            Sha256 = "   ",
        };

        Assert.Null(StemModelDownloadPlan.FileRequestFor(custom, Path.Combine("X:", "models")).Sha256);
        Assert.Null(StemModelDownloadPlan.LooseFileRequest("https://example.invalid/a.ckpt",
            Path.Combine("X:", "models", "a.ckpt")).Sha256);
    }

    [Fact]
    public void Every_builtin_survives_the_pre_flight_its_own_door_applies()
    {
        foreach (var d in StemModelCatalog.BuiltIn)
        {
            if (d.Onnx is { } onnx) OnnxModelCatalog.ValidateEntry(onnx);
            else StemModelDownloadPlan.FileRequestFor(d, Path.Combine("X:", "models"));
        }
    }

    // ---- The progress adapter -----------------------------------------------

    [Fact]
    public void Progress_maps_bytes_and_total_onto_the_UIs_contract()
    {
        var p = StemModelDownloadPlan.Adapt(new DownloadProgress(512, 2048, 0.25, Resumed: false));

        Assert.Equal(512, p.BytesRead);
        Assert.Equal(2048, p.TotalBytes);
        Assert.Equal(0.25, p.Fraction, 6);
    }

    /// <summary>
    ///     An unknown total is <c>null</c> on JuceSharp's side and its
    ///     <c>Fraction</c> is −1 for it. The UI's contract spells unknown as a
    ///     total of 0 and a fraction of 0 — letting the −1 through would drive a
    ///     progress bar backwards.
    /// </summary>
    [Fact]
    public void An_unknown_total_becomes_zero_never_minus_one()
    {
        var p = StemModelDownloadPlan.Adapt(new DownloadProgress(4096, null, -1.0, Resumed: false));

        Assert.Equal(4096, p.BytesRead);
        Assert.Equal(0, p.TotalBytes);
        Assert.Equal(0.0, p.Fraction);
    }

    [Fact]
    public void A_resumed_download_opens_part_way_along_rather_than_at_zero()
    {
        // BytesReceived already counts what the .part file contributed, so the
        // bar must not restart — that is the visible half of resume working.
        var p = StemModelDownloadPlan.Adapt(new DownloadProgress(150, 200, 0.75, Resumed: true));

        Assert.Equal(0.75, p.Fraction, 6);
    }

    // ---- helpers ------------------------------------------------------------

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Comments name the things this lint forbids, on purpose — the
    /// history of the download path is written in them.</summary>
    private static string StripComments(string src)
    {
        var lines = src.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal));
        return string.Join("\n", lines);
    }
}

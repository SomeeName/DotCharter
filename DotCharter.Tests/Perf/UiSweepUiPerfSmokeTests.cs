using System.Globalization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The headless UI sweep: builds EVERY view under
///     <c>DotCharter.UI/Views</c>, attaches each to a real <see cref="Avalonia.Controls.Window" />,
///     shows it, lays it out and interrogates the resulting tree — then writes a
///     contact sheet so the whole app can be scanned in one pass.
///     <para>
///         It exists because four classes of XAML fault in this repo build
///         perfectly clean and fail only at runtime: an unresolvable
///         <c>StaticResource</c>, a <c>StringFormat</c> handed a <c>Binding</c>,
///         a <c>RelayCommand&lt;int&gt;</c> fed a literal <c>CommandParameter</c>
///         and a view that failed to construct and rendered as a "Not Found: …"
///         placeholder. A fifth — <c>Padding="{DynamicResource SpaceXs}"</c>,
///         where <c>Space*</c> is an <c>x:Double</c> — shipped from three
///         separate changes on one day. The per-area smoke tests each catch these
///         for their own area; this one catches them in BULK, for every surface,
///         including the ones no per-area test has ever built.
///     </para>
///     <para>
///         <b>Headless throughout.</b> No window reaches the desktop. The whole
///         sweep runs inside ONE <c>Dispatch</c> — every dispatch can re-enter
///         <c>HeadlessUnitTestSession.EnsureIsolatedApplication</c> on a
///         different pool thread, and past ~60 of them the second thread to touch
///         the compositor throws.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///         session, which cannot share a testhost with the gate and is excluded from the
///         default gate. Run it by hand:
///         <c>dotnet test --filter FullyQualifiedName~UiSweep</c>.
///     </para>
///     <para>
///         In the <c>"HeadlessUI"</c> collection since 2026-08-23. It used to be
///         deliberately OUT of it, because <c>HeadlessSessionLifecycle</c>'s
///         disposal "tears the shared session down and hangs a filtered run" —
///         true of the synchronous <c>Dispose()</c>, dead since <c>303b2682</c>
///         made it <c>IAsyncDisposable</c>. Staying out was the worse bug: a
///         class outside the collection runs in PARALLEL with it and poisons
///         the process-global dispatcher for the whole run.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class UiSweepUiPerfSmokeTests(ITestOutputHelper output)
{
    /// <summary>
    ///     The sweep. One dispatch, every surface, all findings collected before
    ///     anything is asserted — a fail-fast sweep would report the first broken
    ///     view and hide the other seventy.
    ///     <para>
    ///         Only the fault classes that mean "this surface is broken in the
    ///         product" fail the run: a view that threw while being constructed
    ///         or laid out, a view that measured nothing (a view whose styles
    ///         failed still ARRANGES — it just has no size, and that is the shape
    ///         every silent XAML fault here takes), and a binding onto
    ///         <c>Command</c> that resolved to nothing, which is exactly the
    ///         button that draws and does nothing. Clipped text, overflow and
    ///         inert controls are REPORTED — they are real, but a single one is
    ///         not proof of breakage and a sweep that fails on them stops being
    ///         run.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task EverySurface_buildsAttachesLaysOutAndReports()
    {
        UiSweepReport report = null!;
        await HeadlessSessionFixture.Dispatch(() => report = UiSweepRunner.Run(),
            CancellationToken.None);

        output.WriteLine(report.Summary());

        Assert.True(report.Results.Count >= 60,
            $"the sweep only found {report.Results.Count} surfaces — the catalogue is not enumerating the app");

        // The CULTURE is part of the finding. Half of what this class catches is
        // a slot sized for English, and "needs 120.7 px in a 92 px slot" is
        // unactionable without the language that produced the string — the
        // release gate re-runs this same test under de-DE and ja-JP
        // (DOTCHARTER_HEADLESS_CULTURE), so a red run here could be any of three.
        Assert.True(report.Errors.Count == 0,
            $"{report.Errors.Count} surface(s) failed the sweep " +
            $"(UI culture: {HeadlessSessionFixture.SweptCulture}):\n  " +
            string.Join("\n  ", report.Errors.Select(f => $"{f.Surface}: {f.Kind} — {f.Detail}")) +
            "\n\n" + report.Summary());
    }

    /// <summary>
    ///     The catalogue is derived from the compiled assembly, so it cannot go
    ///     stale on its own — but a view that stops being constructible with no
    ///     arguments would silently drop OUT of it. This ties the catalogue back
    ///     to the repo: every <c>.axaml</c> under <c>Views/</c> whose root is a
    ///     view (not a resource dictionary) is either swept or excluded WITH A
    ///     REASON.
    ///     <para>
    ///         Pure source + reflection — no app session — so it is cheap and
    ///         deterministic; it lives here only because the class as a whole is
    ///         out of the default gate.
    ///     </para>
    /// </summary>
    [Fact]
    public void Catalog_coversEveryViewFileInTheRepo()
    {
        var swept = UiSweepCatalog.ViewTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var missing = UiSweepCatalog.ViewAxamlFiles()
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .Select(n => n!)
            .Where(n => !swept.Contains(n) && !UiSweepCatalog.Exclusions.ContainsKey(n))
            .ToList();

        Assert.True(missing.Count == 0,
            "These views exist in DotCharter.UI/Views but the sweep neither builds nor excludes them. " +
            "Either give them a public parameterless constructor, or add them to " +
            "UiSweepCatalog.Exclusions with a one-line reason:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    ///     Every exclusion states why. An exclusion list without reasons decays
    ///     into a list of things nobody dares delete.
    /// </summary>
    [Fact]
    public void EveryExclusion_carriesAReason()
    {
        var silent = UiSweepCatalog.Exclusions
            .Where(kv => kv.Value.Length < 20)
            .Select(kv => kv.Key)
            .ToList();

        Assert.True(silent.Count == 0,
            "These sweep exclusions have no usable reason:\n  " + string.Join("\n  ", silent));
    }

    /// <summary>
    ///     The seeding contract. A sweep of surfaces with no DataContext measures
    ///     CHROME: an <c>ItemsControl</c> with no items has no rows, so every
    ///     item template — where these faults actually hide — goes unvisited. So
    ///     the sweep is only worth its runtime if most surfaces that DECLARE a
    ///     data type actually got a real one.
    ///     <para>
    ///         Stated as a floor, not an exact count, so adding a surface does
    ///         not fail it; it exists to catch the seeder silently resolving
    ///         nothing (a renamed shell property, a container that no longer
    ///         registers the view models) which would leave the sweep green and
    ///         blind.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Sweep_seedsARealDataContextForMostSurfacesThatDeclareOne()
    {
        UiSweepReport report = null!;
        await HeadlessSessionFixture.Dispatch(() => report = UiSweepRunner.Run(),
            CancellationToken.None);

        var declared = report.Results
            .Where(r => r.DataContextSource != UiSweepDataContextSource.NotDeclared)
            .ToList();
        var seeded = declared.Count(r => r.DataContextSource != UiSweepDataContextSource.Unavailable);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{seeded}/{declared.Count} surfaces that declare an x:DataType were bound to a real instance"));
        foreach (var r in declared.Where(r => r.DataContextSource == UiSweepDataContextSource.Unavailable))
            output.WriteLine($"  unseeded: {r.Surface} wants {r.DataContextType}");

        Assert.True(declared.Count >= 40,
            $"only {declared.Count} surfaces declare a root x:DataType — the markup reader is not working");
        Assert.True(seeded * 2 >= declared.Count,
            $"only {seeded} of {declared.Count} declared data types resolved to a real instance — " +
            "the sweep would be measuring empty chrome");
    }

    /// <summary>
    ///     A real data context is not enough: a surface whose content is a LIST
    ///     shows nothing until that list holds something. An <c>ItemsControl</c>
    ///     with no items instantiates no <c>ItemTemplate</c>, so the markup where
    ///     a list surface keeps almost all of its layout is never constructed,
    ///     never styled and never measured — and every check in the sweep passes
    ///     it by having nothing to look at. The recovery dialog is the worked
    ///     example: it was reported clean while its row template, which contained
    ///     a real overlap, had never been laid out at all.
    ///     <para>
    ///         <b>Seeding and REALIZING are separate, and this asserts both.</b>
    ///         Adding an item only queues a layout invalidation for that
    ///         <c>ItemsControl</c>; it does not mark its ancestors invalid, so a
    ///         plain <c>Measure(sameSize)</c> on the root returns immediately and
    ///         the rows stay unbuilt. That regression is invisible — the sweep
    ///         stays green and reports nothing — which is exactly why the item
    ///         CONTAINER count is asserted and not just the seed count. It is
    ///         asserted in BULK because a minority of seeded lists legitimately
    ///         realize nothing: they sit in a subtree that is hidden at rest (a
    ///         collapsed HUD strip, an unselected tab), and Avalonia does not
    ///         measure one. A broken re-layout takes the whole number to zero.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Sweep_seedsEmptyListsAndActuallyLaysOutTheirRows()
    {
        UiSweepReport report = null!;
        await HeadlessSessionFixture.Dispatch(() => report = UiSweepRunner.Run(),
            CancellationToken.None);

        var seeded = report.Results.Where(r => r.SeededLists > 0).ToList();
        var realized = seeded.Where(r => r.RealizedRows > 0).ToList();
        foreach (var r in seeded)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {r.Surface}: {r.SeededLists} list(s) seeded, {r.RealizedRows} item container(s)"));
        }

        Assert.True(seeded.Count >= 15,
            $"only {seeded.Count} surfaces had an empty list seeded — the seeder has stopped finding them, " +
            "and every item template in the app is going unmeasured again");

        Assert.True(realized.Count * 2 >= seeded.Count,
            $"only {realized.Count} of {seeded.Count} seeded surfaces produced any item container at all. " +
            "Seeding a list is not enough — the rows have to be LAID OUT, and adding an item only " +
            "queues that control with the layout manager. A Measure() on an already-valid root does " +
            "not reach it; a real layout pass does.");

        Assert.True(report.Results.Sum(r => r.RealizedRows) >= 25,
            $"the whole sweep realized {report.Results.Sum(r => r.RealizedRows)} item containers — " +
            "the item templates are effectively unswept again");
    }
}

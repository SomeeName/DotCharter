using DotCharter.Services.Export.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
///     <see cref="GpExporter.ExportToBytes" /> reaches alphaTab's process-global
///     id counter (<c>Score.ResetIds()</c>), and the ids it hands out land in the
///     emitted <c>score.gpif</c>. So an export is only reproducible if nothing
///     else is exporting at the same time — and the app exports DETACHED, so a
///     `.gp` save companion can overlap a sheet render or a second export.
///     <para>
///         This is the regression test for that race. Before
///         <c>AlphaTabRuntime.RunExclusive</c> it failed reliably (2 of 40
///         identical-input export PAIRS serialized to different bytes with one
///         background exporter running); the two byte-comparison tests next door
///         — <c>Export_IsDeterministic</c> and <c>Export_IsCultureInvariant</c> —
///         were its visible symptom, failing at random under parallel suite load
///         and passing in isolation.
///     </para>
///     <para>
///         Nothing here is wall-clock: the assertion is that identical input
///         serializes to identical bytes, which is true or false regardless of
///         how fast the machine is. The loop bound only sets how many chances
///         the race gets.
///     </para>
/// </summary>
public sealed class GpExporterConcurrencyTests
{
    /// <summary>
    ///     Every thread exports this many PAIRS and checks its OWN pairs — so all
    ///     of them are both signal and contention, and the total work is BOUNDED.
    ///     <para>
    ///         The first cut of this test used one measuring loop plus background
    ///         threads spinning `while (!stop)`, and that killed the testhost: an
    ///         export allocates a whole score plus a zip buffer, the gate
    ///         serializes the threads, and the ones waiting just kept queueing
    ///         allocations. A stress test that has to be bounded by a stop flag
    ///         is unbounded in the only dimension that matters. These numbers are
    ///         deliberately modest — the guard that CANNOT miss is the source
    ///         lint in <see cref="GpExporterExclusivityLintTests" />; this one is
    ///         the behavioural corroboration.
    ///     </para>
    /// </summary>
    private const int Pairs = 40;

    private const int Exporters = 3;

    private static List<BeatMarker> Beats44(int count, double bpm = 120.0)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 60_000.0 / bpm,
                Bpm = bpm,
                BeatsInMeasure = 4,
                NoteDenominator = 4,
                IsMeasure = i % 4 == 0
            });
        }

        return beats;
    }

    private static (Arrangement Arrangement, ChartLevel Level, List<BeatMarker> Beats) Input()
    {
        var arrangement = new Arrangement { Name = "Lead Test", StringCount = 6 };
        arrangement.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0, new Fraction(0, 12)),
            StringIndex = 0,
            Fret = 3,
            Sustain = new FractionalPosition(1, new Fraction(0, 12))
        });
        return (arrangement, level, Beats44(4));
    }

    /// <summary>The .gp container embeds zip timestamps, so the comparison is on
    /// the score document itself — same rule as <c>Export_IsDeterministic</c>.</summary>
    private static string Gpif(byte[] gp)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(gp));
        var entry = archive.Entries.Single(e =>
            e.FullName.EndsWith("score.gpif", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void AnOverlappingExport_CannotChangeWhatThisOneSerializes()
    {
        var mismatches = 0;
        Exception? failure = null;

        // Each thread is a detached save companion / sheet render exporting at
        // the same time as the others. Each checks its own pairs, so every
        // thread is both the thing under test and the contention for the rest.
        var threads = new List<Thread>();
        for (var t = 0; t < Exporters; t++)
        {
            var thread = new Thread(() =>
            {
                var (arrangement, level, beats) = Input();
                try
                {
                    for (var i = 0; i < Pairs; i++)
                    {
                        var first = Gpif(GpExporter.ExportToBytes(arrangement, level, beats, "T", "A"));
                        var second = Gpif(GpExporter.ExportToBytes(arrangement, level, beats, "T", "A"));
                        if (first != second) Interlocked.Increment(ref mismatches);
                    }
                }
                catch (Exception ex)
                {
                    // A torn model surfaces as an exception as readily as a
                    // wrong byte, so a throw is the same finding.
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
            })
            { IsBackground = true };
            threads.Add(thread);
            thread.Start();
        }

        foreach (var thread in threads) thread.Join();

        Assert.Null(failure);
        Assert.True(mismatches == 0,
            $"{mismatches} of {Exporters * Pairs} identical-input export pairs serialized differently " +
            "while other exporters ran — GpExporter has lost its exclusive hold on alphaTab's global ids");
    }
}

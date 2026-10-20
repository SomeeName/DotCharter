namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
///     Serializes every test that opens a REAL native JUCE engine, or that flips
///     the process-global null-device guards those engines read at start-up.
///     <para>
///         <c>DCA_ENABLE_NULL_DEVICE</c> and <c>DCA_NULL_DEVICE_MANUAL</c> are
///         read natively at engine construction (via <c>GetEnvironmentVariable</c>,
///         not the CRT's cached <c>getenv</c>), so they must be live BEFORE the
///         engine is built — and an environment variable is per-PROCESS, not per
///         test. <c>NamAmpFaceNativeTests.NullDeviceScope</c> sets both for the
///         lifetime of its engine and clears them on dispose, while eight
///         DotCharter classes construct real engines with no serialization at
///         all. A sibling that opens an engine while the var reads <c>1</c>
///         silently gets the NULL device instead of a real one; a sibling whose
///         open straddles <c>Dispose</c> gets a real device where it wanted the
///         null one. Either way the failure is non-deterministic and reads as
///         "the native tests are environment-flaky", which every guide in this
///         repo already tells the next agent to shrug off.
///     </para>
///     <para>
///         <c>JuceSharp.Tests</c> has carried a <c>NullDevice</c> collection for
///         exactly this since the RT work
///         (<c>JuceSharp/docs/agents/rt-safety-and-tests.md</c>: <i>"Every test
///         that SETS the guard lives in the xunit <c>[Collection("NullDevice")]</c>
///         — the var is process-wide"</i>). This is the DotCharter half, which
///         did not exist. Membership is enforced by
///         <c>Infrastructure/NativeEngineCollectionLintTests</c>.
///     </para>
///     <para>
///         <b>Serialized WITHIN, parallel WITHOUT.</b> One collection is one
///         lane, so no two members ever overlap, and that is all the env vars
///         need: the lint keeps every engine opener and every guard writer IN
///         here. It used to also carry <c>DisableParallelization</c>, which runs
///         a collection alone AFTER every parallel one has finished. That bought
///         nothing (nothing outside opens an engine) and cost ~70 s of a ~180 s
///         gate, because this lane, 40 s of it <c>ToneLowEndResponseTests</c>,
///         was the gate's whole serialized tail. A member's waits on
///         native work are therefore deadlock detectors (60 s), never budgets:
///         the lane now shares the box with the parallel phase.
///     </para>
/// </summary>
[CollectionDefinition("NativeEngine")]
public class NativeEngineCollection;
